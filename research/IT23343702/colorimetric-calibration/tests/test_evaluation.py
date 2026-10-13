"""Unit tests for the evaluation harness (T23)."""

import numpy as np
import pytest

from colorimetric.evaluation import (
    DARK_GROUP,
    LIGHT_GROUP,
    auroc,
    cci_reduction,
    colour_consistency,
    evaluate_fairness,
    fst_to_group,
    gate_metrics,
    mean_skin_L,
    sensitivity_specificity,
)


def test_auroc_perfect_and_inverted():
    y = np.array([0, 0, 1, 1])
    assert auroc(y, np.array([0.1, 0.2, 0.8, 0.9])) == 1.0
    assert auroc(y, np.array([0.9, 0.8, 0.2, 0.1])) == 0.0


def test_auroc_ties_give_half_credit():
    assert auroc(np.array([0, 1]), np.array([0.5, 0.5])) == 0.5


def test_auroc_matches_pair_counting():
    rng = np.random.default_rng(0)
    y = rng.integers(0, 2, 200)
    s = rng.normal(size=200) + y
    pos, neg = s[y == 1], s[y == 0]
    expected = np.mean([(p > n) + 0.5 * (p == n) for p in pos for n in neg])
    assert auroc(y, s) == pytest.approx(expected)


def test_auroc_single_class_is_nan():
    assert np.isnan(auroc(np.ones(5), np.linspace(0, 1, 5)))


def test_sensitivity_specificity():
    y = np.array([1, 1, 0, 0])
    s = np.array([0.9, 0.2, 0.1, 0.7])
    assert sensitivity_specificity(y, s) == (0.5, 0.5)


def test_fst_to_group():
    assert [fst_to_group(f) for f in ("I", "III", "IV", "VI")] == [LIGHT_GROUP, LIGHT_GROUP, DARK_GROUP, DARK_GROUP]
    with pytest.raises(ValueError):
        fst_to_group("VII")


def _two_group_data(dark_noise: float):
    rng = np.random.default_rng(1)
    n = 2000
    y = rng.integers(0, 2, n)
    groups = np.where(np.arange(n) % 2 == 0, LIGHT_GROUP, DARK_GROUP)
    noise = np.where(groups == DARK_GROUP, dark_noise, 0.3)
    score = 1 / (1 + np.exp(-(2 * (y - 0.5) + rng.normal(size=n) * noise * 3)))
    return y, score, groups


def test_biased_model_has_bigger_gaps_than_fair_model():
    fair = evaluate_fairness(*_two_group_data(0.3))
    biased = evaluate_fairness(*_two_group_data(1.2))
    assert fair.auroc_gap < 0.03
    assert biased.auroc_gap > fair.auroc_gap + 0.05
    assert biased.groups[DARK_GROUP].auroc < biased.groups[LIGHT_GROUP].auroc
    assert biased.equalized_odds_gap >= max(biased.sensitivity_gap, biased.specificity_gap) - 1e-12


def test_meets_targets_flag_and_row():
    y = np.array([0, 0, 1, 1] * 2)
    s = np.array([0.1, 0.2, 0.8, 0.9] * 2)
    g = np.array([LIGHT_GROUP] * 4 + [DARK_GROUP] * 4)
    rep = evaluate_fairness(y, s, g)
    assert rep.meets_targets is True
    row = rep.as_row()
    assert row["AUROC FST IV-VI"] == 1.0 and row["Meets targets?"] == "Yes"


def test_length_mismatch_raises():
    with pytest.raises(ValueError):
        evaluate_fairness(np.zeros(3), np.zeros(2), np.array(["a"] * 3))


def test_colour_consistency_and_reduction():
    before = np.array([40.0, 50.0, 60.0, 70.0])
    after = np.array([54.0, 56.0, 58.0, 60.0])
    assert colour_consistency(before) > colour_consistency(after)
    assert 0 < cci_reduction(before, after) < 1
    assert cci_reduction(before, before) == pytest.approx(0.0)
    assert colour_consistency(np.array([np.nan, 50.0, 50.0])) == 0.0


def test_mean_skin_L_on_swatch_and_nonskin():
    skin = np.full((40, 40, 3), (230, 190, 170), dtype=np.uint8)  # inside the HSV skin mask (H=20, S=26%, V=90%)
    assert mean_skin_L(skin) == pytest.approx(80.0, abs=1.0)
    assert np.isnan(mean_skin_L(np.full((40, 40, 3), (0, 0, 255), dtype=np.uint8)))  # pure blue


def test_gate_metrics_counts():
    true = np.array([0, 1, 3, 5, 5, 2])      # light, light, dark, dark, dark, light
    ita = np.array([40.0, 10.0, 5.0, -20.0, 20.0, 35.0])
    g = gate_metrics(true, ita, cutoff=15.0)  # corrected: idx 1 (light), 2, 3 (dark)
    assert g.accuracy == pytest.approx(4 / 6)
    assert g.dark_recall == pytest.approx(2 / 3)
    assert g.light_false_correction == pytest.approx(1 / 3)
