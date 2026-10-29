// fbtcal SteamVR driver: swaps in "ideal pose" tracker poses during VRChat FBT calibration.
//
// It adds no devices of its own. It hooks IVRServerDriverHost::TrackedDevicePoseUpdated
// (the same trick OpenVR-SpaceCalibrator uses), so every other driver's pose updates pass
// through it. While the control app keeps a fresh heartbeat in shared memory, the listed
// devices get the app's pose instead of their own; otherwise everything passes untouched.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include <MinHook.h>
#include <openvr_driver.h>

#include "shared_pose.h"

namespace {

void Log(const char* fmt, ...) {
    vr::IVRDriverLog* log = vr::VRDriverLog();
    if (!log) return;
    char msg[400];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(msg, sizeof msg, fmt, ap);
    va_end(ap);
    char line[410];
    snprintf(line, sizeof line, "%s\n", msg);  // SteamVR already prefixes the driver name
    log->Log(line);
}

// ---------------------------------------------------------------- shared memory

class OverrideSource {
public:
    bool Open() {
        // The driver creates the mapping (zero-filled = no override); the app opens it.
        // Tests set FBTCAL_SHM_NAME so they never write into a live SteamVR's mapping.
        wchar_t name[128];
        const DWORD n = GetEnvironmentVariableW(L"FBTCAL_SHM_NAME", name, 128);
        m_map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(fbtcal::ShmBlock),
                                   n > 0 && n < 128 ? name : fbtcal::kShmName);
        if (!m_map) return false;
        m_block = static_cast<const fbtcal::ShmBlock*>(
            MapViewOfFile(m_map, FILE_MAP_READ, 0, 0, sizeof(fbtcal::ShmBlock)));
        return m_block != nullptr;
    }

    // Copies a consistent snapshot. False when nothing should be overridden: no app or stale
    // heartbeat. If the writer is mid-update, this thread's last consistent snapshot stands in:
    // falling back to the real pose for that one update made the trackers flicker (~5x a second
    // with the Python writer).
    bool Snapshot(fbtcal::ShmBlock& out) const {
        thread_local fbtcal::ShmBlock last;
        thread_local bool haveLast = false;
        const fbtcal::ShmBlock* b = m_block;
        if (!b || b->magic != fbtcal::kMagic || b->version != fbtcal::kVersion) return false;
        bool consistent = false;
        for (int tries = 0; tries < 64 && !consistent; ++tries) {
            const uint32_t s1 = b->seq;
            if (s1 & 1) {
                YieldProcessor();
                continue;
            }
            MemoryBarrier();
            std::memcpy(&out, const_cast<const fbtcal::ShmBlock*>(b), sizeof out);
            MemoryBarrier();
            consistent = b->seq == s1;
        }
        if (consistent) {
            last = out;
            haveLast = true;
        } else if (haveLast) {
            out = last;
        } else {
            return false;
        }
        const int64_t age = static_cast<int64_t>(GetTickCount64() - out.heartbeat_ms);
        return out.count > 0 && age >= 0 && age <= static_cast<int64_t>(fbtcal::kHeartbeatTimeoutMs);
    }

    bool IsOpen() const { return m_block != nullptr; }

private:
    HANDLE m_map = nullptr;
    const fbtcal::ShmBlock* m_block = nullptr;
};

OverrideSource g_source;
std::atomic<bool> g_wasActive{false};

bool ApplyOverride(uint32_t device, const vr::DriverPose_t& in, uint32_t size, vr::DriverPose_t& out) {
    // A different struct size means a driver built against another layout: leave it alone.
    if (size != sizeof(vr::DriverPose_t)) return false;
    fbtcal::ShmBlock snap;
    const bool active = g_source.Snapshot(snap);
    if (active != g_wasActive.load(std::memory_order_relaxed) && active != g_wasActive.exchange(active))
        Log(active ? "override ON (%u device(s))" : "override OFF", active ? snap.count : 0u);
    if (!active) return false;

    const uint32_t n = snap.count < fbtcal::kMaxEntries ? snap.count : fbtcal::kMaxEntries;
    for (uint32_t i = 0; i < n; ++i) {
        const fbtcal::ShmEntry& e = snap.entries[i];
        if (e.device != device || !e.active) continue;
        out = in;
        out.poseTimeOffset = 0;
        out.qWorldFromDriverRotation = {1, 0, 0, 0};
        out.qDriverFromHeadRotation = {1, 0, 0, 0};
        out.qRotation = {e.rot[0], e.rot[1], e.rot[2], e.rot[3]};
        for (int k = 0; k < 3; ++k) {
            out.vecWorldFromDriverTranslation[k] = 0;
            out.vecDriverFromHeadTranslation[k] = 0;
            out.vecPosition[k] = e.pos[k];
            out.vecVelocity[k] = 0;
            out.vecAcceleration[k] = 0;
            out.vecAngularVelocity[k] = 0;
            out.vecAngularAcceleration[k] = 0;
        }
        out.result = vr::TrackingResult_Running_OK;
        out.poseIsValid = true;
        out.deviceIsConnected = true;
        return true;
    }
    return false;
}

