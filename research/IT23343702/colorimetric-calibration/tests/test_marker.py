"""Tests for the sticker grey-patch correction (marker.py)."""

import json
from pathlib import Path

import cv2
import numpy as np
import pytest

from colorimetric import CalibrationPipeline
from colorimetric import marker as M
from colorimetric.marker import (
    MarkerReference, apply_gains, correct_with_marker, estimate_gains, load_layout, load_reference,
)

LAYOUT = load_layout()
STAND_IN = load_reference()
DARK = (115, 82, 68)


def lab_rgb(lab):
    px = np.array(lab, dtype=np.float32).reshape(1, 1, 3)
    return tuple(int(round(v * 255)) for v in cv2.cvtColor(px, cv2.COLOR_LAB2RGB)[0, 0])


GREY = lab_rgb((50, 0, 0))
SKIN_A = lab_rgb((38, 14, 14))
SKIN_B = lab_rgb((25, 8, 8))


def sticker(scale=1.0, grey=GREY, skin_a=SKIN_A, skin_b=SKIN_B, size=500, noise=2.0, seed=0):
    """Straightened 50 mm sticker crop (10 px/mm) whose colours are multiplied by `scale`."""
    rng = np.random.default_rng(seed)
    img = np.full((size, size, 3), 20.0)
    img[size // 10: -size // 10, size // 10: -size // 10] = 240
    colours = {"grey": grey, "skin_a": skin_a, "skin_b": skin_b}
    for name, (cx, cy, s) in LAYOUT.patches.items():
        half = int(s * size / 2)
        img[int(cy * size) - half: int(cy * size) + half, int(cx * size) - half: int(cx * size) + half] = colours[name]
    img = img + rng.normal(0, noise, img.shape)
    return np.clip(img * scale, 0, 255).astype(np.uint8)


def photo(scale=1.0, h=90, w=160, seed=1):
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), DARK, dtype=np.float64) + rng.normal(0, 6, (h, w, 3))
    return np.clip(img * scale, 0, 255).astype(np.uint8)


def cast(rgb, ratio):
    """Grey as an uncorrected camera reads it under a light with R/B gain `ratio`."""
    lin = M._srgb_to_linear(np.array(rgb) / 255.0) * np.array([ratio ** 0.5, 1, ratio ** -0.5])
    return tuple(int(round(v)) for v in np.clip(M._linear_to_srgb(lin), 0, 1) * 255)


# ---- layout and reference files -------------------------------------------------------------

def test_layout_matches_cv_file_names_and_sizes():
    assert LAYOUT.names == ("grey", "skin_a", "skin_b") and LAYOUT.skin_names == ("skin_a", "skin_b")
    assert LAYOUT.patches["grey"] == pytest.approx((0.25, 0.5, 0.2))
    assert LAYOUT.patches["skin_b"][0] == pytest.approx(0.75)
    assert LAYOUT.sample_fraction == 0.6 and LAYOUT.calibration_mode == "relative"


def test_bundled_layout_equals_cv_source_of_truth_when_present():
    cv_file = Path(__file__).resolve().parents[3] / "IT23294066" / "area-measurement" / "marker_layout.json"
    if not cv_file.exists():
        pytest.skip("CV's marker_layout.json is not in this checkout")
    assert json.loads(cv_file.read_text()) == json.loads(M.LAYOUT_PATH.read_text())


def test_default_reference_is_stand_in_and_not_measured():
    assert M.MEASURED is False and STAND_IN.source == "stand-in"
    assert STAND_IN.lab["skin_b"][1] is None  # aim only: L* is all there is


def test_reference_loading_rejects_bad_files(tmp_path):
    def write(d):
        p = tmp_path / "ref.json"
        p.write_text(json.dumps(d))
        return p
    ok = {"source": "reference-photos", "lab": {"grey": [48, 1, 2]}}
    assert load_reference(write(ok)).source == "reference-photos"
    with pytest.raises(ValueError, match="source"):
        load_reference(write({**ok, "source": "guess"}))
    with pytest.raises(ValueError, match="MEASURED"):
        load_reference(write({**ok, "source": "measured"}))
    with pytest.raises(ValueError, match="grey"):
        load_reference(write({"source": "stand-in", "lab": {"skin_a": [38, 14, 14]}}))
    with pytest.raises(ValueError, match="grey"):
        load_reference(write({"source": "stand-in", "lab": {"grey": [50, None, None]}}))


# ---- gains ----------------------------------------------------------------------------------

def test_correctly_exposed_sticker_changes_almost_nothing():
    res = estimate_gains(sticker())
    assert res.used and np.allclose(res.gains, 1.0, atol=0.08)
    assert res.reference_source == "stand-in" and res.calibration_mode == "relative"
    assert res.label == "relative calibration, stand-in reference colours"


def test_underexposed_photo_is_lifted_and_skin_patches_recover():
    img, res = correct_with_marker(photo(scale=0.6), sticker(scale=0.6))
    assert res.used and res.gains.min() > 1.3
    assert img.mean() > photo(scale=0.6).mean()
    assert all(de < 12 for de in res.skin_delta_e.values())


