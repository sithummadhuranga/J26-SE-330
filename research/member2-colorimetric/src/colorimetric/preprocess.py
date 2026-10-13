"""Pipeline Step 1 (raw input processing), Step 7 (final normalisation) and LAB helpers.

Conventions: images are uint8 RGB (H, W, 3). LAB arrays are float64 with
L in [0, 100] and a*, b* signed (roughly [-128, 127]), the same scale as ita.py/ccm.py.
"""

from __future__ import annotations

import cv2
import numpy as np

IMAGENET_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)


def _check_rgb(rgb: np.ndarray) -> None:
    if rgb.dtype != np.uint8 or rgb.ndim != 3 or rgb.shape[2] != 3:
        raise ValueError("expected a uint8 RGB image of shape (H, W, 3)")


def rgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    """uint8 RGB -> float64 LAB (L in [0,100], a*/b* signed)."""
    _check_rgb(rgb)
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB).astype(np.float64)
    lab[..., 0] *= 100.0 / 255.0
    lab[..., 1] -= 128.0
    lab[..., 2] -= 128.0
    return lab


def lab_to_rgb(lab: np.ndarray) -> np.ndarray:
    """float LAB (as produced by rgb_to_lab) -> uint8 RGB, clipping out-of-range values."""
    packed = np.empty(lab.shape, dtype=np.float64)
    packed[..., 0] = lab[..., 0] * (255.0 / 100.0)
    packed[..., 1] = lab[..., 1] + 128.0
    packed[..., 2] = lab[..., 2] + 128.0
    packed = np.clip(np.rint(packed), 0, 255).astype(np.uint8)
    return cv2.cvtColor(packed, cv2.COLOR_LAB2RGB)


def process_raw_input(
    rgb: np.ndarray,
    size: int = 512,
    low_pct: float = 0.5,
    high_pct: float = 99.5,
    denoise: bool = True,
) -> np.ndarray:
    """Step 1: resize to size x size, stretch intensity, soften JPEG artefacts.

    * Resize uses INTER_AREA when shrinking and INTER_CUBIC when enlarging.
    * The intensity stretch maps one (low_pct, high_pct) percentile range of the
      whole image to [0, 255] with the SAME gain for all three channels, so it
      never changes hue (a per-channel stretch would shift skin colour).
    * JPEG artefact reduction is a small edge-preserving bilateral filter
      (assumed interpretation of the tracker's "JPEG artefacts"; revisit in ablation).
    """
    _check_rgb(rgb)
    h, w = rgb.shape[:2]
    interp = cv2.INTER_AREA if size < max(h, w) else cv2.INTER_CUBIC
    out = cv2.resize(rgb, (size, size), interpolation=interp)

    lo, hi = np.percentile(out, [low_pct, high_pct])
    if hi - lo > 1e-6:
        out = np.clip((out.astype(np.float32) - lo) * (255.0 / (hi - lo)), 0, 255).astype(np.uint8)

    if denoise:
        out = cv2.bilateralFilter(out, d=5, sigmaColor=25, sigmaSpace=5)
    return out


def final_normalise(rgb: np.ndarray, mode: str = "imagenet") -> np.ndarray:
    """Step 7: uint8 RGB -> float32 model input of the same shape.

    mode "imagenet": (x/255 - mean) / std with ImageNet statistics.
    mode "zscore":   per-image, per-channel (x - mean) / std.
    """
    _check_rgb(rgb)
    x = rgb.astype(np.float32) / 255.0
    if mode == "imagenet":
        return (x - IMAGENET_MEAN) / IMAGENET_STD
    if mode == "zscore":
        mean = x.mean(axis=(0, 1), keepdims=True)
        std = x.std(axis=(0, 1), keepdims=True)
        return (x - mean) / np.maximum(std, 1e-6)
    raise ValueError(f"unknown mode {mode!r} (use 'imagenet' or 'zscore')")
