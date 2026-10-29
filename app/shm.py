"""Writer side of the shared memory the fbtcal driver reads.

Mirrors driver/src/shared_pose.h - change both together. The driver only uses the poses
while the heartbeat is younger than 250 ms, so write() has to be called continuously
while an override should stay on; if this process stops, the real poses come back.
"""
from __future__ import annotations

import ctypes
import mmap
import struct

NAME = "Local\\fbtcal_pose_override_v1"
TEST_NAME = "Local\\fbtcal_pose_override_test"  # tests: never write into a live SteamVR's mapping
MAGIC = 0x4C414346  # "FCAL"
VERSION = 1
MAX_ENTRIES = 16
HEADER = struct.Struct("<IIIIQ")    # magic, version, seq, count, heartbeat_ms
ENTRY = struct.Struct("<II3d4d")    # device, active, pos[3], rot[4] (w, x, y, z)
SIZE = HEADER.size + MAX_ENTRIES * ENTRY.size
SEQ_OFFSET = 8

_kernel32 = ctypes.windll.kernel32
_kernel32.GetTickCount64.restype = ctypes.c_uint64


def now_ms() -> int:
    return int(_kernel32.GetTickCount64())


def mapping_exists() -> bool:
    """True if the mapping already exists, i.e. SteamVR has loaded the fbtcal driver
    (or another app instance is running). Call it before OverrideWriter creates one."""
    _kernel32.OpenFileMappingW.restype = ctypes.c_void_p
    h = _kernel32.OpenFileMappingW(0x0004, False, NAME)  # FILE_MAP_READ
    if h:
        _kernel32.CloseHandle(ctypes.c_void_p(h))
    return bool(h)


class OverrideWriter:
    def __init__(self, name=NAME):
        # Opens the driver's mapping, or creates it if SteamVR isn't running yet.
        self.mm = mmap.mmap(-1, SIZE, tagname=name)

    def _seq(self) -> int:
        return struct.unpack_from("<I", self.mm, SEQ_OFFSET)[0]

    def write(self, entries, heartbeat_ms=None):
        """entries: list of (device_index, pos (3,), quat (w, x, y, z)). Empty = no override."""
        entries = list(entries)[:MAX_ENTRIES]
        seq = self._seq()
        if seq & 1:
            seq += 1
        struct.pack_into("<I", self.mm, SEQ_OFFSET, seq + 1)  # odd: mid-update
        for i, (dev, pos, quat) in enumerate(entries):
            ENTRY.pack_into(self.mm, HEADER.size + i * ENTRY.size, int(dev), 1, *map(float, pos), *map(float, quat))
        hb = now_ms() if heartbeat_ms is None else heartbeat_ms
        HEADER.pack_into(self.mm, 0, MAGIC, VERSION, seq + 1, len(entries), hb)
        struct.pack_into("<I", self.mm, SEQ_OFFSET, seq + 2)  # even: consistent

    def clear(self):
        self.write([], heartbeat_ms=0)

    def close(self):
        try:
            self.clear()
        finally:
            self.mm.close()
