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
    normalise_skin_L_variance,
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


def test_kmeans_subsample_gives_nearly_the_same_centres():
    rng = np.random.default_rng(1)
    base = np.array([115, 82, 68], dtype=np.int16)
    img = np.clip(base + rng.integers(-12, 13, size=(400, 400, 3)), 0, 255).astype(np.uint8)
    cv2.setRNGSeed(0)
    full = sample_skin_lab_colors(img, k=3, max_pixels=None)
    cv2.setRNGSeed(0)
    sub = sample_skin_lab_colors(img, k=3, max_pixels=5_000)
    assert np.abs(full.mean(axis=0) - sub.mean(axis=0)).max() < 0.5


def _lab(img):
    lab = cv2.cvtColor(img, cv2.COLOR_RGB2LAB).astype(np.float64)
    return lab[..., 0] * 100.0 / 255.0, lab[..., 1] - 128.0, lab[..., 2] - 128.0


def test_relative_ccm_keeps_a_and_b_columns_identity():
    M = estimate_adaptive_ccm(_dark_skin_patch(), FitzpatrickType.VI, k=4)
    assert np.allclose(M[:, 1:], np.eye(3)[:, 1:], atol=1e-6)  # a*, b* (erythema) unchanged


def test_relative_ccm_leaves_skin_already_at_prior_alone():
    light = np.full((64, 64, 3), REFERENCE_PRIORS["light_skin"]["rgb"] + 15, dtype=np.uint8)
    M = estimate_adaptive_ccm(light, FitzpatrickType.VI, k=2, correct=True)
    assert np.allclose(M, np.eye(3), atol=1e-6)


def test_relative_ccm_does_not_blow_out_bright_regions():
    rng = np.random.default_rng(2)
    img = np.clip(np.array([90, 62, 50]) + rng.integers(-8, 9, size=(200, 200, 3)), 0, 255).astype(np.uint8)
    img[:20, :20] = (235, 225, 220)                    # bright patch (e.g. wound bed / dressing edge)
    cv2.setRNGSeed(0)
    v2 = apply_adaptive_ccm(img, estimate_adaptive_ccm(img, FitzpatrickType.VI, k=4))
    cv2.setRNGSeed(0)
    v1 = apply_adaptive_ccm(img, estimate_adaptive_ccm(img, FitzpatrickType.VI, k=4, shift="fixed"))
    assert (_lab(v1)[0] > 95).mean() > 0.005            # v1 clips the bright patch
    assert (_lab(v2)[0] > 95).mean() <= 0.011           # v2: at most ~1% above the ceiling
    assert _lab(v2)[0][20:, 20:].mean() > _lab(img)[0][20:, 20:].mean()  # skin still lifted


def test_unknown_shift_raises():
    with pytest.raises(ValueError):
        estimate_adaptive_ccm(_dark_skin_patch(), FitzpatrickType.VI, k=4, shift="nope")


def _textured_dark_skin(seed=3, spread=14):
    rng = np.random.default_rng(seed)
    return np.clip(np.array([110, 75, 60]) + rng.integers(-spread, spread + 1, size=(120, 120, 1)), 0, 255).astype(np.uint8)


def test_stratified_ccm_lifts_darker_types_more():
    img = _textured_dark_skin()
    lifts = []
    for fst in (FitzpatrickType.IV, FitzpatrickType.V, FitzpatrickType.VI):
        cv2.setRNGSeed(0)
        out = apply_adaptive_ccm(img, estimate_adaptive_ccm(img, fst, k=4))
        lifts.append(_lab(out)[0].mean() - _lab(img)[0].mean())
    assert 0 < lifts[0] < lifts[1] < lifts[2]


def test_l_variance_normalisation_hits_target_and_keeps_mean_and_a():
    img = _textured_dark_skin(spread=30)
    mask = np.ones(img.shape[:2], bool)
    out = normalise_skin_L_variance(img, mask, target_std=6.0, gain_range=(0.5, 2.0))
    L0, a0, _ = _lab(img)
    L1, a1, _ = _lab(out)
    assert abs(L1.std() - 6.0) < 0.6
    assert abs(L1.mean() - L0.mean()) < 0.5
    assert np.abs(a1 - a0).mean() < 1.0                 # FR12: a* kept (up to uint8 rounding)


def test_l_variance_gain_is_limited_and_needs_skin():
    img = _textured_dark_skin(spread=30)
    out = normalise_skin_L_variance(img, np.ones(img.shape[:2], bool), target_std=0.1)
    assert _lab(out)[0].std() >= 0.79 * _lab(img)[0].std()   # floor of the default gain range
    assert np.array_equal(normalise_skin_L_variance(img, np.zeros(img.shape[:2], bool)), img)
