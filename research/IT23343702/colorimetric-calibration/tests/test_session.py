"""Reference-session file names -> manifest for scripts/reference_from_photos.py.

Pattern agreed with CV (message 9): S<sticker number>_<A|B|C>_<shot>_<phone>.jpg, A/B/C = daylight / warm lamp /
cool lamp, the phone is the last part. Crops are matched by file stem: <stem>.png in a sticker folder and in a card folder.
"""

import csv
import importlib.util
from pathlib import Path

import cv2
import numpy as np
import pytest

from colorimetric import session as S

ROOT = Path(__file__).resolve().parents[1]


@pytest.mark.parametrize("name,expected", [
    ("S1_A_1_pixel7.jpg", ("S1", "A", "1", "pixel7")),
    ("S12_C_03_Galaxy_A52.JPG", ("S12", "C", "03", "Galaxy_A52")),   # the phone may contain underscores
    ("S2_B_2_redmi.jpeg", ("S2", "B", "2", "redmi")),
])
def test_parse_photo_name(name, expected):
    p = S.parse_photo_name(name)
    assert (p.sticker_id, p.light, p.shot, p.phone) == expected


@pytest.mark.parametrize("name", [
    "1_A_1_pixel7.jpg",        # no S
    "S1_D_1_pixel7.jpg",       # light must be A, B or C
    "S1_a_1_pixel7.jpg",       # lower case is not the agreed pattern
    "S1_A_1.jpg",              # no phone
    "S1_A_1_.jpg",             # empty phone
    "S1_A__pixel7.jpg",        # empty shot
    "S1_A_1_pixel7.png.txt",   # wrong extension
    "S1_A_1_pixel7.heic",      # not a format the converter reads
    "photo.jpg",
    "sub/S1_A_1_pixel7.jpg",   # a name, not a path
])
def test_parse_photo_name_rejects(name):
    with pytest.raises(ValueError):
        S.parse_photo_name(name)


def _img(path: Path, v=128):
    path.parent.mkdir(parents=True, exist_ok=True)
    cv2.imwrite(str(path), np.full((20, 20, 3), v, np.uint8))


def _session(tmp_path, names, with_sticker=None, with_card=None):
    photos, st, card = tmp_path / "photos", tmp_path / "sticker", tmp_path / "card"
    for n in names:
        _img(photos / n)
        stem = Path(n).stem
        if with_sticker is None or n in with_sticker:
            _img(st / f"{stem}.png")
        if with_card is None or n in with_card:
            _img(card / f"{stem}.png")
    return photos, st, card


def test_rows_use_paths_relative_to_the_manifest(tmp_path):
    photos, st, card = _session(tmp_path, ["S1_A_1_pixel7.jpg"])
    rows, problems = S.build_rows(photos, st, card, relative_to=tmp_path / "out")
    assert problems == []
    r = rows[0]
    assert (r["phone"], r["sticker_id"], r["light"], r["shot"]) == ("pixel7", "S1", "A", "1")
    assert (tmp_path / "out" / r["sticker_crop"]).resolve() == (st / "S1_A_1_pixel7.png").resolve()
    assert (tmp_path / "out" / r["card_crop"]).resolve() == (card / "S1_A_1_pixel7.png").resolve()


def test_missing_crops_and_bad_names_are_reported_not_dropped_silently(tmp_path):
    names = ["S1_A_1_p.jpg", "S2_A_1_p.jpg", "S3_A_1_p.jpg", "notes.jpg"]
    photos, st, card = _session(tmp_path, names, with_sticker={"S1_A_1_p.jpg", "S3_A_1_p.jpg"}, with_card={"S1_A_1_p.jpg", "S2_A_1_p.jpg"})
    rows, problems = S.build_rows(photos, st, card, relative_to=tmp_path)
    assert [r["sticker_id"] for r in rows] == ["S1"]
    why = {p["file"]: p["reason"] for p in problems}
    assert "sticker crop" in why["S2_A_1_p.jpg"]
    assert "card crop" in why["S3_A_1_p.jpg"]
    assert "pattern" in why["notes.jpg"]
    assert len(problems) == 3


def test_two_files_for_the_same_photo_are_a_problem(tmp_path):
    photos, st, card = _session(tmp_path, ["S1_A_1_p.jpg"])
    _img(photos / "S1_A_1_p.jpeg")
    _img(st / "S1_A_1_p.png")
    rows, problems = S.build_rows(photos, st, card, relative_to=tmp_path)
    assert len(rows) <= 1 and any("same photo" in p["reason"] for p in problems)


def test_rows_are_sorted_so_the_manifest_is_repeatable(tmp_path):
    names = ["S2_B_1_p.jpg", "S1_C_1_p.jpg", "S1_A_2_p.jpg", "S1_A_1_p.jpg"]
    photos, st, card = _session(tmp_path, names)
    rows, _ = S.build_rows(photos, st, card, relative_to=tmp_path)
    assert [(r["sticker_id"], r["light"], r["shot"]) for r in rows] == [("S1", "A", "1"), ("S1", "A", "2"), ("S1", "C", "1"), ("S2", "B", "1")]


def test_manifest_has_the_columns_reference_from_photos_reads(tmp_path):
    spec = importlib.util.spec_from_file_location("rfp", ROOT / "scripts" / "reference_from_photos.py")
    rfp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rfp)
    assert S.COLUMNS == rfp.COLUMNS
    photos, st, card = _session(tmp_path, ["S1_A_1_pixel7.jpg"])
    rows, _ = S.build_rows(photos, st, card, relative_to=tmp_path)
    out = tmp_path / "manifest.csv"
    S.write_manifest(rows, out)
    with out.open(newline="") as f:
        read = list(csv.DictReader(f))
    assert list(read[0]) == list(S.COLUMNS) and read[0]["phone"] == "pixel7"
