"""Follows VRChat's output log and reports when FBT calibration starts and ends.

Needs VRChat's `--enable-ik-debug-logging` launch option; the lines look like:
  2026.10.25 20:45:00 Debug      -  [VRCTrackingManager] [IK Debug Log] Preparing for calibration...
  2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrating trackers...
"""
from __future__ import annotations

import os
from pathlib import Path

DEFAULT_DIR = Path(os.environ.get("USERPROFILE", "~")).expanduser() / "AppData/LocalLow/VRChat/VRChat"
IK_TAG = "[IK Debug Log]"
START = "[IK Debug Log] Preparing for calibration..."
END = ("[IK Debug Log] Calibrating trackers...", "[IK Debug Log] Applying calibration to avatar...")


def newest_log(folder: Path):
    logs = sorted(folder.glob("output_log_*.txt"))  # names sort by date
    return logs[-1] if logs else None


class VrcLogWatcher:
    """poll() returns the new events since the last call: "start", "end", "newlog"."""

    def __init__(self, folder: Path = DEFAULT_DIR):
        self.folder = Path(folder)
        self.path = None
        self.offset = 0
        self.partial = b""
        self.ik_debug_seen = False
        self._attach(newest_log(self.folder), from_end=True)

    def _attach(self, path, from_end):
        self.path, self.partial = path, b""
        self.offset = path.stat().st_size if (path and from_end) else 0
        self.ik_debug_seen = False
        if path:
            with open(path, "rb") as fh:  # only to know whether the launch option is on
                self.ik_debug_seen = IK_TAG.encode() in fh.read(self.offset or None)

    def poll(self):
        events = []
        latest = newest_log(self.folder)
        if latest is not None and latest != self.path:
            self._attach(latest, from_end=False)  # VRChat restarted: read the new log from its start
            events.append("newlog")
        if self.path is None:
            return events
        try:
            size = self.path.stat().st_size
        except OSError:
            return events
        if size < self.offset:  # truncated
            self.offset, self.partial = 0, b""
        if size == self.offset:
            return events
        with open(self.path, "rb") as fh:
            fh.seek(self.offset)
            data = fh.read(size - self.offset)
        self.offset += len(data)
        lines = (self.partial + data).split(b"\n")
        self.partial = lines.pop()
        for raw in lines:
            line = raw.decode("utf-8", "replace")
            if IK_TAG in line:
                self.ik_debug_seen = True
            if START in line:
                events.append("start")
            elif any(e in line for e in END):
                events.append("end")
        return events
