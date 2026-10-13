"""Tests for pipeline steps 1, 2, 5, 6, 7: preprocess, illumination, enhance."""

import cv2
import numpy as np
import pytest

from colorimetric.enhance import clahe_median, remove_specular, specular_mask
from colorimetric.illumination import (
    gray_world,
    multi_scale_retinex_L,
    single_scale_retinex_L,
    white_balance,
)
from colorimetric.preprocess import (
    final_normalise,
    lab_to_rgb,
    process_raw_input,
    rgb_to_lab,
)

SKIN = (200, 150, 120)


def skin_image(h=96, w=128, rgb=SKIN, noise=4, seed=0):
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), rgb, dtype=np.float64) + rng.normal(0, noise, (h, w, 3))
    return np.clip(img, 0, 255).astype(np.uint8)


def with_shadow(img, strength=0.55, sigma=18):
    """Darken a smooth round patch in the middle (a cast shadow / uneven light)."""
    h, w = img.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    bump = np.exp(-(((yy - h / 2) ** 2) + ((xx - w / 2) ** 2)) / (2 * sigma**2))
    return np.clip(img * (1 - strength * bump)[..., None], 0, 255).astype(np.uint8)


# --- LAB helpers (T31) -------------------------------------------------------

def test_lab_round_trip_within_tolerance():
    img = skin_image()
    back = lab_to_rgb(rgb_to_lab(img))
    assert np.abs(back.astype(int) - img.astype(int)).max() <= 2


def test_lab_ranges():
    lab = rgb_to_lab(skin_image())
    assert 0 <= lab[..., 0].min() and lab[..., 0].max() <= 100


def test_rejects_non_uint8_or_wrong_shape():
    with pytest.raises(ValueError):
        rgb_to_lab(np.zeros((4, 4, 3), dtype=np.float32))
    with pytest.raises(ValueError):
        process_raw_input(np.zeros((4, 4), dtype=np.uint8))


# --- Step 1 (T30) ------------------------------------------------------------

@pytest.mark.parametrize("shape", [(192, 256), (1000, 800), (512, 512)])
def test_process_raw_input_output_size_and_dtype(shape):
    out = process_raw_input(skin_image(*shape))
    assert out.shape == (512, 512, 3) and out.dtype == np.uint8


def test_process_raw_input_keeps_hue():
    """The same-gain stretch must not shift the R:G:B ordering/ratios much."""
    img = np.clip(skin_image(noise=10).astype(int) // 2 + 20, 0, 255).astype(np.uint8)
    out = process_raw_input(img)
    r, g, b = out.reshape(-1, 3).mean(axis=0)
    assert r > g > b


def test_process_raw_input_stretches_range():
    low = np.clip(skin_image(noise=15).astype(int) // 3, 0, 255).astype(np.uint8)
    out = process_raw_input(low, denoise=False)
    assert out.max() > low.max() * 2


# --- Illumination (T32-T35) --------------------------------------------------

def test_gray_world_equalises_channel_means():
    out = gray_world(skin_image())
    means = out.reshape(-1, 3).mean(axis=0)
    assert means.max() - means.min() < 2.0


def test_white_balance_reduces_cast_and_limits_gain():
    cast = skin_image(rgb=(255, 215, 150), noise=6)  # strong yellow cast, bright end uneven
    out = white_balance(cast)
    spread_before = np.ptp(np.percentile(cast.reshape(-1, 3), 99, axis=0))
    spread_after = np.ptp(np.percentile(out.reshape(-1, 3), 99, axis=0))
    assert spread_after < spread_before
    assert out.astype(float).mean() / cast.astype(float).mean() <= 1.45


def _shadow_dip(img):
    """Mean L* of the left margin minus mean L* of the shadow centre."""
    L = rgb_to_lab(img)[..., 0]
    h, w = L.shape
    return L[:, : w // 8].mean() - L[h // 2 - 10 : h // 2 + 10, w // 2 - 10 : w // 2 + 10].mean()


def _shaded():
    return with_shadow(skin_image(h=256, w=256, noise=2), sigma=40)


def test_ssr_with_small_sigma_removes_smooth_shadow():
    shaded = _shaded()
    assert _shadow_dip(single_scale_retinex_L(shaded, sigma=15)) < 0.5 * _shadow_dip(shaded)


def test_msr_reduces_shadow_and_keeps_mean_brightness():
    shaded = _shaded()
    out = multi_scale_retinex_L(shaded)
    assert _shadow_dip(out) < 0.85 * _shadow_dip(shaded)
    assert out.shape == shaded.shape and out.dtype == np.uint8
    assert abs(rgb_to_lab(out)[..., 0].mean() - rgb_to_lab(shaded)[..., 0].mean()) < 2.0


def test_retinex_touches_only_lightness():
    img = skin_image(noise=8)
    out = multi_scale_retinex_L(img)
    a0, a1 = rgb_to_lab(img)[..., 1], rgb_to_lab(out)[..., 1]
    assert abs(a0.mean() - a1.mean()) < 2.0


# --- Steps 5 and 6 (T40, T41) -------------------------------------------------

def test_specular_mask_finds_white_spot_only():
    img = skin_image()
    img[40:46, 60:66] = 255
    m = specular_mask(img, dilate_px=0)
    assert m[43, 63] and m.sum() == 36


def test_remove_specular_replaces_highlight_with_skin_tone():
    img = skin_image()
    img[40:46, 60:66] = 255
    out = remove_specular(img)
    assert out[43, 63].mean() < 230
    assert np.abs(out[43, 63].astype(int) - np.array(SKIN)).max() < 25


def test_remove_specular_noop_without_highlights():
    img = skin_image()
    assert np.array_equal(remove_specular(img), img)


def test_clahe_increases_contrast_and_preserves_hue():
    rng = np.random.default_rng(3)
    base = 120 + rng.normal(0, 3, (96, 96, 1))
    img = np.clip(base * np.array([1.0, 0.8, 0.7]), 0, 255).astype(np.uint8)
    out = clahe_median(img)
    assert rgb_to_lab(out)[..., 0].std() > rgb_to_lab(img)[..., 0].std()
    assert abs(rgb_to_lab(out)[..., 1].mean() - rgb_to_lab(img)[..., 1].mean()) < 3.0


# --- Step 7 (T42) -------------------------------------------------------------

def test_final_normalise_imagenet_and_zscore():
    img = skin_image()
    im = final_normalise(img, "imagenet")
    assert im.dtype == np.float32 and im.shape == img.shape
    z = final_normalise(img, "zscore")
    assert np.allclose(z.mean(axis=(0, 1)), 0, atol=1e-2)
    assert np.allclose(z.std(axis=(0, 1)), 1, atol=1e-3)
    with pytest.raises(ValueError):
        final_normalise(img, "nope")
