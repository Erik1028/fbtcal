// Offline test for driver_fbtcal.dll: loads it against a fake vrserver host, so the hook
// can be checked without SteamVR.
//
//   hooktest <dll>                      built-in scenarios, writes the shared memory itself
//   hooktest <dll> --external <device> <x> <y> <z> <qw> <qx> <qy> <qz>
//                                       expects app/shm.py to have written that override
// Exit code 0 = all checks passed.
#include <windows.h>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <openvr_driver.h>

#include "shared_pose.h"

namespace {

int g_failures = 0;

void Check(bool ok, const char* what) {
    std::printf("  [%s] %s\n", ok ? "ok" : "FAIL", what);
    if (!ok) ++g_failures;
}

class FakeLog : public vr::IVRDriverLog {
public:
    void Log(const char* msg) override { std::printf("    driver log: %s", msg); }
};

class FakeHost : public vr::IVRServerDriverHost {
public:
    vr::DriverPose_t last{};
    uint32_t lastDevice = ~0u;
    int calls = 0;

    bool TrackedDeviceAdded(const char*, vr::ETrackedDeviceClass, vr::ITrackedDeviceServerDriver*) override {
        return false;
    }
    __declspec(noinline) void TrackedDevicePoseUpdated(uint32_t device, const vr::DriverPose_t& pose,
                                                       uint32_t) override {
        last = pose;
        lastDevice = device;
        ++calls;
    }
    void VsyncEvent(double) override {}
    void VendorSpecificEvent(uint32_t, vr::EVREventType, const vr::VREvent_Data_t&, double) override {}
    bool IsExiting() override { return false; }
    bool PollNextEvent(vr::VREvent_t*, uint32_t) override { return false; }
    void GetRawTrackedDevicePoses(float, vr::TrackedDevicePose_t*, uint32_t) override {}
    void RequestRestart(const char*, const char*, const char*, const char*) override {}
    uint32_t GetFrameTimings(vr::Compositor_FrameTiming*, uint32_t) override { return 0; }
    void SetDisplayEyeToHead(uint32_t, const vr::HmdMatrix34_t&, const vr::HmdMatrix34_t&) override {}
    void SetDisplayProjectionRaw(uint32_t, const vr::HmdRect2_t&, const vr::HmdRect2_t&) override {}
    void SetRecommendedRenderTargetSize(uint32_t, uint32_t, uint32_t) override {}
};

FakeHost g_host;
FakeLog g_log;
char g_dummy[4096];  // stands in for interfaces the driver must find but never calls

class FakeContext : public vr::IVRDriverContext {
public:
    void* GetGenericInterface(const char* version, vr::EVRInitError* err) override {
        if (err) *err = vr::VRInitError_None;
        if (std::strncmp(version, "IVRServerDriverHost_", 20) == 0) return &g_host;
        if (std::strncmp(version, "IVRDriverLog_", 13) == 0) return &g_log;
        return g_dummy;
    }
    vr::DriverHandle_t GetDriverHandle() override { return 1; }
};

vr::DriverPose_t SamplePose() {
    vr::DriverPose_t p{};
    p.qWorldFromDriverRotation = {0.7071, 0, 0.7071, 0};
    p.vecWorldFromDriverTranslation[1] = 0.5;
    p.qDriverFromHeadRotation = {1, 0, 0, 0};
    p.vecPosition[0] = 1.25;
    p.vecPosition[1] = 0.9;
    p.vecPosition[2] = -0.3;
    p.vecVelocity[2] = 0.4;
    p.qRotation = {0.9, 0.1, 0.3, 0.2};
    p.result = vr::TrackingResult_Running_OK;
    p.poseIsValid = true;
    p.deviceIsConnected = true;
    return p;
}

bool SamePose(const vr::DriverPose_t& a, const vr::DriverPose_t& b) {
    return std::memcmp(&a, &b, sizeof a) == 0;
}

bool IsOverride(const vr::DriverPose_t& p, const double pos[3], const double rot[4]) {
    const double eps = 1e-12;
    bool ok = p.qWorldFromDriverRotation.w == 1 && p.qWorldFromDriverRotation.x == 0 &&
              p.qWorldFromDriverRotation.y == 0 && p.qWorldFromDriverRotation.z == 0 &&
              p.qRotation.w == rot[0] && p.qRotation.x == rot[1] && p.qRotation.y == rot[2] &&
              p.qRotation.z == rot[3] && p.poseIsValid && p.result == vr::TrackingResult_Running_OK;
    for (int k = 0; k < 3; ++k)
        ok = ok && std::fabs(p.vecPosition[k] - pos[k]) < eps && p.vecWorldFromDriverTranslation[k] == 0 &&
             p.vecVelocity[k] == 0;
    return ok;
}

// Sends one update the way a driver would: through the interface pointer from the context.
void Send(vr::IVRServerDriverHost* host, uint32_t device, const vr::DriverPose_t& pose,
          uint32_t size = sizeof(vr::DriverPose_t)) {
    host->TrackedDevicePoseUpdated(device, pose, size);
}

class Writer {
public:
    fbtcal::ShmBlock* b = nullptr;
    bool Open(const wchar_t* name) {
        HANDLE m = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                                      sizeof(fbtcal::ShmBlock), name);
        if (!m) return false;
        b = static_cast<fbtcal::ShmBlock*>(MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(fbtcal::ShmBlock)));
        return b != nullptr;
    }
    void Write(uint32_t device, uint32_t active, const double pos[3], const double rot[4], int64_t ageMs = 0) {
        b->seq = b->seq + 1;
        b->magic = fbtcal::kMagic;
        b->version = fbtcal::kVersion;
        b->count = 1;
        b->entries[0] = {device, active, {pos[0], pos[1], pos[2]}, {rot[0], rot[1], rot[2], rot[3]}};
        b->heartbeat_ms = GetTickCount64() - ageMs;
        b->seq = b->seq + 1;
    }
};

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: hooktest <driver_fbtcal.dll> [--external device x y z qw qx qy qz]\n");
        return 2;
    }
    const bool external = argc == 11 && std::strcmp(argv[2], "--external") == 0;

    // Never touch a live SteamVR's mapping: the driver under test reads FBTCAL_SHM_NAME, and the
    // app tests (--external) set the same name for their writer.
    wchar_t shmName[128];
    const DWORD n = GetEnvironmentVariableW(L"FBTCAL_SHM_NAME", shmName, 128);
    if (n == 0 || n >= 128) {
        wcscpy_s(shmName, L"Local\\fbtcal_pose_override_test");
        SetEnvironmentVariableW(L"FBTCAL_SHM_NAME", shmName);
    }

    Writer writer;
    if (!external && !writer.Open(shmName)) {
        std::printf("cannot create shared memory\n");
        return 2;
    }

    HMODULE dll = LoadLibraryA(argv[1]);
    if (!dll) {
        std::printf("cannot load %s (error %lu)\n", argv[1], GetLastError());
        return 2;
    }
    using Factory = void* (*)(const char*, int*);
    auto factory = reinterpret_cast<Factory>(GetProcAddress(dll, "HmdDriverFactory"));
    Check(factory != nullptr, "exports HmdDriverFactory");
    if (!factory) return 1;
    int rc = 0;
    Check(factory("IVRWatchdogProvider_001", &rc) == nullptr, "refuses other interfaces");
    auto* provider = static_cast<vr::IServerTrackedDeviceProvider*>(factory(vr::IServerTrackedDeviceProvider_Version, &rc));
    Check(provider != nullptr, "returns a server provider");
    if (!provider) return 1;

    FakeContext ctx;
    Check(provider->Init(&ctx) == vr::VRInitError_None, "Init succeeds");
    auto* host = static_cast<vr::IVRServerDriverHost*>(ctx.GetGenericInterface("IVRServerDriverHost_006", nullptr));
    const vr::DriverPose_t real = SamplePose();

    if (external) {
        const uint32_t device = static_cast<uint32_t>(std::atoi(argv[3]));
        double pos[3], rot[4];
        for (int k = 0; k < 3; ++k) pos[k] = std::atof(argv[4 + k]);
        for (int k = 0; k < 4; ++k) rot[k] = std::atof(argv[7 + k]);
        Send(host, device, real);
        Check(g_host.lastDevice == device && IsOverride(g_host.last, pos, rot), "app-written override applied");
        Send(host, device + 1, real);
        Check(SamePose(g_host.last, real), "other device untouched");
        std::printf("%s\n", g_failures ? "FAILED" : "PASSED");
        return g_failures ? 1 : 0;
    }

    const double pos[3] = {0.1, -0.65, 0.05};
    const double rot[4] = {0.5, 0.5, -0.5, 0.5};

    Send(host, 7, real);
    Check(g_host.calls == 1 && SamePose(g_host.last, real), "no app: pose passes through unchanged");

    writer.Write(7, 1, pos, rot);
    Send(host, 7, real);
    Check(IsOverride(g_host.last, pos, rot), "fresh override replaces device 7");
    Send(host, 3, real);
    Check(SamePose(g_host.last, real), "device 3 untouched");
    Send(host, 7, real, sizeof(vr::DriverPose_t) - 8);
    Check(SamePose(g_host.last, real), "unknown struct size untouched");

    writer.Write(7, 1, pos, rot, 1000);
    Send(host, 7, real);
    Check(SamePose(g_host.last, real), "stale heartbeat: real pose again");

    writer.Write(7, 0, pos, rot);
    Send(host, 7, real);
    Check(SamePose(g_host.last, real), "inactive entry untouched");

    writer.Write(7, 1, pos, rot);
    Send(host, 7, real);  // a consistent read: this thread caches it
    Check(IsOverride(g_host.last, pos, rot), "override again");
    writer.b->seq = writer.b->seq + 1;  // writer "mid-update"
    Send(host, 7, real);
    Check(IsOverride(g_host.last, pos, rot), "writer mid-update: keeps the last consistent override (no flicker)");
    Sleep(static_cast<DWORD>(fbtcal::kHeartbeatTimeoutMs + 50));
    Send(host, 7, real);
    Check(SamePose(g_host.last, real), "writer stuck mid-update: real pose once the cached heartbeat is stale");
    writer.b->seq = writer.b->seq + 1;  // update completes
    writer.Write(7, 1, pos, rot);
    Send(host, 7, real);
    Check(IsOverride(g_host.last, pos, rot), "after the update completes: override again");

    provider->Cleanup();
    Send(host, 7, real);
    Check(SamePose(g_host.last, real), "after Cleanup: hook removed");
    Check(g_host.calls == 11, "every update reached the host exactly once");

    std::printf("%s\n", g_failures ? "FAILED" : "PASSED");
    return g_failures ? 1 : 0;
}
