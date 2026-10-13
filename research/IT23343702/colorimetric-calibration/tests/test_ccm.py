"""Unit tests for the adaptive 3x3 CCM (Pipeline Step 4)."""

import cv2
import numpy as np
import pytest

from colorimetric.ccm import (
    REFERENCE_PRIORS,
    apply_adaptive_ccm,
    apply_ccm,
    compute_ccm,
    estimate_adaptive_ccm,
    sample_skin_lab_colors,
)
from colorimetric.ita import FitzpatrickType


def test_reference_priors_have_rgb_and_lab():
    for name in ("dark_skin", "light_skin"):
        assert "rgb" in REFERENCE_PRIORS[name]
        assert "lab" in REFERENCE_PRIORS[name]
        assert REFERENCE_PRIORS[name]["rgb"].shape == (3,)
        assert REFERENCE_PRIORS[name]["lab"].shape == (3,)

    # light skin should have higher L* than dark skin
    assert REFERENCE_PRIORS["light_skin"]["lab"][0] > REFERENCE_PRIORS["dark_skin"]["lab"][0]


def test_compute_ccm_recovers_exact_linear_mapping():
    rng = np.random.default_rng(0)
    true_M = np.array([[1.1, 0.0, 0.0], [0.0, 0.95, 0.05], [0.0, 0.0, 1.0]])
    source = rng.uniform(0, 100, size=(10, 3))
    target = source @ true_M

    fitted_M = compute_ccm(source, target)
    reconstructed = apply_ccm(source, fitted_M)

    assert reconstructed == pytest.approx(target, abs=1e-6)


def test_compute_ccm_shape_mismatch_raises():
    with pytest.raises(ValueError):
        compute_ccm(np.zeros((5, 3)), np.zeros((4, 3)))


def _dark_skin_patch(size=64):
    rgb = REFERENCE_PRIORS["dark_skin"]["rgb"].astype(np.uint8)
    patch = np.zeros((size, size, 3), dtype=np.uint8)
    patch[:, :] = rgb
    return patch


def test_sample_skin_lab_colors_returns_k_clusters():
    patch = _dark_skin_patch()
    colors = sample_skin_lab_colors(patch, k=4)
    assert colors.shape == (4, 3)


def test_estimate_adaptive_ccm_none_for_light_fst():
    patch = _dark_skin_patch()
    for fst in (FitzpatrickType.I, FitzpatrickType.II, FitzpatrickType.III):
        assert estimate_adaptive_ccm(patch, fst) is None


def test_estimate_adaptive_ccm_returns_matrix_for_dark_fst():
    patch = _dark_skin_patch()
    for fst in (FitzpatrickType.IV, FitzpatrickType.V, FitzpatrickType.VI):
        M = estimate_adaptive_ccm(patch, fst, k=4)
        assert M.shape == (3, 3)


def test_apply_adaptive_ccm_lightens_dark_skin_patch():
    patch = _dark_skin_patch()
    M = estimate_adaptive_ccm(patch, FitzpatrickType.VI, k=4)
    corrected = apply_adaptive_ccm(patch, M)

    original_L = cv2.cvtColor(patch, cv2.COLOR_RGB2LAB)[..., 0].mean()
    corrected_L = cv2.cvtColor(corrected, cv2.COLOR_RGB2LAB)[..., 0].mean()

    # correction targets the light-skin reference, so mean L* should increase
    assert corrected_L > original_L


def test_gate_override_forces_or_skips_correction():
    patch = _dark_skin_patch()
    assert estimate_adaptive_ccm(patch, FitzpatrickType.VI, k=4, correct=False) is None
    assert estimate_adaptive_ccm(patch, FitzpatrickType.I, k=4, correct=True) is not None
    # default (None) keeps the FST rule
    assert estimate_adaptive_ccm(patch, FitzpatrickType.I, k=4) is None
