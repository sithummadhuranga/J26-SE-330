"""Fit the T55 gate on all labelled images and write src/colorimetric/gate/gate_model.json.

Usage: .venv/bin/python scripts/export_gate_model.py --data ~/Documents/Color-pipeline/data/fitzpatrick17k
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.gate import FEATURE_NAMES, GATE_PATH  # noqa: E402
from fst_context import load_features  # noqa: E402

THRESHOLD = 0.35  # recall-leaning operating point, see scripts/fst_gate_eval.py


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", type=Path, required=True)
    d = ap.parse_args().data.expanduser()
    X, y = load_features(d, d / "context_features.npz")
    sc = StandardScaler().fit(X)
    lr = LogisticRegression(max_iter=3000, C=0.5).fit(sc.transform(X), y >= 3)
    GATE_PATH.write_text(json.dumps({
        "features": list(FEATURE_NAMES), "mean": sc.mean_.tolist(), "scale": sc.scale_.tolist(),
        "coef": lr.coef_[0].tolist(), "intercept": float(lr.intercept_[0]), "threshold": THRESHOLD,
        "info": {"fitted_on": f"{len(y)} Fitzpatrick17k images (atlasdermatologico), dark = FST IV-VI",
                 "evidence": "scripts/fst_gate_eval.py (repeated 5-fold CV)", "in_sample": True},
    }, indent=1) + "\n")
    print("wrote", GATE_PATH)


if __name__ == "__main__":
    main()
