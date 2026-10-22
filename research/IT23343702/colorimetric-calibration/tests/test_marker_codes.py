"""Stable reason codes next to the free-text reason of a rejected sticker (capture quality gate, message 10/11).

The text stays as it is (tests/test_marker.py and the golden vectors read it); the code is what logic and the app use.
"""

import cv2
import pytest

from colorimetric import marker as M

G = __import__("pathlib").Path(__file__).parent / "golden"


def read(rel):
    return cv2.cvtColor(cv2.imread(str(G / rel)), cv2.COLOR_BGR2RGB)


@pytest.mark.parametrize("crop,code", [
    ("r01_grey_clipped", "grey_clipped"),
    ("r02_grey_uneven", "grey_not_uniform"),
    ("r03_too_small", "crop_invalid"),
    ("r04_grey_not_neutral", "grey_not_neutral"),
    ("r05_gain_out_of_range", "grey_gain_out_of_range"),
])
def test_every_rejection_has_its_code(crop, code):
    import json
    e = json.loads((G / "expected.json").read_text())["grey_gains"][crop]
    ref = M.load_reference(G / e["reference"]) if e["reference"].endswith(".json") else None
    r = M.estimate_gains(read(f"crops/{crop}.png"), reference=ref)
    assert not r.used and r.code == code


def test_skin_patch_off_has_its_code():
    import json
    e = json.loads((G / "expected.json").read_text())["grey_gains"]["r06_skin_patch_off"]
    r = M.estimate_gains(read("crops/r06_skin_patch_off.png"), reference=M.load_reference(G / e["reference"]))
    assert not r.used and r.code == "skin_patch_off"


def test_an_accepted_sticker_has_no_code():
    r = M.estimate_gains(read("crops/c01_reference_colours.png"))
    assert r.used and r.code == ""


def test_the_code_list_is_the_one_agreed_with_cv():
    assert M.MARKER_CODES == ("grey_clipped", "grey_not_uniform", "grey_not_neutral", "grey_gain_out_of_range", "skin_patch_off", "crop_invalid")


def test_every_code_can_be_produced():
    """A code nobody can reach is a dead promise to the other side."""
    import json
    seen = set()
    for name, e in json.loads((G / "expected.json").read_text())["grey_gains"].items():
        if e["used"]:
            continue
        ref = M.load_reference(G / e["reference"]) if e["reference"].endswith(".json") else None
        seen.add(M.estimate_gains(read(f"crops/{name}.png"), reference=ref).code)
    assert seen == set(M.MARKER_CODES)
