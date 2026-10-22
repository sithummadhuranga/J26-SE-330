"""Unit tests for the ITA formula and Fitzpatrick classification boundaries.

Verifies compute_ita/classify_fitzpatrick against the exact table in the
research guide (Section 2), then exercises classify_image() on synthetic
RGB swatches for each FST band.
"""

import numpy as np
import pytest

from colorimetric.ita import (
    GATE_CUTOFF_FITTED,
    GATE_CUTOFF_STANDARD,
    needs_ccm,
    FitzpatrickType,
    classify_fitzpatrick,
    classify_image,
    compute_ita,
)


@pytest.mark.parametrize(
    "L, b, expected_ita",
    [
        (50.0, 1.0, 0.0),      # L == 50 -> ITA == 0 regardless of b
        (90.0, 1.0, 88.57),    # very light skin, near-vertical angle: atan(40/1)
        (10.0, 1.0, -88.57),   # very dark skin, near -90: atan(-40/1)
    ],
)
def test_compute_ita_known_values(L, b, expected_ita):
    assert compute_ita(L, b) == pytest.approx(expected_ita, abs=0.01)


@pytest.mark.parametrize(
    "ita, expected_fst",
    [
        (60.0, FitzpatrickType.I),
        (55.01, FitzpatrickType.I),
        (55.0, FitzpatrickType.II),  # 55 itself falls in the 41-55 (inclusive) band, per guide table
        (50.0, FitzpatrickType.II),
        (41.01, FitzpatrickType.II),
        (41.0, FitzpatrickType.III),
        (30.0, FitzpatrickType.III),
        (28.0, FitzpatrickType.IV),
        (15.0, FitzpatrickType.IV),
        (10.0, FitzpatrickType.V),
        (-20.0, FitzpatrickType.V),
        (-30.0, FitzpatrickType.VI),
        (-45.0, FitzpatrickType.VI),
    ],
)
def test_classify_fitzpatrick_boundaries(ita, expected_fst):
    assert classify_fitzpatrick(ita) == expected_fst


def _solid_rgb_patch(rgb_color, size=64):
    patch = np.zeros((size, size, 3), dtype=np.uint8)
    patch[:, :] = rgb_color
    return patch


@pytest.mark.parametrize(
    "rgb_color, expected_fst",
    [
        ((245, 192, 165), FitzpatrickType.I),   # very light skin tone
        ((142, 106, 98), FitzpatrickType.V),     # brown skin tone
        ((95, 58, 38), FitzpatrickType.VI),     # dark brown/black skin tone
    ],
)
def test_classify_image_on_synthetic_swatch(rgb_color, expected_fst):
    image = _solid_rgb_patch(rgb_color)
    result = classify_image(image, min_skin_pixels=10)
    assert result.fitzpatrick_type == expected_fst
    assert result.skin_pixel_count > 0


def test_classify_image_excludes_wound_bbox():
    image = _solid_rgb_patch((142, 106, 98), size=64)
    # Paint a "wound" region bright red inside the patch
    image[20:40, 20:40] = (200, 30, 30)

    result_with_exclusion = classify_image(image, wound_bbox=(20, 20, 20, 20), min_skin_pixels=10)
    # skin pixel count should reflect only non-wound HSV-matching pixels
    assert result_with_exclusion.skin_pixel_count > 0


def test_classify_image_raises_on_insufficient_skin_pixels():
    # Pure blue image will not match the skin HSV range at all
    image = _solid_rgb_patch((0, 0, 255))
    with pytest.raises(ValueError):
        classify_image(image)


def test_gate_matches_fst_bands_at_standard_cutoff():
    for ita in (60.0, 45.0, 30.0, 28.5):
        assert not needs_ccm(ita) and classify_fitzpatrick(ita).value in ("I", "II", "III")
    for ita in (28.0, 15.0, 0.0, -50.0):
        assert needs_ccm(ita) and classify_fitzpatrick(ita).value in ("IV", "V", "VI")


def test_fitted_gate_is_stricter_than_standard():
    assert GATE_CUTOFF_FITTED < GATE_CUTOFF_STANDARD
    assert needs_ccm(20.0) and not needs_ccm(20.0, GATE_CUTOFF_FITTED)


# ---- skin mask lower brightness limit (V) ---------------------------------------------------

def _patch(rgb, n=40):
    return np.full((n, n, 3), rgb, dtype=np.uint8)


def test_very_dark_skin_is_kept_by_default_but_not_by_the_guides_limit():
    from colorimetric.ita import SKIN_V_MIN_GUIDE, SKIN_V_MIN, skin_mask_hsv
    deep = _patch((69, 49, 41))  # HSV V = 27%: the darkest skin we care about
    assert skin_mask_hsv(deep).all()
    assert not skin_mask_hsv(deep, v_min=SKIN_V_MIN_GUIDE).any()
    assert SKIN_V_MIN < SKIN_V_MIN_GUIDE


def test_light_skin_mask_is_unchanged_by_the_lower_limit():
    from colorimetric.ita import SKIN_V_MIN_GUIDE, skin_mask_hsv
    for rgb in [(194, 150, 130), (230, 190, 170), (115, 82, 68)]:
        img = _patch(rgb)
        assert np.array_equal(skin_mask_hsv(img), skin_mask_hsv(img, v_min=SKIN_V_MIN_GUIDE))


def test_black_frame_and_deep_shadow_stay_out_of_the_mask():
    from colorimetric.ita import skin_mask_hsv
    for rgb in [(20, 20, 20), (30, 22, 18), (0, 0, 0)]:
        assert not skin_mask_hsv(_patch(rgb)).any()


def test_very_dark_skin_now_gets_a_class():
    from colorimetric.ita import classify_image, SKIN_V_MIN_GUIDE
    deep = _patch((69, 49, 41), 64)
    assert classify_image(deep).fitzpatrick_type.value in ("V", "VI")
    with pytest.raises(ValueError):
        classify_image(deep, v_min=SKIN_V_MIN_GUIDE)
