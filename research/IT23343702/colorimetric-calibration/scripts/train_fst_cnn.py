"""MobileNetV2 Fitzpatrick-type classifier on 64x64 skin patches (T52).

Colour is the signal, so augmentation is geometric only (flips, 90-degree turns): no brightness,
contrast or hue jitter. Phase 1 trains the head on frozen ImageNet weights, phase 2 fine-tunes the
whole net at a low rate. Model selection uses the validation split only; test is scored once.
Saves the Keras model and prints the same metrics as scripts/fst_baseline.py.

Usage: .venv/bin/python scripts/train_fst_cnn.py --patches ~/Documents/Color-pipeline/data/fst_patches \
           --out ~/Documents/Color-pipeline/data/fst_model
"""
from __future__ import annotations

import argparse
import csv
import os
from pathlib import Path

os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "2")
import cv2
import numpy as np
import tensorflow as tf
from tensorflow import keras


def load(root: Path, split: str):
    with (root / f"{split}.csv").open(newline="") as f:
        rows = list(csv.DictReader(f))
    X = np.stack([cv2.cvtColor(cv2.imread(str(root / r["patch"])), cv2.COLOR_BGR2RGB) for r in rows]).astype(np.float32)
    return X, np.array([int(r["fst"]) - 1 for r in rows]), np.array([r["image"] for r in rows])


def build(weights: str | None) -> keras.Model:
    base = keras.applications.MobileNetV2(input_shape=(64, 64, 3), include_top=False, weights=weights, alpha=1.0)
    inp = keras.Input((64, 64, 3))
    x = keras.layers.Rescaling(1 / 127.5, offset=-1)(inp)  # MobileNetV2 expects [-1, 1]
    x = keras.layers.RandomFlip("horizontal_and_vertical")(x)
    x = base(x)
    x = keras.layers.GlobalAveragePooling2D()(x)
    x = keras.layers.Dropout(0.3)(x)
    return keras.Model(inp, keras.layers.Dense(6, activation="softmax")(x))


def metrics(name: str, P: np.ndarray, Y: np.ndarray) -> None:
    pred = P.argmax(1)
    rng = np.random.default_rng(0)
    boots = [(pred[i] == Y[i]).mean() for i in (rng.integers(0, len(Y), len(Y)) for _ in range(2000))]
    light = ((P[:, :3].sum(1) < P[:, 3:].sum(1)) == (Y >= 3)).mean()
    rec = [(pred[Y == k] == k).mean() for k in range(6)]
    print(f"{name:22s} n={len(Y):4d} exact {(pred == Y).mean():.3f} [{np.percentile(boots, 2.5):.3f},{np.percentile(boots, 97.5):.3f}] "
          f"within1 {(abs(pred - Y) <= 1).mean():.3f} light/dark {light:.3f} recall " + " ".join(f"{r:.2f}" for r in rec))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--patches", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--no-pretrained", action="store_true")
    a = ap.parse_args()
    root, out = a.patches.expanduser(), a.out.expanduser()
    out.mkdir(parents=True, exist_ok=True)
    keras.utils.set_random_seed(a.seed)
    (Xtr, ytr, _), (Xva, yva, _), (Xte, yte, ite) = (load(root, s) for s in ("train", "val", "test"))

    model = build(None if a.no_pretrained else "imagenet")
    ckpt = keras.callbacks.ModelCheckpoint(str(out / "best.keras"), monitor="val_accuracy", save_best_only=True)
    stop = keras.callbacks.EarlyStopping(monitor="val_accuracy", patience=8, restore_best_weights=True)
    base = model.layers[-4]  # the MobileNetV2 block
    for phase, (lr, train_base, epochs) in enumerate([(1e-3, False, 15), (1e-4, True, 40)], 1):
        base.trainable = train_base
        model.compile(optimizer=keras.optimizers.Adam(lr), loss="sparse_categorical_crossentropy", metrics=["accuracy"])
        print(f"--- phase {phase}: lr {lr}, base trainable {train_base}")
        model.fit(Xtr, ytr, validation_data=(Xva, yva), epochs=epochs, batch_size=64, callbacks=[ckpt, stop], verbose=2)
    model.save(out / "final.keras")

    metrics("val (patch)", model.predict(Xva, verbose=0), yva)
    proba = model.predict(Xte, verbose=0)
    metrics("TEST (patch)", proba, yte)
    ids = sorted(set(ite))
    P = np.stack([proba[ite == i].mean(0) for i in ids])
    Y = np.array([yte[ite == i][0] for i in ids])
    metrics("TEST (image vote)", P, Y)
    np.save(out / "test_proba.npy", proba)


if __name__ == "__main__":
    main()
