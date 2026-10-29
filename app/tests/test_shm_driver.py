"""The app's shared-memory writer against the real driver DLL (through driver/build/hooktest.exe)."""
import os
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "app"))
import shm  # noqa: E402
from posemath import quat_from_matrix  # noqa: E402

HOOKTEST = ROOT / "driver" / "build" / "hooktest.exe"
DLL = ROOT / "driver" / "fbtcal" / "bin" / "win64" / "driver_fbtcal.dll"


@unittest.skipUnless(HOOKTEST.exists() and DLL.exists(), "build the driver first (driver/build.bat)")
class AppToDriver(unittest.TestCase):
    def run_hooktest(self, device, pos, quat):
        args = [str(HOOKTEST), str(DLL), "--external", str(device)] + [repr(float(v)) for v in (*pos, *quat)]
        env = dict(os.environ, FBTCAL_SHM_NAME=shm.TEST_NAME)  # the DLL under test reads this name
        return subprocess.run(args, capture_output=True, text=True, timeout=30, env=env)

    def test_written_pose_reaches_the_driver(self):
        w = shm.OverrideWriter(shm.TEST_NAME)
        stop = threading.Event()
        pos = np.array([0.123456789, -0.75, 1.5])
        quat = quat_from_matrix(np.array([[0, 0, 1], [0, 1, 0], [-1, 0, 0]], float))

        def keep_writing():  # like the app: the heartbeat must stay fresh while hooktest starts
            while not stop.is_set():
                w.write([(2, [9, 9, 9], [1, 0, 0, 0]), (5, pos, quat)])
                time.sleep(0.01)

        t = threading.Thread(target=keep_writing)
        t.start()
        try:
            r = self.run_hooktest(5, pos, quat)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        finally:
            stop.set()
            t.join()
            w.close()

    def test_cleared_writer_overrides_nothing(self):
        w = shm.OverrideWriter(shm.TEST_NAME)
        try:
            w.write([(5, [1, 2, 3], [1, 0, 0, 0])])
            w.clear()
            r = self.run_hooktest(5, [1, 2, 3], [1, 0, 0, 0])
            self.assertNotEqual(r.returncode, 0)
            self.assertIn("[FAIL] app-written override applied", r.stdout)
        finally:
            w.close()


if __name__ == "__main__":
    unittest.main()
