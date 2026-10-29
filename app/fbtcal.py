"""FBTcal - VRChat FBT calibration without posing (SlimeVR).

While VRChat calibrates, the FBTcal SteamVR driver shows the trackers in the pose they
would have if you stood straight under your headset. So you can calibrate sitting or
lying down, and VRChat still gets clean offsets.

  python fbtcal.py devices          list the SteamVR devices
  python fbtcal.py pose             show the standing pose in use (captured, or from SlimeVR's proportions)
  python fbtcal.py capture          record the standing pose once (stand straight, right after a SlimeVR full reset)
  python fbtcal.py run              automatic: ideal pose while VRChat calibrates (watches the VRChat log)
  python fbtcal.py hold [seconds]   manual: ideal pose for N seconds
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

import numpy as np

try:
    import openvr
    from openvr.error_code import OpenVRError
except ImportError:
    sys.exit("pyopenvr is missing. Install:  pip install openvr numpy")

import shm
import slimevr
from posemath import (adjust_pose, ideal_pose, pitch_deg, quat_from_matrix, rest_offset, slimevr_rest_pose, stance_m,
                      symmetrize, toe_out_deg)
from vrclog import VrcLogWatcher

PROFILE = Path(os.environ.get("APPDATA", ".")) / "fbtcal" / "profile.json"
SETTINGS = PROFILE.with_name("settings.json")  # shared with fbtcal.exe
CALIBRATION_TIMEOUT_S = 90   # failsafe if VRChat never reports the end of calibration
END_GRACE_S = 0.3            # keep the ideal pose a moment after VRChat took the calibration
OVERRIDE_HZ = 200
STANDING_MAX_SIDEWAYS_M = 0.30   # capture: a standing body hangs under the head
STANDING_MIN_DROP_M = 0.15
CLASSES = {
    openvr.TrackedDeviceClass_HMD: "hmd",
    openvr.TrackedDeviceClass_Controller: "controller",
    openvr.TrackedDeviceClass_GenericTracker: "tracker",
}


def beep(*notes):
    """Non-blocking: a blocking beep would stall the heartbeat and drop the override."""
    def play():
        try:
            import winsound
            for freq, ms in notes:
                winsound.Beep(freq, ms)
        except Exception:
            pass
    threading.Thread(target=play, daemon=True).start()


# ---------------------------------------------------------------- SteamVR access

class VR:
    def __init__(self):
        try:
            self.sys = openvr.init(openvr.VRApplication_Background)
        except OpenVRError as e:
            sys.exit(f"Can't reach SteamVR ({type(e).__name__}). Start SteamVR and try again.")
        self.poses = (openvr.TrackedDevicePose_t * openvr.k_unMaxTrackedDeviceCount)()

    def devices(self):
        """[(index, class name, serial)] for the HMD, controllers and trackers."""
        out = []
        for i in range(openvr.k_unMaxTrackedDeviceCount):
            c = self.sys.getTrackedDeviceClass(i)
            if c in CLASSES:
                try:
                    serial = self.sys.getStringTrackedDeviceProperty(i, openvr.Prop_SerialNumber_String)
                except OpenVRError:
                    continue
                out.append((i, CLASSES[c], serial))
        return out

    def trackers(self):
        return {serial: i for i, c, serial in self.devices() if c == "tracker"}

    def sample(self):
        # Raw universe: the same space the driver poses live in.
        self.sys.getDeviceToAbsoluteTrackingPose(openvr.TrackingUniverseRawAndUncalibrated, 0, self.poses)

    def pose(self, i):
        """(R, p, ok) of device i from the last sample()."""
        pose = self.poses[i]
        m = np.array([[pose.mDeviceToAbsoluteTracking.m[r][c] for c in range(4)] for r in range(3)])
        ok = bool(pose.bPoseIsValid) and pose.eTrackingResult == openvr.TrackingResult_Running_OK
        return m[:, :3], m[:, 3], ok

    def quit_requested(self):
        ev = openvr.VREvent_t()
        while self.sys.pollNextEvent(ev):
            if ev.eventType == openvr.VREvent_Quit:
                self.sys.acknowledgeQuit_Exiting()
                return True
        return False

    def shutdown(self):
        openvr.shutdown()


# ---------------------------------------------------------------- profile

def load_settings():
    try:
        return json.loads(SETTINGS.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_setting(key, value):
    """Sets one key of settings.json, keeping the others (it is shared with fbtcal.exe)."""
    data = load_settings()
    data[key] = value
    SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    write_atomic(SETTINGS, json.dumps(data))


def load_setting_number(key, scale=1.0):
    """A number set on the SteamVR panel, scaled; None = not set (the pose's own)."""
    try:
        v = load_settings().get(key)
        return None if v is None else float(v) * scale
    except (TypeError, ValueError):
        return None


def load_stance():
    """The stance set on the SteamVR panel (feet distance, metres); None = as captured."""
    return load_setting_number("stance_cm", 0.01)


def pose_source():
    """Where the standing pose comes from: "capture" or "slimevr" (SlimeVR's proportions).

    Chosen on the SteamVR panel; unset = the capture if there is one, else SlimeVR."""
    source = load_settings().get("pose_source")
    if source in ("capture", "slimevr"):
        return source
    return "capture" if PROFILE.exists() else "slimevr"


def load_profile():
    """The standing pose in use, stance applied: {serial: (pos, rot)} in the HMD yaw frame."""
    if pose_source() == "slimevr":
        try:
            bones, info = slimevr.load()
        except slimevr.SlimeVrError as e:
            sys.exit(f"{e}\nSwitch to the captured pose, or capture one:  python fbtcal.py capture")
        print(f"Pose: SlimeVR body proportions ({info['profile']} profile, head–ankle {info['height_m'] * 100:.0f} cm)")
        if info["missing"]:
            print("  defaults (from the height):", ", ".join(info["missing"]))
        trackers = slimevr_rest_pose(bones)
    else:
        if not PROFILE.exists():
            sys.exit("No standing pose captured yet. First:  python fbtcal.py capture")
        data = json.loads(PROFILE.read_text(encoding="utf-8"))
        print(f"Pose: captured ({data.get('captured', '?')})")
        trackers = {s: (np.array(t["pos"]), np.array(t["rot"])) for s, t in data["trackers"].items()}
    # the SteamVR panel's settings: stance (feet distance), toe-out, feet forward
    return adjust_pose(trackers, load_stance(), load_setting_number("toe_out_deg"),
                       load_setting_number("feet_forward_cm", 0.01))


def write_atomic(path, text):
    """Temp file onto the disk, then one replace: a crash mid-write leaves the old file, never half of one.

    Retries a moment while a reader (fbtcal.exe) holds the file. Same as Files.WriteAtomic in the exe."""
    path = Path(path)
    tmp = path.with_name(path.name + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    for attempt in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(0.05)


def save_profile(data):
    PROFILE.parent.mkdir(parents=True, exist_ok=True)
    if PROFILE.exists():  # never lose an earlier capture
        backup = PROFILE.with_name(f"profile.json.bak-{datetime.now():%Y%m%d-%H%M%S}")
        shutil.copy2(PROFILE, backup)
        print(f"The previous profile is kept: {backup}")
    write_atomic(PROFILE, json.dumps(data, indent=2))
    if json.loads(PROFILE.read_text(encoding="utf-8")) != data:
        sys.exit("The saved profile reads back different!")
    print(f"Profile saved and checked: {PROFILE}")


# ---------------------------------------------------------------- commands

def cmd_devices(args):
    vr = VR()
    try:
        for i, c, serial in vr.devices():
            print(f"  [{i:2d}] {c:10s} {serial}")
    finally:
        vr.shutdown()


def cmd_pose(args):
    profile = load_profile()
    print(f"Stance: {stance_m(profile) * 100:.0f} cm" if stance_m(profile) is not None else "Stance: –")
    print(f"Toe-out: {toe_out_deg(profile):.0f}°" if toe_out_deg(profile) is not None else "Toe-out: –")
    print(f"Feet forward: {(load_setting_number('feet_forward_cm') or 0):+.0f} cm")
    print("Trackers against the head (cm: right / up / back):")
    for s, (p, _) in profile.items():
        print(f"  {s:24s} {p[0] * 100:+6.1f} {p[1] * 100:+7.1f} {p[2] * 100:+6.1f}")


def cmd_capture(args):
    vr = VR()
    try:
        trackers = vr.trackers()
        if not trackers:
            sys.exit("I can't see any tracker in SteamVR.")
        print("Trackers:", ", ".join(trackers))
        print("\nDo a SlimeVR full reset, then stand up straight, legs straight, and look ahead.")
        for s in range(args.delay, 0, -1):
            print(f"  {s}…", end="", flush=True)
            if s <= 3:
                beep((700, 120))
            time.sleep(1)
        print("  NOW! Hold still.")
        beep((1200, 300))

        n = int(args.seconds * 90)
        hmd_R, hmd_p, hmd_ok = [], [], []
        trk = {s: ([], [], []) for s in trackers}
        nxt = time.perf_counter()
        for _ in range(n):
            vr.sample()
            R, p, ok = vr.pose(0)
            hmd_R.append(R)
            hmd_p.append(p)
            hmd_ok.append(ok)
            for s, i in trackers.items():
                R, p, ok = vr.pose(i)
                for lst, v in zip(trk[s], (R, p, ok)):
                    lst.append(v)
            nxt += 1 / 90
            time.sleep(max(0.0, nxt - time.perf_counter()))
        beep((900, 100), (900, 100))
    finally:
        vr.shutdown()

    hmd_R, hmd_p, hmd_ok = np.array(hmd_R), np.array(hmd_p), np.array(hmd_ok)
    problems = []
    pitch = float(np.median([pitch_deg(R) for R in hmd_R[hmd_ok]])) if hmd_ok.any() else float("nan")
    if not abs(pitch) <= 20:
        problems.append(f"your head was tilted {pitch:+.0f}° (look straight ahead)")
    out = {"version": 1, "captured": datetime.now().isoformat(timespec="seconds"),
           "hmd_pitch_deg": round(pitch, 1) if np.isfinite(pitch) else None, "trackers": {}}
    print("\nTrackers against the head (cm: right / up / back):")
    raw = {}
    for s, (R, p, ok) in trk.items():
        m = hmd_ok & np.array(ok)
        if m.sum() < 0.8 * n:
            problems.append(f"{s}: too few valid samples")
            continue
        pos, rot, ps, rs = rest_offset(hmd_R[m], hmd_p[m], np.array(R)[m], np.array(p)[m])
        flag = ""
        if ps > 0.03 or rs > 5:
            flag = "  <- moved"
            problems.append(f"{s}: moved during the capture ({ps * 100:.1f} cm, {rs:.0f}°)")
        print(f"  {s:24s} {pos[0] * 100:+6.1f} {pos[1] * 100:+7.1f} {pos[2] * 100:+6.1f}{flag}")
        raw[s] = (pos, rot)
        # Standing = every tracker hangs under the head (catches a capture made sitting or lying).
        sideways = float(np.hypot(pos[0], pos[2]))
        if sideways > STANDING_MAX_SIDEWAYS_M or pos[1] > -STANDING_MIN_DROP_M:
            problems.append(f"{s}: not under the head ({sideways * 100:.0f} cm to the side, {-pos[1] * 100:.0f} cm down), "
                            "this does not look like standing")

    # The ideal pose faces straight ahead and is left/right symmetric; the measured one is kept too.
    sym, rep = symmetrize(raw)
    as_json = lambda d: {s: {"pos": p.round(5).tolist(), "rot": R.round(6).tolist()} for s, (p, R) in d.items()}
    out["trackers"], out["trackers_raw"] = as_json(sym), as_json(raw)
    out["symmetry"] = {"body_yaw_deg": round(rep["body_yaw_deg"], 2), "pair_mismatch_m": round(rep["pair_mismatch_m"], 4)}
    print(f"\nSymmetrized: your body was turned {rep['body_yaw_deg']:+.1f}° against your head, "
          f"left and right differed by {rep['pair_mismatch_m'] * 100:.1f} cm at most.")

    if problems:
        print("\nProblems:\n  - " + "\n  - ".join(problems))
        if not args.force:
            sys.exit("Not saved. Capture again, or save it anyway:  python fbtcal.py capture --force")
    save_profile(out)
    if load_settings().get("pose_source") == "slimevr":  # a fresh capture is meant to be used
        save_setting("pose_source", "capture")
        print("Using the captured pose from now on (instead of SlimeVR's proportions).")


def override_loop(vr, profile, writer, until):
    """Keeps the ideal pose on until until() is true. Returns False if SteamVR quit."""
    index_of, refreshed = {}, 0.0
    period = 1 / OVERRIDE_HZ
    while not until():
        now = time.perf_counter()
        if now - refreshed > 1.0:
            trackers = vr.trackers()
            index_of = {s: trackers[s] for s in profile if s in trackers}
            refreshed = now
            if vr.quit_requested():
                return False
        vr.sample()
        R_h, p_h, ok = vr.pose(0)
        if ok:  # no write while the headset is lost: the heartbeat lapses and real poses return
            entries = []
            for s, i in index_of.items():
                R, p = ideal_pose(R_h, p_h, *profile[s])
                entries.append((i, p, quat_from_matrix(R)))
            writer.write(entries)
        time.sleep(max(0.0, period - (time.perf_counter() - now)))
    writer.clear()
    return True


def open_writer():
    if not shm.mapping_exists():
        print("WARNING: the FBTcal driver is not loaded in SteamVR, so the trackers won't move.\n"
              "  Check: SteamVR Settings > Startup / Shutdown > Manage Add-ons > fbtcal = On,\n"
              "  then restart SteamVR.")
    return shm.OverrideWriter()


def report_profile(vr, profile):
    present = vr.trackers()
    missing = [s for s in profile if s not in present]
    print("Into the ideal pose:", ", ".join(s for s in profile if s in present) or "nothing")
    if missing:
        print("Not in SteamVR:", ", ".join(missing))


def cmd_hold(args):
    profile = load_profile()
    vr = VR()
    writer = open_writer()
    try:
        report_profile(vr, profile)
        end = time.perf_counter() + args.seconds
        print(f"Ideal pose for {args.seconds:.0f} s. Calibrate in VRChat now (both triggers).")
        beep((900, 120), (1200, 200))
        override_loop(vr, profile, writer, lambda: time.perf_counter() >= end)
        beep((900, 100), (700, 150))
        print("Done, the trackers are back where they really are.")
    finally:
        writer.close()
        vr.shutdown()


def cmd_run(args):
    profile = load_profile()
    vr = VR()
    writer = open_writer()
    watcher = VrcLogWatcher()
    try:
        report_profile(vr, profile)
        print(f"VRChat log: {watcher.path or 'none yet'}")
        if watcher.path and not watcher.ik_debug_seen:
            print("Tip: if the calibration does not switch on by itself, add this to VRChat's launch options:\n"
                  "  --enable-ik-debug-logging")
        print("Watching. In VRChat: Calibrate FBT, then both triggers. (Quit: Ctrl+C)")

        state = {"start": 0.0, "end": None}

        def calibration_over():
            now = time.perf_counter()
            for ev in watcher.poll():
                if ev == "start":  # calibration restarted: keep going, reset the timer
                    state.update(start=now, end=None)
                elif ev == "end" and state["end"] is None:
                    state["end"] = now + END_GRACE_S
            if state["end"] is not None and now >= state["end"]:
                print("  Calibration done, the trackers are back where they really are.")
                return True
            if now - state["start"] > CALIBRATION_TIMEOUT_S:
                print("  Timed out: VRChat never reported the end of the calibration, switched off.")
                return True
            return False

        while True:
            if vr.quit_requested():
                break
            events = watcher.poll()
            if "newlog" in events:
                print(f"New VRChat log: {watcher.path.name}")
            if "start" in events:
                print(f"[{datetime.now():%H:%M:%S}] Calibration starts: ideal pose ON.")
                beep((900, 120), (1200, 200))
                state.update(start=time.perf_counter(), end=None)
                if not override_loop(vr, profile, writer, calibration_over):
                    break
                beep((900, 100), (700, 150))
            time.sleep(0.05)
        print("SteamVR quit, so do I.")
    except KeyboardInterrupt:
        pass
    finally:
        writer.close()
        vr.shutdown()


def main():
    for stream in (sys.stdout, sys.stderr):  # "–" and "°" must not crash a cp1252 pipe
        stream.reconfigure(errors="replace")
    ap = argparse.ArgumentParser(description="VRChat FBT calibration without posing (SlimeVR)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("devices", help="list the devices").set_defaults(fn=cmd_devices)
    sub.add_parser("pose", help="the standing pose in use (captured, or SlimeVR's proportions)").set_defaults(fn=cmd_pose)
    p = sub.add_parser("capture", help="capture the standing pose (once)")
    p.add_argument("--seconds", type=float, default=3)
    p.add_argument("--delay", type=int, default=6, help="time to get ready (s)")
    p.add_argument("--force", action="store_true", help="save despite warnings")
    p.set_defaults(fn=cmd_capture)
    sub.add_parser("run", help="automatic: follows the VRChat log").set_defaults(fn=cmd_run)
    p = sub.add_parser("hold", help="manual: ideal pose for N seconds")
    p.add_argument("seconds", type=float, nargs="?", default=15)
    p.set_defaults(fn=cmd_hold)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
