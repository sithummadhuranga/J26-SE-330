"""Reference-session converter (reference.py), tested on synthetic photos only: no real printed sticker or card exists yet."""

import json
import sys
from functools import lru_cache
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from make_golden_vectors import camera, sticker  # noqa: E402

from colorimetric import marker as M
from colorimetric.reference import ReferenceRow, build_reference, linear_rgb_to_lab, patch_labs_from_card

PRINTED = {"grey": (53.0, 1.0, -1.0), "skin_a": (41.0, 16.0, 12.0), "skin_b": (27.0, 9.0, 7.0)}  # what the printer really made
LIGHTS = {"daylight": (1.0, 1.0, 1.0), "warm": (1.3, 1.0, 0.6), "cool": (0.8, 1.0, 1.4)}
CARD_SRGB = float(M._linear_to_srgb(0.18)) * 255.0  # an ideal 18% card


def card(gain, seed=0, size=40):
    rng = np.random.default_rng(seed)
    px = np.array([[camera([CARD_SRGB] * 3, gain, 1.5, rng) for _ in range(size)] for _ in range(size)])
    return np.rint(px).astype(np.uint8)


@lru_cache(maxsize=None)
def _row(phone, s, light, exposure, printed_key):
    printed = dict(printed_key)
    gain = tuple(np.array(LIGHTS[light]) * exposure)
    crop = sticker(gain=gain, seed=s, grey_lab=printed["grey"], skin_a_lab=printed["skin_a"], skin_b_lab=printed["skin_b"])
    return ReferenceRow(phone, f"S{s}", light, "1", patch_labs_from_card(crop, card(gain, seed=s)))


def photo_rows(phone="phoneA", stickers=3, printed=PRINTED, lights=("daylight", "warm", "cool")):
    exposure = {"daylight": 1.0, "warm": 0.6, "cool": 1.6}
    key = tuple(sorted(printed.items()))
    return [_row(phone, s, light, exposure[light], key) for s in range(stickers) for light in lights]


def test_card_itself_reads_as_l_49_5():
    lab = linear_rgb_to_lab(np.array([0.18, 0.18, 0.18]))
    assert lab[0] == pytest.approx(49.5, abs=0.05) and abs(lab[1]) < 0.01 and abs(lab[2]) < 0.01


def test_recovers_the_printed_colours_under_three_lights():
    ref = build_reference(photo_rows())["phoneA"]
    for name, true in PRINTED.items():
        assert ref.lab[name] == pytest.approx(true, abs=1.2), name


def test_good_prints_pass_the_grey_rule():
    ref = build_reference(photo_rows())["phoneA"]
    assert ref.keep_printed_grey, ref.reason


def test_uneven_prints_fail_the_grey_rule():
    rows = []
    for grey_l in (47.0, 52.0, 57.0):
        rows += photo_rows(stickers=1, printed={**PRINTED, "grey": (grey_l, 0.0, 0.0)}, lights=("daylight",))
    ref = build_reference(rows)["phoneA"]
    assert not ref.keep_printed_grey and "cut the grey" in ref.reason


def test_tinted_grey_fails_the_grey_rule():
    ref = build_reference(photo_rows(stickers=1, printed={**PRINTED, "grey": (50.0, 6.0, 6.0)}))["phoneA"]
    assert not ref.keep_printed_grey


def test_single_photo_cannot_pass():
    ref = build_reference(photo_rows(stickers=1, lights=("daylight",)))["phoneA"]
    assert not ref.keep_printed_grey and "fewer than 2" in ref.reason


def test_phones_are_kept_apart():
    rows = photo_rows("phoneA", stickers=1) + photo_rows("phoneB", stickers=1, printed={**PRINTED, "skin_a": (45.0, 16.0, 12.0)})
    out = build_reference(rows)
    assert set(out) == {"phoneA", "phoneB"} and out["phoneB"].lab["skin_a"][0] > out["phoneA"].lab["skin_a"][0] + 2


def test_clipped_card_is_refused():
    white = np.full((40, 40, 3), 255, np.uint8)
    with pytest.raises(ValueError, match="card clipped"):
        patch_labs_from_card(sticker(), white)


def test_bad_arrays_are_refused():
    with pytest.raises(ValueError):
        patch_labs_from_card(sticker().astype(np.float32), card((1, 1, 1)))


def test_output_loads_as_a_marker_reference(tmp_path):
    ref = build_reference(photo_rows())["phoneA"]
    p = tmp_path / "ref.json"
    p.write_text(json.dumps(ref.reference_json()))
    loaded = M.load_reference(p)
    assert loaded.source == "reference-photos" and set(loaded.lab) == {"grey", "skin_a", "skin_b"}
    # the corrected sticker reads close to its own reference
    gain = LIGHTS["warm"]
    crop = sticker(gain=gain, seed=9, grey_lab=PRINTED["grey"], skin_a_lab=PRINTED["skin_a"], skin_b_lab=PRINTED["skin_b"])
    res = M.estimate_gains(crop, reference=loaded)
    assert res.used and max(res.skin_delta_e.values()) < 3.0


def test_cli_writes_one_reference_per_phone_and_a_report(tmp_path):
    import csv

    import cv2

    from reference_from_photos import main

    rows = []
    for k, (light, e) in enumerate((("daylight", 1.0), ("warm", 0.6))):
        gain = tuple(np.array(LIGHTS[light]) * e)
        for name, img in (("sticker", sticker(gain=gain, seed=k, grey_lab=PRINTED["grey"], skin_a_lab=PRINTED["skin_a"],
                                              skin_b_lab=PRINTED["skin_b"])), ("card", card(gain, seed=k))):
            cv2.imwrite(str(tmp_path / f"{name}{k}.png"), cv2.cvtColor(img, cv2.COLOR_RGB2BGR))
        rows.append(["phoneA", "S0", light, "1", f"sticker{k}.png", f"card{k}.png"])
    rows.append(["phoneA", "S0", "cool", "1", "missing.png", "card0.png"])
    with (tmp_path / "m.csv").open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["phone", "sticker_id", "light", "shot", "sticker_crop", "card_crop"])
        w.writerows(rows)
    assert main(["--manifest", str(tmp_path / "m.csv"), "--out", str(tmp_path / "out")]) == 0
    report = json.loads((tmp_path / "out" / "reference_report.json").read_text())
    assert report["rows_used"] == 2 and len(report["rows_dropped"]) == 1 and "cannot read" in report["rows_dropped"][0]["reason"]
    assert M.load_reference(tmp_path / "out" / "marker_reference_phoneA.json").source == "reference-photos"
