"""Illumination and colour-cast correction (Step 2 and ablation baselines).

Variants kept side by side for the ablation (none > Gray World > Retinex >
Retinex+CCM > ...): `gray_world`, `single_scale_retinex_L`,
`multi_scale_retinex_L`, and `white_balance`.
All functions take and return uint8 RGB images.
"""

from __future__ import annotations

import cv2
import numpy as np

from .preprocess import _check_rgb, lab_to_rgb, rgb_to_lab

DEFAULT_SIGMAS = (15, 80, 250)
# The guide defines these sigmas for 512 px images. Larger photos are blurred on a
# copy whose long side is WORK_SIZE and the smooth illumination estimate is scaled back
# up, which gives nearly the same result in a fraction of the time. None = exact full size.
DEFAULT_WORK_SIZE = 512


def gray_world(rgb: np.ndarray) -> np.ndarray:
    """Gray World baseline: scale each channel so all channel means equal the global mean."""
    _check_rgb(rgb)
    x = rgb.astype(np.float64)
    means = x.reshape(-1, 3).mean(axis=0)
    gains = means.mean() / np.maximum(means, 1e-6)
    return np.clip(x * gains, 0, 255).astype(np.uint8)


def _retinex_log(L: np.ndarray, sigmas: tuple[float, ...], work_size: int | None = DEFAULT_WORK_SIZE) -> np.ndarray:
    """Mean over `sigmas` of log(L+1) - log(illumination+1), at the full size of L.

    Illumination is a Gaussian blur of L. When the image is larger than `work_size`
    the blur is done on a downscaled copy (sigmas apply at that size) and the result
    is resized back up (bilinear): illumination is smooth, so little is lost.
    """
    h, w = L.shape
    L32 = L.astype(np.float32)
    scale = 1.0 if work_size is None else min(1.0, work_size / max(h, w))
    small = L32 if scale == 1.0 else cv2.resize(
        L32, (max(1, round(w * scale)), max(1, round(h * scale))), interpolation=cv2.INTER_AREA
    )
    log_l = np.log(L32 + 1.0)
    acc = np.zeros_like(L32)
    for sigma in sigmas:
        blur = cv2.GaussianBlur(small, (0, 0), sigma)
        if scale != 1.0:
            blur = cv2.resize(blur, (w, h), interpolation=cv2.INTER_LINEAR)
        acc += log_l - np.log(blur + 1.0)
    return (acc / len(sigmas)).astype(np.float64)


def _anchor_to_mean(R: np.ndarray, L: np.ndarray) -> np.ndarray:
    """Turn a log-ratio retinex output into L*, keeping the image's mean brightness.

    L_out = (mean(L) + 1) * exp(R) - 1, i.e. the retinex ratio L/illumination is
    applied as a gain around the original mean. A percentile re-stretch would
    re-amplify any residual shadow back to the full range, so it is not used.
    """
    return np.clip((L.mean() + 1.0) * np.exp(R) - 1.0, 0.0, 100.0)


def _apply_to_L(rgb: np.ndarray, retinex_fn) -> np.ndarray:
    lab = rgb_to_lab(rgb)
    L = lab[..., 0]
    lab[..., 0] = _anchor_to_mean(retinex_fn(L), L)
    return lab_to_rgb(lab)  # a*/b* untouched so colour (erythema) fidelity is kept


def single_scale_retinex_L(rgb: np.ndarray, sigma: float = 80.0, work_size: int | None = DEFAULT_WORK_SIZE) -> np.ndarray:
    """SSR on the L* channel only."""
    _check_rgb(rgb)
    return _apply_to_L(rgb, lambda L: _retinex_log(L, (sigma,), work_size))


def multi_scale_retinex_L(
    rgb: np.ndarray,
    sigmas: tuple[float, ...] = DEFAULT_SIGMAS,
    work_size: int | None = DEFAULT_WORK_SIZE,
) -> np.ndarray:
    """MSR (equal-weight mean over sigmas, default 15/80/250) on the L* channel only."""
    _check_rgb(rgb)
    return _apply_to_L(rgb, lambda L: _retinex_log(L, tuple(sigmas), work_size))


def white_balance(rgb: np.ndarray, percentile: float = 99.0, max_gain: float = 1.4) -> np.ndarray:
    """White-patch balance: scale channels so their bright-end percentiles match.

    Unlike Gray World this does not assume the scene averages to gray, which
    is wrong for close-ups that are mostly skin. Gains are limited to
    [1/max_gain, max_gain] so a bad estimate cannot wreck the image.
    """
    _check_rgb(rgb)
    x = rgb.astype(np.float64)
    ref = np.percentile(x.reshape(-1, 3), percentile, axis=0)
    gains = np.clip(ref.max() / np.maximum(ref, 1e-6), 1.0 / max_gain, max_gain)
    return np.clip(x * gains, 0, 255).astype(np.uint8)
