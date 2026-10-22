"""Specular highlight removal (Step 5) and contrast enhancement (Step 6)."""

from __future__ import annotations

import cv2
import numpy as np

from .preprocess import _check_rgb


def specular_mask(rgb: np.ndarray, v_min: int = 240, s_max: int = 30, dilate_px: int = 2) -> np.ndarray:
    """Boolean mask of specular highlights: V > v_min and S < s_max (OpenCV 0-255 scale), dilated."""
    _check_rgb(rgb)
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    mask = ((hsv[..., 2] > v_min) & (hsv[..., 1] < s_max)).astype(np.uint8)
    if dilate_px > 0 and mask.any():
        k = 2 * dilate_px + 1
        mask = cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k)))
    return mask.astype(bool)


def remove_specular(rgb: np.ndarray, radius: int = 3, **mask_kwargs) -> np.ndarray:
    """Inpaint specular highlights with Telea's method (V>240 & S<30 by default)."""
    mask = specular_mask(rgb, **mask_kwargs)
    if not mask.any():
        return rgb.copy()
    bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    out = cv2.inpaint(bgr, mask.astype(np.uint8) * 255, radius, cv2.INPAINT_TELEA)
    return cv2.cvtColor(out, cv2.COLOR_BGR2RGB)


def clahe_median(
    rgb: np.ndarray,
    clip_limit: float = 2.0,
    tile_grid: int = 8,
    median_ksize: int = 3,
) -> np.ndarray:
    """CLAHE on the L* channel (clip 2.0, 8x8 tiles), then a k=3 median filter.

    Only L* is equalised, so a*/b* (and therefore skin hue and erythema) are untouched
    by the contrast step; the median filter is applied to the final RGB.
    """
    _check_rgb(rgb)
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB)
    clahe = cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=(tile_grid, tile_grid))
    lab[..., 0] = clahe.apply(lab[..., 0])
    out = cv2.cvtColor(lab, cv2.COLOR_LAB2RGB)
    return cv2.medianBlur(out, median_ksize) if median_ksize > 1 else out
