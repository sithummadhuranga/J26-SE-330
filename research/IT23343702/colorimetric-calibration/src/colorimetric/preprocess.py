"""Pipeline Step 1 (raw input processing), Step 7 (final normalisation), LAB helpers and
the model-side letterbox.

Conventions: images are uint8 RGB (H, W, 3). LAB arrays are float64 with
L in [0, 100] and a*, b* signed (roughly [-128, 127]), the same scale as ita.py/ccm.py.
"""

from __future__ import annotations

from dataclasses import dataclass

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
    low_pct: float = 0.5,
    high_pct: float = 99.5,
    denoise: bool = True,
) -> np.ndarray:
    """Step 1: stretch intensity and soften JPEG artefacts, keeping the exact input shape.

    Geometry is never changed (agreed with Member 1: same height and width in and
    out), because resizing or stretching would corrupt wound-area measurement. The
    resize for the model is done separately by `letterbox`, on the model side.

    * The intensity stretch maps one (low_pct, high_pct) percentile range of the
      whole image to [0, 255] with the SAME gain for all three channels, so it
      never changes hue (a per-channel stretch would shift skin colour).
    * JPEG artefact reduction is a small edge-preserving bilateral filter
      (assumed interpretation of the tracker's "JPEG artefacts"; revisit in ablation).
    """
    _check_rgb(rgb)
    out = rgb
    lo, hi = np.percentile(out, [low_pct, high_pct])
    if hi - lo > 1e-6:
        out = np.clip((out.astype(np.float32) - lo) * (255.0 / (hi - lo)), 0, 255).astype(np.uint8)
    if denoise:
        out = cv2.bilateralFilter(out, d=5, sigmaColor=25, sigmaSpace=5)
    return out


@dataclass(frozen=True)
class LetterboxInfo:
    """How an image was fitted into a square, so results can be mapped back."""

    scale: float
    pad_left: int
    pad_top: int
    orig_h: int
    orig_w: int
    new_h: int
    new_w: int


def letterbox(rgb: np.ndarray, size: int = 512, fill: int = 0) -> tuple[np.ndarray, LetterboxInfo]:
    """Model-side resize: uniform downscale/upscale to fit size x size, black borders.

    The aspect ratio is preserved (same scale on both axes), so lengths and areas
    can be recovered exactly with the returned info. Not part of calibration.
    """
    _check_rgb(rgb)
    h, w = rgb.shape[:2]
    scale = size / max(h, w)
    new_h, new_w = max(1, round(h * scale)), max(1, round(w * scale))
    interp = cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC
    resized = cv2.resize(rgb, (new_w, new_h), interpolation=interp)
    top, left = (size - new_h) // 2, (size - new_w) // 2
    canvas = np.full((size, size, 3), fill, dtype=np.uint8)
    canvas[top : top + new_h, left : left + new_w] = resized
    return canvas, LetterboxInfo(scale, left, top, h, w, new_h, new_w)


def unletterbox(arr: np.ndarray, info: LetterboxInfo, interpolation: int = cv2.INTER_NEAREST) -> np.ndarray:
    """Map a square model output (e.g. a mask) back to the original height and width."""
    crop = arr[info.pad_top : info.pad_top + info.new_h, info.pad_left : info.pad_left + info.new_w]
    return cv2.resize(crop, (info.orig_w, info.orig_h), interpolation=interpolation)


def final_normalise(rgb: np.ndarray, mode: str = "imagenet") -> np.ndarray:
    """Step 7 (model side): uint8 RGB -> float32 model input of the same shape.

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
