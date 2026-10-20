"""Tests for process_image() (T43)."""

import numpy as np
import pytest

from colorimetric.pipeline import ABLATION_VARIANTS, PipelineConfig, process_image, with_gate

DARK = (115, 82, 68)    # ColorChecker "dark skin": ITA far below 0
LIGHT = (235, 200, 185)  # light skin: ITA well above 28


def skin(h, w, rgb, seed=0):
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), rgb, dtype=np.float64) + rng.normal(0, 6, (h, w, 3))
    return np.clip(img, 0, 255).astype(np.uint8)


@pytest.mark.parametrize("shape", [(120, 200), (200, 120), (97, 131)])
def test_shape_and_dtype_preserved(shape):
    res = process_image(skin(*shape, DARK))
    assert res.image.shape == (*shape, 3) and res.image.dtype == np.uint8


def test_dark_skin_gets_ccm_and_light_skin_does_not():
    dark = process_image(skin(128, 128, DARK))
    light = process_image(skin(128, 128, LIGHT))
    assert dark.fst in ("IV", "V", "VI") and dark.ccm_applied
    assert light.fst in ("I", "II", "III") and not light.ccm_applied


def test_ita_is_read_from_the_raw_image_by_default():
    img = skin(128, 128, DARK)
    from colorimetric.ita import classify_image
    assert process_image(img).ita_angle == pytest.approx(classify_image(img).ita_angle)


def test_no_skin_does_not_crash_and_skips_ccm():
    blue = np.full((64, 64, 3), (0, 0, 255), dtype=np.uint8)
    res = process_image(blue)
    assert res.ita_angle is None and res.fst is None and not res.ccm_applied
    assert res.image.shape == blue.shape
    assert any("not estimated" in n for n in res.notes)


def test_repeatable_output():
    img = skin(128, 128, DARK)
    assert np.array_equal(process_image(img).image, process_image(img).image)


def test_gate_cutoff_changes_decision():
    # an image whose ITA sits between the two cut-offs
    img = skin(128, 128, (200, 160, 140))
    base = process_image(img)
    mid = base.ita_angle
    cfg = PipelineConfig(gate_cutoff=mid + 1)
    assert process_image(img, cfg).ccm_applied
    assert not process_image(img, with_gate(cfg, mid - 1)).ccm_applied


def test_timings_logged_and_total_is_sum():
    res = process_image(skin(128, 128, DARK))
    for key in ("ita", "1_raw_input", "2_msr", "5_specular", "6_clahe", "total"):
        assert key in res.timings_ms
    assert res.timings_ms["total"] == pytest.approx(sum(v for k, v in res.timings_ms.items() if k != "total"))


@pytest.mark.parametrize("name", list(ABLATION_VARIANTS))
def test_every_ablation_variant_runs_and_keeps_shape(name):
    img = skin(90, 140, DARK)
    res = process_image(img, ABLATION_VARIANTS[name])
    assert res.image.shape == img.shape


def test_none_variant_is_identity():
    img = skin(64, 64, DARK)
    assert np.array_equal(process_image(img, ABLATION_VARIANTS["none"]).image, img)


def test_bad_config_values_raise():
    img = skin(32, 32, DARK)
    with pytest.raises(ValueError):
        process_image(img, PipelineConfig(illumination="x"))
    with pytest.raises(ValueError):
        process_image(img, PipelineConfig(classify_on="x"))


def test_ablation_has_proposal_table1_configurations():
    for name in ("none", "gray_world", "ssr", "retinex", "retinex_ccm", "full"):
        assert name in ABLATION_VARIANTS
    res = process_image(skin(64, 64, DARK), ABLATION_VARIANTS["ssr"])
    assert "2_ssr" in res.timings_ms and "2_msr" not in res.timings_ms


def test_default_gate_is_the_ita_rule():
    assert PipelineConfig().gate == "ita"
    assert ABLATION_VARIANTS["full_model_gate"].gate == "model"


def test_unknown_gate_raises():
    with pytest.raises(ValueError):
        process_image(skin(64, 64, DARK), PipelineConfig(gate="nope"))


def test_model_gate_decides_the_ccm(monkeypatch):
    cfg = PipelineConfig(gate="model")
    dark, light = skin(128, 128, DARK), skin(128, 128, LIGHT)
    monkeypatch.setattr("colorimetric.pipeline.is_dark", lambda rgb: False)
    assert not process_image(dark, cfg).ccm_applied  # the ITA rule alone would have corrected this one
    monkeypatch.setattr("colorimetric.pipeline.is_dark", lambda rgb: True)
    assert process_image(light, cfg).ccm_applied


def test_model_gate_on_the_real_model_corrects_dark_skin():
    res = process_image(skin(128, 128, DARK), PipelineConfig(gate="model"))
    assert res.ccm_applied


def test_model_gate_falls_back_to_ita_when_it_cannot_read(monkeypatch):
    monkeypatch.setattr("colorimetric.pipeline.is_dark", lambda rgb: None)
    res = process_image(skin(128, 128, DARK), PipelineConfig(gate="model"))
    assert res.ccm_applied and any("using the ITA rule" in n for n in res.notes)
