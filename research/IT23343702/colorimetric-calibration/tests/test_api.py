"""Tests for the CalibrationPipeline API (proposal Table 3; FR1, FR8, FR9, FR11, NFR7)."""

import json

import numpy as np
import pytest

from colorimetric.api import ALL_STAGES, CalibrationPipeline, PipelineStage, config_from_stages, stages_of
from colorimetric.pipeline import ABLATION_VARIANTS, PipelineConfig, process_image

DARK = (115, 82, 68)


def skin(h, w, rgb=DARK, seed=0):
    rng = np.random.default_rng(seed)
    img = np.full((h, w, 3), rgb, dtype=np.float64) + rng.normal(0, 6, (h, w, 3))
    return np.clip(img, 0, 255).astype(np.uint8)


def test_default_runs_all_seven_stages_and_returns_contract_fields():
    img = skin(90, 160)
    res = CalibrationPipeline().process(img)
    assert res.calibrated_image.shape == img.shape and res.calibrated_image.dtype == np.uint8
    assert res.model_tensor.shape == (512, 512, 3) and res.model_tensor.dtype == np.float32
    assert res.fitzpatrick_class in ("IV", "V", "VI") and res.ccm_applied
    for key in ("1_raw_input", "2_msr", "4_ccm_fit", "5_specular", "6_clahe", "7_final", "total"):
        assert key in res.stage_timing
    assert res.stage_timing["total"] == pytest.approx(sum(v for k, v in res.stage_timing.items() if k != "total"))


def test_same_output_as_process_image():
    img = skin(64, 64)
    assert np.array_equal(CalibrationPipeline().process(img).calibrated_image, process_image(img).image)


@pytest.mark.parametrize("stage", list(PipelineStage))
def test_each_stage_can_be_switched_off(stage):
    res = CalibrationPipeline(enabled_stages=ALL_STAGES - {stage}).process(skin(64, 96))
    assert res.calibrated_image.shape == (64, 96, 3)
    if stage is PipelineStage.FINAL_NORMALISATION:
        assert res.model_tensor is None and "7_final" not in res.stage_timing
    if stage in (PipelineStage.SKIN_TONE, PipelineStage.CCM):
        assert not res.ccm_applied
    if stage is PipelineStage.SKIN_TONE:
        assert res.fitzpatrick_class is None


def test_no_stages_is_identity():
    img = skin(48, 48)
    res = CalibrationPipeline(enabled_stages=set()).process(img)
    assert np.array_equal(res.calibrated_image, img) and res.model_tensor is None


def test_stages_round_trip_through_config():
    for name, cfg in ABLATION_VARIANTS.items():
        assert stages_of(config_from_stages(stages_of(cfg), cfg)) == stages_of(cfg), name
    assert CalibrationPipeline(ABLATION_VARIANTS["retinex"]).config.use_ccm is False


def test_wound_bbox_is_accepted_and_shape_kept():
    img = skin(100, 100)
    img[30:70, 30:70] = (150, 20, 30)
    res = CalibrationPipeline().process(img, wound_bbox=(30, 30, 40, 40))
    assert res.calibrated_image.shape == img.shape


def test_to_json_is_serialisable_and_has_no_image_data():
    data = CalibrationPipeline().process(skin(64, 64)).to_json()
    text = json.dumps(data)
    assert set(data) == {"fitzpatrick_class", "ita_angle", "ccm_applied", "stage_timing_ms", "notes"}
    assert len(text) < 2000


def test_bad_arguments_raise():
    with pytest.raises(ValueError):
        CalibrationPipeline(normalise_mode="nope")
    with pytest.raises(ValueError):
        CalibrationPipeline(enabled_stages={"not a stage"})
