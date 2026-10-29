#pragma once
// Shared memory between the control app (writer) and the driver (reader).
//
// The app writes override poses in the SteamVR raw universe. The driver swaps them in for
// the listed device indices, but only while the heartbeat is fresh: if the app stops or
// crashes, every device falls back to its real pose within kHeartbeatTimeoutMs.
// app/shm.py mirrors this layout - change both together.
#include <cstdint>

namespace fbtcal {

constexpr wchar_t kShmName[] = L"Local\\fbtcal_pose_override_v1";
constexpr uint32_t kMagic = 0x4C414346;  // "FCAL"
constexpr uint32_t kVersion = 1;
constexpr uint32_t kMaxEntries = 16;
constexpr uint64_t kHeartbeatTimeoutMs = 250;

#pragma pack(push, 1)
struct ShmEntry {
    uint32_t device;  // OpenVR device index
    uint32_t active;  // 1 = replace this device's pose
    double pos[3];    // raw universe, metres
    double rot[4];    // quaternion w, x, y, z
};

struct ShmBlock {
    uint32_t magic;
    uint32_t version;
    volatile uint32_t seq;  // seqlock: odd while the writer is mid-update
    uint32_t count;
    uint64_t heartbeat_ms;  // GetTickCount64() at the last write
    ShmEntry entries[kMaxEntries];
};
#pragma pack(pop)

static_assert(sizeof(ShmEntry) == 64, "ShmEntry layout is shared with app/shm.py");
static_assert(sizeof(ShmBlock) == 24 + kMaxEntries * 64, "ShmBlock layout is shared with app/shm.py");

}  // namespace fbtcal
