"""Golden vectors for the Dart port (tests/golden/, made by scripts/make_golden_vectors.py).

If one of these fails, the spec of the light version changed: regenerate the vectors on purpose
and tell the Dart side.
"""

import json
from pathlib import Path

import cv2
import numpy as np
import pytest

from colorimetric import marker as M
from colorimetric.ita import classify_image

G = Path(__file__).parent / "golden"
EXP = json.loads((G / "expected.json").read_text())


def read(rel):
    return cv2.cvtColor(cv2.imread(str(G / rel)), cv2.COLOR_BGR2RGB)


@pytest.mark.parametrize("name", [k for k, v in EXP["grey_gains"].items() if v["used"]])
def test_grey_gains_accepted(name):
    e = EXP["grey_gains"][name]
    r = M.estimate_gains(read(f"crops/{name}.png"))
    assert r.used and r.label == e["label"]
    assert r.gains == pytest.approx(e["gains"], abs=e["gains_tolerance"])
    for k, v in e["skin_delta_e"].items():
        assert r.skin_delta_e[k] == pytest.approx(v, abs=0.1)


@pytest.mark.parametrize("name", [k for k, v in EXP["grey_gains"].items() if not v["used"]])
def test_grey_gains_rejected(name):
    e = EXP["grey_gains"][name]
    ref = M.load_reference(G / e["reference"]) if e["reference"].endswith(".json") else None
    r = M.estimate_gains(read(f"crops/{name}.png"), reference=ref)
    assert not r.used and e["reason_contains"] in r.reason


@pytest.mark.parametrize("name", list(EXP["skin_type"]))
def test_skin_type(name):
    e, img = EXP["skin_type"][name], read(f"photos/{name}.png")
    if e["error"]:
        with pytest.raises(ValueError):
            classify_image(img)
        return
    r = classify_image(img)
    assert r.ita_angle == pytest.approx(e["ita_opencv8"], abs=0.01)
    assert r.fitzpatrick_type.value == e["fitzpatrick"]
    assert r.skin_pixel_count == e["skin_pixel_count"]
    assert (r.ita_angle <= 28.0) == e["gate_dark_at_28"]
    assert abs(e["ita_float"] - e["ita_opencv8"]) < e["tolerance"]["ita_deg"] * 3  # the two Lab routes agree
    if "with_v_min_35" in e:
        old = e["with_v_min_35"]
        if old["error"]:
            with pytest.raises(ValueError):
                classify_image(img, v_min=35.0)
        else:
            classify_image(img, v_min=35.0)


@pytest.mark.parametrize("name", list(EXP["apply_gains"]))
def test_apply_gains_exact(name):
    e = EXP["apply_gains"][name]
    out = M.apply_gains(read(e["input"]), np.array(e["gains"]))
    assert np.array_equal(out, read(e["output"]))
