"""SlimeVR's body proportions, for a standing pose without standing up to capture it.

SlimeVR server v22 keeps them in %APPDATA%\\dev.slimevr.SlimeVR: global.json names the selected
user profile, user\\<name>.json has its userHeight and proportions (metres). posemath.slimevr_rest_pose
turns them into the pose. Older servers (vrconfig.yml) are not read.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

SLIMEVR_DIR = Path(os.environ.get("APPDATA", ".")) / "dev.slimevr.SlimeVR"

# SlimeVR's defaults (skeleton/proportions.kt) for bones missing from a profile. SlimeVR scales them to the
# profile's height; for adults that is a plain ratio of the default height (the bones up to LOWER_LEG).
DEFAULTS = {"NECK": 0.1, "UPPER_CHEST": 0.16, "LOWER_CHEST": 0.16, "UPPER_WAIST": 0.1, "LOWER_WAIST": 0.1,
            "HIP": 0.05, "UPPER_LEG": 0.4, "LOWER_LEG": 0.5,
            "HIPS_WIDTH": 0.26, "FOOT_LENGTH": 0.115, "FOOT_SHIFT": -0.02}
HEIGHT_BONES = ("NECK", "UPPER_CHEST", "LOWER_CHEST", "UPPER_WAIST", "LOWER_WAIST", "HIP", "UPPER_LEG", "LOWER_LEG")
DEFAULT_HEIGHT = sum(DEFAULTS[k] for k in HEIGHT_BONES)


class SlimeVrError(Exception):
    """The proportions can't be read; the message is for the user."""


def load(root=SLIMEVR_DIR):
    """The selected SlimeVR profile's bone lengths: (bones, info), info = profile, height_m, missing."""
    root = Path(root)
    if not root.is_dir():
        raise SlimeVrError("Can't find SlimeVR's settings (%APPDATA%\\dev.slimevr.SlimeVR).")
    try:
        name = json.loads((root / "global.json").read_text(encoding="utf-8")).get("selectedUserProfile") or "default"
    except (OSError, ValueError):
        name = "default"
    path = root / "user" / f"{name}.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError:
        raise SlimeVrError(f"No SlimeVR body proportions ({path.name}). This needs SlimeVR server v22 or later.") from None
    except ValueError as e:
        raise SlimeVrError(f"The SlimeVR profile is broken ({path.name}): {e}") from None
    stored = data.get("proportions") if isinstance(data, dict) else None
    if not isinstance(stored, dict):
        raise SlimeVrError(f"The SlimeVR profile ({path.name}) has no body proportions.")
    height = data.get("userHeight")
    height = float(height) if isinstance(height, (int, float)) and height > 0 else DEFAULT_HEIGHT
    bones, missing = {}, []
    for key, default in DEFAULTS.items():
        value = stored.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            bones[key] = float(value)
        else:
            bones[key] = default * height / DEFAULT_HEIGHT
            missing.append(key)
    return bones, {"profile": name, "height_m": height, "missing": missing}
