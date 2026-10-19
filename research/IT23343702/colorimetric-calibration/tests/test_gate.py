import json

import numpy as np
import pytest

from colorimetric.gate import FEATURE_NAMES, GATE_PATH, context_features, dark_probability, is_dark, load_gate


def skin(rgb, shape=(120, 160)):
    return np.full((*shape, 3), rgb, np.uint8)


def test_model_file_matches_feature_list():
    m = load_gate()
    n = len(FEATURE_NAMES)
    assert len(m.mean) == len(m.scale) == len(m.coef) == n
    assert 0 < m.threshold < 1


def test_feature_list_mismatch_raises(tmp_path):
    d = json.loads(GATE_PATH.read_text())
    d["features"] = d["features"][:-1]
    p = tmp_path / "g.json"
    p.write_text(json.dumps(d))
    with pytest.raises(ValueError):
        load_gate(p)


def test_no_skin_gives_none():
    blue = skin((20, 40, 200))
    assert context_features(blue) is None
    assert dark_probability(blue) is None
    assert is_dark(blue) is None


def test_dark_skin_scores_higher_than_light():
    light, dark = skin((224, 172, 150)), skin((110, 70, 50))
    assert dark_probability(dark) > dark_probability(light)
    assert is_dark(dark) is True  # flat synthetic patches: a sanity check, not an accuracy claim
    assert is_dark(light) is False


def test_probability_is_plain_logistic():
    m = load_gate()
    f = context_features(skin((150, 105, 85)))
    z = float(np.dot((f - m.mean) / m.scale, m.coef) + m.intercept)
    assert m.probability(f) == pytest.approx(1 / (1 + np.exp(-z)))


def test_rejects_bad_input():
    with pytest.raises(ValueError):
        context_features(np.zeros((10, 10), np.uint8))
