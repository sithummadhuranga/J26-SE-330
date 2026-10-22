"""Shared set-up for the CV tests: puts the script folders and the colour component on the import path.

Run all tests from the repo root with the project's virtual environment:
    research/IT23294066/.venv/bin/python -m unittest discover -s research/IT23294066/tests -t research/IT23294066/tests -v
The tests use only made-up data (no DFUC photos, no model weights), so they run anywhere the requirements are installed.
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]
CALIB = REPO / "research" / "IT23343702" / "colorimetric-calibration"
for p in [ROOT / s for s in ("area-measurement", "calibration-study", "colour-regions", "data-preparation", "on-device-export",
                             "segmentation/evaluation", "segmentation/training")] + [CALIB / "src", CALIB / "scripts"]:
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))