def test_colour_cast_is_removed():
    warm = sticker(grey=cast(GREY, 1.6))
    corrected = apply_gains(warm, estimate_gains(warm).gains)
    cx, cy, _ = LAYOUT.patches["grey"]
    h = corrected.shape[0]
    px = corrected[int(cy * h) - 5: int(cy * h) + 5, int(cx * h) - 5: int(cx * h) + 5].reshape(-1, 3).mean(axis=0)
    assert np.ptp(px) < 4


@pytest.mark.parametrize("bad", ["clipped", "uneven", "tiny", "dark_patch", "stained"])
def test_unusable_marker_is_rejected_and_image_unchanged(bad):
    crop = {
        "clipped": sticker(grey=(255, 255, 255)),
        "uneven": sticker(noise=40.0),
        "tiny": sticker(size=14),
        "dark_patch": sticker(grey=(10, 10, 10)),
        "stained": sticker(grey=(150, 119, 70)),
    }[bad]
    img = photo()
    out, res = correct_with_marker(img, crop)
    assert not res.used and res.reason
    assert out is img


# ---- neutrality rule (new) ------------------------------------------------------------------

def test_neutrality_limit_sits_between_a_strong_cast_and_a_stained_patch():
    strong_cast = cast(GREY, 2.5)  # very warm lamp, uncorrected camera: a*b* error about 18
    assert estimate_gains(sticker(grey=strong_cast)).used
    stained = cast(GREY, 4.0)      # a*b* error well above the limit
    res = estimate_gains(sticker(grey=stained))
    assert not res.used and "not neutral" in res.reason


def test_neutrality_is_measured_from_the_reference_grey_not_from_zero():
    # a printed grey that is a little warm is fine if the reference photos say so
    warm_print = lab_rgb((50, 6, 12))
    ref = MarkerReference("reference-photos", {**STAND_IN.lab, "grey": (50.0, 6.0, 12.0)})
    assert estimate_gains(sticker(grey=warm_print), reference=ref).used


# ---- skin-patch error rule (new) ------------------------------------------------------------

def test_wrong_skin_patch_is_reported_but_not_rejected_for_a_stand_in_reference():
    crop = sticker(skin_a=lab_rgb((75, 10, 10)))  # nowhere near skin_a's aim
    res = estimate_gains(crop)
    assert res.used and res.skin_delta_e["skin_a"] > M.MAX_SKIN_DELTA_E


def test_wrong_skin_patch_is_rejected_for_reference_photo_targets():
    ref = MarkerReference("reference-photos", STAND_IN.lab)
    assert estimate_gains(sticker(), reference=ref).used
    res = estimate_gains(sticker(skin_a=lab_rgb((75, 10, 10))), reference=ref)
    assert not res.used and "skin_a" in res.reason and "dE" in res.reason


def test_skin_b_is_compared_on_lightness_only():
    ref = MarkerReference("reference-photos", STAND_IN.lab)
    res = estimate_gains(sticker(skin_b=lab_rgb((25, 20, -10))), reference=ref)  # odd hue, right L*
    assert res.used and res.skin_delta_e["skin_b"] < 5


def test_non_rgb_crop_raises():
    with pytest.raises(ValueError):
        estimate_gains(np.zeros((10, 10), dtype=np.uint8))


# ---- through the pipeline -------------------------------------------------------------------

def test_pipeline_uses_marker_and_says_what_it_rests_on():
    res = CalibrationPipeline().process(photo(scale=0.6), marker=sticker(scale=0.6))
    assert res.marker_used and res.to_json()["marker_used"] is True
    assert res.marker_label == "relative calibration, stand-in reference colours"
    assert res.to_json()["marker_label"] == res.marker_label
    assert any("relative calibration, stand-in" in n for n in res.notes)
    assert res.calibrated_image.shape == (90, 160, 3)


def test_pipeline_without_or_with_bad_marker_matches_fallback():
    img = photo()
    base = CalibrationPipeline().process(img)
    bad = CalibrationPipeline().process(img, marker=sticker(grey=(255, 255, 255)))
    assert not base.marker_used and not bad.marker_used
    assert base.marker_label is None and bad.marker_label is None
    assert np.array_equal(base.calibrated_image, bad.calibrated_image)
    assert any("marker rejected" in n for n in bad.notes)


def test_marker_brings_an_underexposed_photo_closer_to_the_well_exposed_skin_tone():
    reference = CalibrationPipeline().process(photo(scale=1.0), marker=sticker()).ita_angle
    dim = photo(scale=0.6)
    without = CalibrationPipeline().process(dim).ita_angle
    with_marker = CalibrationPipeline().process(dim, marker=sticker(scale=0.6)).ita_angle
    assert with_marker is not None and without is not None
    assert abs(with_marker - reference) < abs(without - reference)
