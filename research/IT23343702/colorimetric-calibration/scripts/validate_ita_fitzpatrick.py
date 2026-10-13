"""Validate the ITA classifier against Fitzpatrick17k dermatologist labels (T22).

Runs classify_image() on every downloaded image listed as 'ok' in the download
log and compares it with `fitzpatrick_scale`. Prints a confusion matrix, exact
and within-one-step accuracy, and per-type recall. Optionally writes a CSV.

Usage (from research/IT23343702/colorimetric-calibration):
    python scripts/validate_ita_fitzpatrick.py \
        --data ~/Documents/Color-pipeline/data/fitzpatrick17k \
        --out ~/Documents/Color-pipeline/data/annotations/ita_vs_fitzpatrick17k.csv
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.evaluation import gate_metrics  # noqa: E402
from colorimetric.ita import GATE_CUTOFF_FITTED, GATE_CUTOFF_STANDARD, classify_image  # noqa: E402

TYPES = ["I", "II", "III", "IV", "V", "VI"]


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", type=Path, default=Path("data/fitzpatrick17k"))
    p.add_argument("--out", type=Path, default=None, help="optional per-image results CSV")
    args = p.parse_args()
    data = args.data.expanduser()

    log = data / "download_log.csv"
    if not log.exists():
        print(f"No download log at {log}", file=sys.stderr)
        return 1

    results = []
    failed = 0
    with log.open(newline="") as f:
        for r in csv.DictReader(f):
            if r["status"] != "ok":
                continue
            img = cv2.imread(str(data / "images" / f"{r['md5hash']}.jpg"))
            if img is None:
                continue
            true = TYPES[int(r["fitzpatrick_scale"]) - 1]
            try:
                res = classify_image(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
            except ValueError:
                failed += 1
                continue
            pred = res.fitzpatrick_type.value
            results.append((r["md5hash"], true, pred, res.ita_angle, res.skin_pixel_count))

    n = len(results)
    if n == 0:
        print("No images classified.")
        return 1
    idx = {t: i for i, t in enumerate(TYPES)}
    cm = [[0] * 6 for _ in range(6)]
    for _, t, g, _, _ in results:
        cm[idx[t]][idx[g]] += 1
    exact = sum(cm[i][i] for i in range(6))
    within1 = sum(cm[i][j] for i in range(6) for j in range(6) if abs(i - j) <= 1)
    signed = sum((j - i) * cm[i][j] for i in range(6) for j in range(6)) / n

    print(f"Classified {n} images ({failed} skipped: too few skin pixels)")
    print(f"Exact accuracy:   {exact}/{n} = {exact / n:.1%}")
    print(f"Within one step:  {within1}/{n} = {within1 / n:.1%}")
    print(f"Mean signed error (ITA minus true, in steps): {signed:+.2f}\n")
    print("Confusion matrix (rows = true FST, columns = ITA guess)")
    print("      " + "".join(f"{t:>5}" for t in TYPES) + "   recall")
    for i, t in enumerate(TYPES):
        tot = sum(cm[i])
        rec = f"{cm[i][i] / tot:.0%}" if tot else "-"
        print(f"{t:>5} " + "".join(f"{v:>5}" for v in cm[i]) + f"   {rec} (n={tot})")

    import numpy as np

    true_idx = np.array([idx[t] for _, t, _, _, _ in results])
    angles = np.array([a for _, _, _, a, _ in results])
    print("\nCCM gate (ITA <= cutoff -> IV-VI gets corrected)")
    print("  cutoff   accuracy   dark found   light wrongly corrected")
    for name, c in (("standard", GATE_CUTOFF_STANDARD), ("fitted", GATE_CUTOFF_FITTED)):
        g = gate_metrics(true_idx, angles, c)
        print(f"  {c:5.1f}    {g.accuracy:7.1%}    {g.dark_recall:9.1%}    {g.light_false_correction:9.1%}   ({name})")

    if args.out:
        out = args.out.expanduser()
        with out.open("w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["md5hash", "true_fst", "ita_fst", "ita_angle", "skin_pixel_count"])
            w.writerows(results)
        print(f"\nPer-image results -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
