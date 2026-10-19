"""Image-level skin-type model with whole-image context (T52 option: colour + exposure context).

One row per image (so cross-validation is grouped by image by construction). Features: skin
L*/a*/b*/ITA, whole-image L* percentiles, skin/image L* ratio, skin fraction, HSV hue/saturation.
Reports repeated stratified 5-fold CV: six-way exact, within one step, and a direct
light (I-III) vs dark (IV-VI) classifier (accuracy, AUROC, recall of dark). Compared with
ITA-only features. Features are cached to --cache so a re-run after more downloads is cheap.

Usage: .venv/bin/python scripts/fst_context.py --data ~/Documents/Color-pipeline/data/fitzpatrick17k
"""
from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import cv2
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import RepeatedStratifiedKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.gate import FEATURE_NAMES, context_features  # noqa: E402

NAMES = list(FEATURE_NAMES)
ITA_ONLY = [3]


def image_features(bgr: np.ndarray) -> np.ndarray | None:
    return context_features(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))


def load_features(data: Path, cache: Path):
    rows = []
    with (data / "download_log.csv").open(newline="") as f:
        seen = set()
        for r in csv.DictReader(f):
            p = data / "images" / f"{r['md5hash']}.jpg"
            if r["status"] == "ok" and p.exists() and r["md5hash"] not in seen:
                seen.add(r["md5hash"])
                rows.append((r["md5hash"], int(r["fitzpatrick_scale"]) - 1, p))
    old = {}
    if cache.exists():
        z = np.load(cache, allow_pickle=True)
        old = dict(zip(z["ids"], z["X"]))
    X, y, ids = [], [], []
    for h, k, p in rows:
        v = old.get(h)
        if v is None:
            img = cv2.imread(str(p))
            v = None if img is None else image_features(img)
            if v is None:
                v = np.full(len(NAMES), np.nan, dtype=np.float32)
        X.append(v); y.append(k); ids.append(h)
    X, y, ids = np.stack(X), np.array(y), np.array(ids)
    np.savez(cache, ids=ids, X=X)
    ok = ~np.isnan(X).any(1)
    print(f"{len(ids)} images, {(~ok).sum()} without enough skin pixels (dropped)")
    return X[ok], y[ok]


def cv_eval(make, X, y, cols, seed_reps=5):
    ex, w1, ld_acc, auc, dark_rec = [], [], [], [], []
    for tr, te in RepeatedStratifiedKFold(n_splits=5, n_repeats=seed_reps, random_state=0).split(X, y):
        m = make().fit(X[tr][:, cols], y[tr])
        P = m.predict_proba(X[te][:, cols])
        pred = P.argmax(1)
        ex.append((pred == y[te]).mean()); w1.append((abs(pred - y[te]) <= 1).mean())
        dark_p = P[:, 3:].sum(1)
        ld_acc.append(((dark_p > .5) == (y[te] >= 3)).mean()); auc.append(roc_auc_score(y[te] >= 3, dark_p))
        dark_rec.append(((dark_p > .5) & (y[te] >= 3)).sum() / (y[te] >= 3).sum())
    f = lambda v: f"{np.mean(v):.3f}±{np.std(v):.3f}"
    return f"exact {f(ex)}  within1 {f(w1)}  light/dark acc {f(ld_acc)}  AUROC {f(auc)}  dark recall {f(dark_rec)}"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", type=Path, required=True)
    d = ap.parse_args().data.expanduser()
    X, y = load_features(d, d / "context_features.npz")
    print("class counts:", np.bincount(y), "\n(± is spread over 25 folds, not a confidence interval)")
    lr = lambda: make_pipeline(StandardScaler(), LogisticRegression(max_iter=3000, C=0.5))
    gb = lambda: HistGradientBoostingClassifier(max_iter=150, learning_rate=0.05, max_depth=3, random_state=0)
    allc = list(range(len(NAMES)))
    for name, make, cols in [("ITA only, logistic", lr, ITA_ONLY), ("skin colour only, logistic", lr, [0, 1, 2, 3, 4, 10, 11]),
                             ("skin + exposure context, logistic", lr, allc), ("skin + exposure context, boosting", gb, allc)]:
        print(f"{name:36s}", cv_eval(make, X, y, cols))


if __name__ == "__main__":
    main()
