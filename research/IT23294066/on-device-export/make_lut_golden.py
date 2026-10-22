"""Writes the complete gain lookup tables of the colour component's apply_gains (research/IT23343702, marker.py) for a
few gain sets, so the Dart port can be compared on all 256 entries of every channel and not only on samples.
Output: mobile/test/measurement/golden/lut_full.json (numbers only, no photos).

    python make_lut_golden.py
"""
import json
import sys
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "research" / "IT23343702" / "colorimetric-calibration" / "src"))
from colorimetric import marker as M  # noqa: E402

GAINS = [(1.2, 1.0, 0.8), (0.5, 0.5, 0.5), (1.0, 1.0, 1.0), (2.0, 1.5, 0.25), (0.3, 0.9, 3.9), (4.0, 0.25, 1.7), (1.37, 0.61, 2.83)]
levels = np.arange(256, dtype=np.uint8)
ramp = np.stack([levels, levels, levels], axis=-1)[None]  # 1 x 256 pixels, every level in every channel
out = {"note": "lut[c][i] = apply_gains output for input level i in channel c; made by make_lut_golden.py", "cases": []}
for g in GAINS:
    res = M.apply_gains(ramp, np.array(g))[0]
    out["cases"].append({"gains": list(g), "lut": [res[:, c].astype(int).tolist() for c in range(3)]})
dest = REPO / "mobile" / "test" / "measurement" / "golden" / "lut_full.json"
dest.parent.mkdir(parents=True, exist_ok=True)
dest.write_text(json.dumps(out) + "\n")
print("wrote", dest, dest.stat().st_size, "bytes,", len(GAINS), "gain sets")
