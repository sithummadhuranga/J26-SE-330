"""Reference sticker: grey-patch exposure / white-balance correction (before pipeline Step 3).

The caller finds the sticker in the full-resolution photo, straightens it and passes the flat
crop (the whole sticker including the black frame). Colour is the only thing taken from it,
so the crop's size does not have to match the calibrated image's.

The grey patch fixes what raw-pixel ITA cannot: skin L* depends on how the camera exposed the
frame (DFUC: median skin L* 37 against 53 for the whole photo). Per-channel gains, applied in
linear light, make the grey read its known colour, which fixes exposure and white balance in
one go. The two skin patches are not used for the fit yet; their error after the correction is
reported as a check.

STATUS: the layout is a proposal (40 mm sticker, 5 mm frame, three 10 mm patches in a row) and
the reference colours are STAND-INS until the printed patches are measured (MEASURED = False).
Nothing computed with them is calibrated against the real sticker.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import cv2
import numpy as np

from .ccm import REFERENCE_PRIORS

MEASURED = False  # flip only once REFERENCE_LAB holds measured values of the printed patches

# Layout of the straightened crop as fractions of its width/height: 40 mm sticker, 5 mm black
# frame, three 10 mm patches side by side across the 30 mm white area.
PATCH_NAMES = ("grey", "skin_1", "skin_2")
PATCH_CENTRES = {"grey": (0.25, 0.5), "skin_1": (0.5, 0.5), "skin_2": (0.75, 0.5)}
PATCH_SIZE = 0.25
SAMPLE_FRACTION = 0.6  # central share of each patch that is read, to stay clear of edges

REFERENCE_LAB = {
    "grey": np.array([50.0, 0.0, 0.0]),
    "skin_1": REFERENCE_PRIORS["dark_skin"]["lab"],
    "skin_2": REFERENCE_PRIORS["light_skin"]["lab"],
}

MAX_GREY_L_STD = 5.0       # larger = glare, shadow or a crumpled sticker
MAX_CLIPPED_FRACTION = 0.05
GAIN_RANGE = (0.25, 4.0)   # about +-2 stops; beyond this the patch is probably not the grey
MIN_PATCH_PIXELS = 64


@dataclass
class MarkerResult:
    used: bool
    reason: str = ""                       # why the marker was rejected, if it was
    gains: np.ndarray | None = None        # per-channel linear gains (R, G, B)
    skin_delta_e: dict[str, float] = field(default_factory=dict)  # CIE76 after the correction


def _srgb_to_linear(x: np.ndarray) -> np.ndarray:
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def _linear_to_srgb(x: np.ndarray) -> np.ndarray:
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def _lab_to_linear_rgb(lab: np.ndarray) -> np.ndarray:
    px = np.asarray(lab, dtype=np.float32).reshape(1, 1, 3)
    return _srgb_to_linear(np.clip(cv2.cvtColor(px, cv2.COLOR_LAB2RGB)[0, 0].astype(np.float64), 0, 1))


def _patch_pixels(crop: np.ndarray, name: str) -> np.ndarray:
    h, w = crop.shape[:2]
    cx, cy = PATCH_CENTRES[name]
    half_w, half_h = PATCH_SIZE * SAMPLE_FRACTION * w / 2, PATCH_SIZE * SAMPLE_FRACTION * h / 2
    x0, x1 = int(round(cx * w - half_w)), int(round(cx * w + half_w))
    y0, y1 = int(round(cy * h - half_h)), int(round(cy * h + half_h))
    return crop[max(y0, 0):y1, max(x0, 0):x1].reshape(-1, 3)


def estimate_gains(crop: np.ndarray, grey_lab: np.ndarray | None = None) -> MarkerResult:
    """Read the grey patch of a straightened sticker crop and return the correction gains.

    `used` is False (with `reason`) when the patch looks unreliable; the caller then falls
    back to the skin-pixel behaviour.
    """
    if crop.dtype != np.uint8 or crop.ndim != 3 or crop.shape[2] != 3:
        raise ValueError("marker crop must be a uint8 RGB image of shape (H, W, 3)")
    grey = _patch_pixels(crop, "grey")
    if len(grey) < MIN_PATCH_PIXELS:
        return MarkerResult(False, f"grey patch has only {len(grey)} pixels")

    clipped = float(np.mean((grey.max(axis=1) >= 250) | (grey.min(axis=1) <= 5)))
    if clipped > MAX_CLIPPED_FRACTION:
        return MarkerResult(False, f"grey patch clipped ({clipped:.0%} of pixels)")
    L = cv2.cvtColor(grey.reshape(-1, 1, 3), cv2.COLOR_RGB2LAB)[..., 0].astype(np.float64) * (100.0 / 255.0)
    if L.std() > MAX_GREY_L_STD:
        return MarkerResult(False, f"grey patch not uniform (L* std {L.std():.1f})")

    measured = _srgb_to_linear(np.median(grey, axis=0) / 255.0)
    target = _lab_to_linear_rgb(REFERENCE_LAB["grey"] if grey_lab is None else grey_lab)
    gains = target / np.maximum(measured, 1e-6)
    if gains.min() < GAIN_RANGE[0] or gains.max() > GAIN_RANGE[1]:
        return MarkerResult(False, f"grey patch gains out of range ({gains.min():.2f}-{gains.max():.2f})")
    return MarkerResult(True, gains=gains)


def apply_gains(rgb: np.ndarray, gains: np.ndarray) -> np.ndarray:
    """Multiply linear-light RGB by per-channel gains; uint8 in, uint8 out (clipped)."""
    levels = _srgb_to_linear(np.arange(256) / 255.0)
    out = np.empty_like(rgb)
    for c in range(3):
        lut = np.clip(np.rint(_linear_to_srgb(np.clip(levels * gains[c], 0, 1)) * 255.0), 0, 255)
        out[..., c] = lut.astype(np.uint8)[rgb[..., c]]
    return out


def correct_with_marker(rgb: np.ndarray, crop: np.ndarray) -> tuple[np.ndarray, MarkerResult]:
    """Apply the grey-patch correction to `rgb`; returns (image, result).

    If the marker is rejected the image is returned unchanged and `result.used` is False.
    """
    res = estimate_gains(crop)
    if not res.used:
        return rgb, res
    corrected_crop = apply_gains(crop, res.gains)
    for name in PATCH_NAMES[1:]:
        lab = cv2.cvtColor(np.median(_patch_pixels(corrected_crop, name), axis=0).reshape(1, 1, 3).astype(np.uint8),
                           cv2.COLOR_RGB2LAB).astype(np.float64)[0, 0]
        lab = np.array([lab[0] * (100.0 / 255.0), lab[1] - 128.0, lab[2] - 128.0])
        res.skin_delta_e[name] = float(np.linalg.norm(lab - REFERENCE_LAB[name]))
    return apply_gains(rgb, res.gains), res