// ---------------------------------------------------------------- hooks

using PoseUpdatedFn = void (*)(vr::IVRServerDriverHost*, uint32_t, const vr::DriverPose_t&, uint32_t);

constexpr int kMaxHooks = 4;
PoseUpdatedFn g_original[kMaxHooks] = {};
void* g_target[kMaxHooks] = {};
int g_hookCount = 0;

// One detour per hooked function, so each knows which original to call.
template <int Slot>
void DetourPoseUpdated(vr::IVRServerDriverHost* self, uint32_t device, const vr::DriverPose_t& pose,
                       uint32_t size) {
    vr::DriverPose_t patched;
    if (ApplyOverride(device, pose, size, patched))
        g_original[Slot](self, device, patched, size);
    else
        g_original[Slot](self, device, pose, size);
}

void* const kDetours[kMaxHooks] = {
    reinterpret_cast<void*>(&DetourPoseUpdated<0>),
    reinterpret_cast<void*>(&DetourPoseUpdated<1>),
    reinterpret_cast<void*>(&DetourPoseUpdated<2>),
    reinterpret_cast<void*>(&DetourPoseUpdated<3>),
};

bool HookPoseUpdated(void* iface, const char* version) {
    // vtable slot 1 = TrackedDevicePoseUpdated in IVRServerDriverHost_005 and _006.
    void* fn = (*reinterpret_cast<void***>(iface))[1];
    for (int i = 0; i < g_hookCount; ++i) {
        if (g_target[i] == fn) {
            Log("%s shares an already hooked function", version);
            return true;
        }
    }
    if (g_hookCount == kMaxHooks) return false;
    const int slot = g_hookCount;
    MH_STATUS st = MH_CreateHook(fn, kDetours[slot], reinterpret_cast<void**>(&g_original[slot]));
    if (st != MH_OK) {
        Log("hooking %s failed: %s", version, MH_StatusToString(st));
        return false;
    }
    st = MH_EnableHook(fn);
    if (st != MH_OK) {
        MH_RemoveHook(fn);
        Log("enabling the %s hook failed: %s", version, MH_StatusToString(st));
        return false;
    }
    g_target[slot] = fn;
    ++g_hookCount;
    Log("hooked %s::TrackedDevicePoseUpdated", version);
    return true;
}

// ---------------------------------------------------------------- provider

class Provider : public vr::IServerTrackedDeviceProvider {
public:
    vr::EVRInitError Init(vr::IVRDriverContext* ctx) override {
        VR_INIT_SERVER_DRIVER_CONTEXT(ctx);
        if (!g_source.Open()) {
            Log("could not create the shared memory (error %lu), staying inactive", GetLastError());
            return vr::VRInitError_None;
        }
        const MH_STATUS st = MH_Initialize();
        if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
            Log("MinHook init failed: %s, staying inactive", MH_StatusToString(st));
            return vr::VRInitError_None;
        }
        // Any driver using one of these interface versions now goes through the detour,
        // whether it loaded before or after us: the hook patches the function itself.
        for (const char* version : {"IVRServerDriverHost_005", "IVRServerDriverHost_006"}) {
            vr::EVRInitError err = vr::VRInitError_None;
            void* iface = ctx->GetGenericInterface(version, &err);
            if (iface && err == vr::VRInitError_None) HookPoseUpdated(iface, version);
        }
        Log("ready, %d hook(s)", g_hookCount);
        return vr::VRInitError_None;
    }

    void Cleanup() override {
        MH_DisableHook(MH_ALL_HOOKS);
        // No MH_Uninitialize and no unmapping: another thread may still be on its way out
        // of a detour, and the process is shutting down anyway.
        VR_CLEANUP_SERVER_DRIVER_CONTEXT();
    }

    const char* const* GetInterfaceVersions() override { return vr::k_InterfaceVersions; }
    void RunFrame() override {}
    bool ShouldBlockStandbyMode() override { return false; }
    void EnterStandby() override {}
    void LeaveStandby() override {}
};

Provider g_provider;

}  // namespace

extern "C" __declspec(dllexport) void* HmdDriverFactory(const char* interfaceName, int* returnCode) {
    if (std::strcmp(interfaceName, vr::IServerTrackedDeviceProvider_Version) == 0) return &g_provider;
    if (returnCode) *returnCode = vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
