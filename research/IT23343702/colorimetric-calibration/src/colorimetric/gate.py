"""Light/dark gate from image context (T55): a second opinion on ITA for the CCM decision.

ITA reads only the skin mask, so exposure and a failed mask move it a long way. This gate adds
whole-image brightness context to the skin colour and applies a logistic regression, so it is
plain arithmetic that ports to Dart: 13 features -> standardise -> dot product -> sigmoid.

Evidence (scripts/fst_gate_eval.py, 592 Fitzpatrick17k images, repeated 5-fold CV, dark = IV-VI):
the 28 deg ITA gate calls 95% of dark skin dark but also 61% of light skin; this model at
threshold 0.35 gets 87% / 35%. The shipped weights are fitted on all 592 images, so the CV
figures, not an in-sample score, describe expected behaviour. Labels are dermatologist labels on
diseased skin (noisy), and the data are clinical photos, not wound photos: UNVERIFIED on DFUC.

It does NOT carry over to a second dataset. On 193 hand-labelled ISIC images (97 dark) the 28 deg
ITA gate gets dark recall 0.99 / light specificity 0.87, this model at 0.35 gets 0.71 / 0.96.
The two datasets disagree about which gate is better, so do not replace the 28 deg rule with this.

Not wired into the pipeline by default; call `dark_probability` / `is_dark` yourself.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from .ita import skin_mask_hsv

GATE_PATH = Path(__file__).parent / "gate" / "gate_model.json"
FEATURE_NAMES = (
    "skin_L", "skin_a", "skin_b", "skin_ita", "skin_L_std", "img_L_p5", "img_L_p50",
    "img_L_p95", "L_ratio", "skin_frac", "skin_hue", "skin_sat", "ita_minus_img",
)
MIN_SKIN_PIXELS = 200   # on the 256 px copy; below this there is no skin to read
WORK_SIZE = 256         # long side of the working copy


def context_features(rgb: np.ndarray) -> np.ndarray | None:
    """13 features from an RGB uint8 image, or None when the mask finds too little skin."""
    if rgb.dtype != np.uint8 or rgb.ndim != 3 or rgb.shape[2] != 3:
        raise ValueError("rgb must be a uint8 RGB image of shape (H, W, 3)")
    s = WORK_SIZE / max(rgb.shape[:2])
    if s < 1:
        rgb = cv2.resize(rgb, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    m = skin_mask_hsv(rgb)
    if m.sum() < MIN_SKIN_PIXELS:
        return None
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB).astype(np.float32)
    L, a, b = lab[..., 0] * 100 / 255, lab[..., 1] - 128, lab[..., 2] - 128
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV).astype(np.float32)
    sl, sa, sb = L[m].mean(), a[m].mean(), b[m].mean()
    ita = np.degrees(np.arctan2(sl - 50, sb))
    p5, p50, p95 = np.percentile(L, [5, 50, 95])
    return np.array(
        [sl, sa, sb, ita, L[m].std(), p5, p50, p95, sl / max(L.mean(), 1e-3), m.mean(),
         hsv[..., 0][m].mean() * 2, hsv[..., 1][m].mean() / 2.55,
         ita - np.degrees(np.arctan2(p50 - 50, 20))],
        dtype=np.float32,
    )


@dataclass(frozen=True)
class GateModel:
    mean: np.ndarray
    scale: np.ndarray
    coef: np.ndarray
    intercept: float
    threshold: float
    info: dict

    def probability(self, features: np.ndarray) -> float:
        z = float(np.dot((features - self.mean) / self.scale, self.coef) + self.intercept)
        return 1.0 / (1.0 + np.exp(-z))


def load_gate(path: str | Path | None = None) -> GateModel:
    d = json.loads(Path(path or GATE_PATH).read_text())
    if tuple(d["features"]) != FEATURE_NAMES:
        raise ValueError("gate model was fitted on a different feature list")
    return GateModel(np.array(d["mean"]), np.array(d["scale"]), np.array(d["coef"]),
                     float(d["intercept"]), float(d["threshold"]), d.get("info", {}))


def dark_probability(rgb: np.ndarray, model: GateModel | None = None) -> float | None:
    """P(Fitzpatrick IV-VI), or None when there is not enough skin to read."""
    f = context_features(rgb)
    return None if f is None else (model or load_gate()).probability(f)


def is_dark(rgb: np.ndarray, model: GateModel | None = None) -> bool | None:
    """True if the CCM should run. None means "cannot tell" (caller should retake)."""
    model = model or load_gate()
    p = dark_probability(rgb, model)
    return None if p is None else bool(p > model.threshold)
