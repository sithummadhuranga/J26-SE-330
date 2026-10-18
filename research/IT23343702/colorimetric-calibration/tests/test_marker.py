"""Tests for the sticker grey-patch correction (marker.py)."""

import numpy as np
import pytest

from colorimetric import CalibrationPipeline
from colorimetric.marker import PATCH_CENTRES, PATCH_NAMES, PATCH_SIZE, REFERENCE_LAB, apply_gains, correct_with_marker, estimate_gains

DARK = (115, 82, 68)
GREY = (119, 119, 119)  # sRGB of L* 50


def sticker(scale=1.0, grey=GREY, size=200, noise=2.0, seed=0):
    """Straightened 40 mm sticker crop whose colours are multiplied by `scale` (exposure)."""
    rng = np.random.default_rng(seed)
    img = np.zeros((size, size, 3), dtype=np.float64)
    img[...] = 20
    img[size // 8: -size // 8, size // 8: -size // 8] = 240
    colours = {"grey": grey, "skin_1": (115, 82, 68), "skin_2": (194, 150, 130)}
    for name in PATCH_NAMES:
        cx, cy = PATCH_CENTRES[name]
        h = int(PATCH_SIZE * size / 2)
        img[int(cy * size) - h: int(cy * size) + h, int(cx * size) - h: int(cx * size) + h] = colours[name]
    img = img + rng.normal(0, noise, img.shape)
    return np.clip(img * scale, 0, 255).astype(np.uint8)


def photo(scale=1.0, h=90, w=160, seed=1):
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), DARK, dtype=np.float64) + rng.normal(0, 6, (h, w, 3))
    return np.clip(img * scale, 0, 255).astype(np.uint8)


def test_correctly_exposed_sticker_changes_almost_nothing():
    res = estimate_gains(sticker())
    assert res.used and np.allclose(res.gains, 1.0, atol=0.08)


def test_underexposed_photo_is_lifted_and_skin_patches_recover():
    # same scene, exposed 40% darker: the grey patch asks for a brighter image
    dark_crop = sticker(scale=0.6)
    img, res = correct_with_marker(photo(scale=0.6), dark_crop)
    assert res.used and res.gains.min() > 1.3
    assert img.mean() > photo(scale=0.6).mean()
    assert all(de < 12 for de in res.skin_delta_e.values())


def test_colour_cast_is_removed():
    warm = sticker(grey=(140, 119, 95))
    corrected = apply_gains(warm, estimate_gains(warm).gains)
    h = corrected.shape[0]
    cx, cy = PATCH_CENTRES["grey"]
    px = corrected[int(cy * h) - 5: int(cy * h) + 5, int(cx * h) - 5: int(cx * h) + 5].reshape(-1, 3).mean(axis=0)
    assert np.ptp(px) < 4


@pytest.mark.parametrize("bad", ["clipped", "uneven", "tiny", "wrong_patch"])
def test_unusable_marker_is_rejected_and_image_unchanged(bad):
    crop = {
        "clipped": sticker(grey=(255, 255, 255)),
        "uneven": sticker(noise=40.0),
        "tiny": sticker(size=12),
        "wrong_patch": sticker(grey=(10, 10, 10)),
    }[bad]
    img = photo()
    out, res = correct_with_marker(img, crop)
    assert not res.used and res.reason
    assert out is img


def test_non_rgb_crop_raises():
    with pytest.raises(ValueError):
        estimate_gains(np.zeros((10, 10), dtype=np.uint8))


def test_pipeline_uses_marker_and_reports_it():
    res = CalibrationPipeline().process(photo(scale=0.6), marker=sticker(scale=0.6))
    assert res.marker_used and res.to_json()["marker_used"] is True
    assert res.calibrated_image.shape == (90, 160, 3)


def test_pipeline_without_or_with_bad_marker_matches_fallback():
    img = photo()
    base = CalibrationPipeline().process(img)
    bad = CalibrationPipeline().process(img, marker=sticker(grey=(255, 255, 255)))
    assert not base.marker_used and not bad.marker_used
    assert np.array_equal(base.calibrated_image, bad.calibrated_image)
    assert any("marker rejected" in n for n in bad.notes)


def test_marker_restores_skin_tone_of_an_underexposed_photo():
    # the DFUC failure: the same skin exposed darker. Without a marker it is lost to the HSV
    # skin mask (V < 35%); with the grey patch it reads the same as the properly exposed photo.
    reference = CalibrationPipeline().process(photo(scale=1.0), marker=sticker()).ita_angle
    dim = photo(scale=0.6)
    assert CalibrationPipeline().process(dim).ita_angle is None
    with_marker = CalibrationPipeline().process(dim, marker=sticker(scale=0.6)).ita_angle
    assert with_marker is not None and abs(with_marker - reference) < 10
