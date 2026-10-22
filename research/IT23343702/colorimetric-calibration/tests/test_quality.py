"""Capture quality gate: decision table (finder code + marker code + skin pixels -> ok | warn | retake).

tests/golden/quality_expected.json is the spec and is shared with the Dart side (same pattern as the marker vectors).
Verdict is about using the photo for area and colour; the worst result wins; ties by table order.
"""

import inspect
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from colorimetric import marker as M
from colorimetric import quality as Q

SPEC = json.loads((Path(__file__).parent / "golden" / "quality_expected.json").read_text())


@pytest.mark.parametrize("case", SPEC["cases"], ids=[c["name"] for c in SPEC["cases"]])
def test_vectors(case):
    v = Q.decide(case["finder_code"], case["marker_code"], case["skin_pixels"])
    e = case["expected"]
    assert v.level == e["level"] and v.code == e["code"]
    assert [f.code for f in v.findings] == e["findings"]


def test_table_order_and_threshold_match_the_spec():
    assert list(Q.TABLE_ORDER) == SPEC["table_order"]
    assert Q.MIN_SKIN_PIXELS == SPEC["min_skin_pixels"]


def test_skin_threshold_is_the_one_the_skin_type_code_uses():
    from colorimetric.ita import classify_image
    assert inspect.signature(classify_image).parameters["min_skin_pixels"].default == Q.MIN_SKIN_PIXELS


def test_marker_codes_are_the_marker_modules_codes():
    marker_part = [c for c in Q.TABLE_ORDER if c in M.MARKER_CODES]
    assert set(marker_part) == set(M.MARKER_CODES)


def test_every_code_has_a_level_and_an_english_sentence():
    for code in Q.TABLE_ORDER:
        assert Q.LEVEL[code] in ("warn", "retake")
        assert 0 < len(Q.MESSAGE[code].split()) <= 14, code      # plain and short


def test_every_verdict_carries_a_message_for_the_primary_code():
    v = Q.decide("weak_frame_contrast", None, None)
    assert "Glare" in v.message and v.code == "weak_frame_contrast"
    assert Q.decide(None, "", 5000).message == ""


@pytest.mark.parametrize("args", [("nonsense", None, None), (None, "nonsense", None), (None, None, -1)])
def test_unknown_input_fails_loudly_instead_of_passing_a_bad_photo(args):
    with pytest.raises(ValueError):
        Q.decide(*args)


def test_a_finder_code_cannot_be_given_as_a_marker_code():
    with pytest.raises(ValueError):
        Q.decide(None, "mirrored", None)


def test_assess_takes_the_finder_diagnosis_and_a_marker_result_by_attribute():
    diag = SimpleNamespace(code="mirrored")
    marker = M.MarkerResult(False, "x", code="grey_clipped")
    v = Q.assess(diag, marker, skin_pixels=5000)
    assert v.level == "retake" and v.code == "mirrored" and [f.code for f in v.findings] == ["mirrored", "grey_clipped"]
    assert Q.assess(SimpleNamespace(code=None), M.MarkerResult(True), 5000).level == "ok"


def test_assess_without_a_marker_result_leaves_the_marker_out():
    assert Q.assess(SimpleNamespace(code=None), None, None).level == "ok"


def test_the_verdict_is_a_plain_dict_for_logs_and_json():
    d = Q.decide("cut_by_edge", None, None).to_dict()
    assert d == {"level": "retake", "code": "cut_by_edge", "message": Q.MESSAGE["cut_by_edge"],
                 "findings": [{"code": "cut_by_edge", "level": "retake", "message": Q.MESSAGE["cut_by_edge"]}]}
