import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from vrclog import VrcLogWatcher  # noqa: E402

# Real lines from a VRChat log, in the order a calibration produces them.
CALIBRATION = """\
2026.10.25 20:44:59 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrate FBT and reload avatar...
2026.10.25 20:44:59 Debug      -  [VRCTrackingManager] [IK Debug Log] Clearing all calibrated trackers...
2026.10.25 20:45:00 Debug      -  [VRCTrackingManager] [IK Debug Log] Preparing for calibration...
2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibration alignment... Position:(-0.96, 1.45, 0.08) yaw:90.52669
2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Clearing all calibrated trackers...
2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrating trackers...
2026.10.25 20:45:07 Debug      -  [VRCTrackingSteam] [IK Debug Log] [Steam] Calibrating tracker... Serial number: human://WAIST
2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Applying calibration to avatar... type: 8
"""


class Watcher(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def append(self, name, text):
        with open(self.dir / name, "a", encoding="utf-8", newline="\n") as fh:
            fh.write(text)

    def test_starts_at_the_end_of_an_existing_log(self):
        self.append("output_log_2026-10-25_20-38-23.txt", CALIBRATION)
        w = VrcLogWatcher(self.dir)
        self.assertTrue(w.ik_debug_seen)
        self.assertEqual(w.poll(), [])

    def test_calibration_start_and_end(self):
        self.append("output_log_2026-10-25_20-38-23.txt", "2026.10.25 20:38:23 Log - startup\n")
        w = VrcLogWatcher(self.dir)
        self.assertFalse(w.ik_debug_seen)
        lines = CALIBRATION.splitlines(keepends=True)
        self.append("output_log_2026-10-25_20-38-23.txt", "".join(lines[:3]))
        self.assertEqual(w.poll(), ["start"])
        self.assertTrue(w.ik_debug_seen)
        # A line written in two pieces is only reported once it is complete.
        self.append("output_log_2026-10-25_20-38-23.txt", "".join(lines[3:5]) + lines[5][:40])
        self.assertEqual(w.poll(), [])
        self.append("output_log_2026-10-25_20-38-23.txt", lines[5][40:] + "".join(lines[6:]))
        self.assertEqual(w.poll(), ["end", "end"])

    def test_switches_to_a_new_log_after_restart(self):
        self.append("output_log_2026-10-25_20-38-23.txt", "old\n")
        w = VrcLogWatcher(self.dir)
        self.append("output_log_2026-10-25_21-00-00.txt", CALIBRATION.splitlines(keepends=True)[2])
        self.assertEqual(w.poll(), ["newlog", "start"])
        self.assertEqual(w.path.name, "output_log_2026-10-25_21-00-00.txt")

    def test_no_log_yet(self):
        w = VrcLogWatcher(self.dir)
        self.assertIsNone(w.path)
        self.assertEqual(w.poll(), [])
        self.append("output_log_2026-10-26_10-00-00.txt", CALIBRATION.splitlines(keepends=True)[2])
        self.assertEqual(w.poll(), ["newlog", "start"])


if __name__ == "__main__":
    unittest.main()
