"""Colour-feature baseline for the FST classifier (T52 reference point).

Per-patch features (mean/std of L*, a*, b*, ITA, HSV means) -> logistic regression and gradient
boosting. Reports patch-level and image-level (mean of patch probabilities) results: six-way
exact, within one step, light/dark (I-III vs IV-VI) accuracy and per-type recall, with a
bootstrap interval over images. Same split as scripts/build_patch_dataset.py.

Usage: .venv/bin/python scripts/fst_baseline.py --patches ~/Documents/Color-pipeline/data/fst_patches
"""
from __future__ import annotations

import argparse
import csv
from pathlib import Path

import cv2
import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler


def features(path: Path) -> np.ndarray:
    bgr = cv2.imread(str(path))
    lab = cv2.cvtColor(bgr, cv2.COLOR_BGR2LAB).astype(np.float32)
    L, a, b = lab[..., 0] * 100 / 255, lab[..., 1] - 128, lab[..., 2] - 128
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV).astype(np.float32)
    ita = np.degrees(np.arctan2(L.mean() - 50, b.mean()))
    return np.array([L.mean(), L.std(), a.mean(), a.std(), b.mean(), b.std(), ita,
                     hsv[..., 0].mean(), hsv[..., 1].mean(), hsv[..., 2].mean()], dtype=np.float32)


def load(root: Path, split: str):
    with (root / f"{split}.csv").open(newline="") as f:
        rows = list(csv.DictReader(f))
    X = np.stack([features(root / r["patch"]) for r in rows])
    return X, np.array([int(r["fst"]) - 1 for r in rows]), np.array([r["image"] for r in rows])


def image_level(proba: np.ndarray, images: np.ndarray, y: np.ndarray):
    ids = sorted(set(images))
    P = np.stack([proba[images == i].mean(0) for i in ids])
    Y = np.array([y[images == i][0] for i in ids])
    return P, Y


def report(name: str, P: np.ndarray, Y: np.ndarray, rng: np.random.Generator) -> None:
    pred = P.argmax(1)
    exact, within1 = (pred == Y).mean(), (abs(pred - Y) <= 1).mean()
    light = (P[:, :3].sum(1) < P[:, 3:].sum(1)) == (Y >= 3)
    rec = [(pred[Y == k] == k).mean() if (Y == k).any() else float("nan") for k in range(6)]
    boots = [(pred[i] == Y[i]).mean() for i in (rng.integers(0, len(Y), len(Y)) for _ in range(2000))]
    lo, hi = np.percentile(boots, [2.5, 97.5])
    print(f"{name:34s} n={len(Y):4d} exact {exact:.3f} [{lo:.3f},{hi:.3f}]  within1 {within1:.3f}  light/dark {light.mean():.3f}  recall " + " ".join(f"{r:.2f}" for r in rec))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--patches", type=Path, required=True)
    root = ap.parse_args().patches.expanduser()
    rng = np.random.default_rng(0)
    Xtr, ytr, _ = load(root, "train")
    Xte, yte, ite = load(root, "test")
    models = {"logistic regression": make_pipeline(StandardScaler(), LogisticRegression(max_iter=2000)),
              "gradient boosting": HistGradientBoostingClassifier(max_iter=200, learning_rate=0.05, random_state=0)}
    for name, m in models.items():
        m.fit(Xtr, ytr)
        proba = m.predict_proba(Xte)
        report(f"{name} (patch)", proba, yte, rng)
        P, Y = image_level(proba, ite, yte)
        report(f"{name} (image vote)", P, Y, rng)
    # reference: ITA alone with the standard bins on the same patches' images
    ita = Xte[:, 6]
    cuts = np.array([-30, 10, 28, 41, 55])  # ascending ITA -> VI..I
    pred = 5 - np.searchsorted(cuts, ita)
    print(f"{'ITA bins on patch means':34s} n={len(yte):4d} exact {(pred == yte).mean():.3f}  within1 {(abs(pred - yte) <= 1).mean():.3f}")


if __name__ == "__main__":
    main()
