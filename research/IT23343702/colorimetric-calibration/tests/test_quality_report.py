"""scripts/quality_report.py: the gate applied to a photo (CV's finder -> marker check -> verdict)."""

import importlib.util
from pathlib import Path

import cv2
import numpy as np
import pytest

ROOT = Path(__file__).resolve().parents[1]
FINDER = ROOT.parents[1] / "IT23294066" / "area-measurement" / "sticker_finder.py"
pytestmark = pytest.mark.skipif(not FINDER.exists(), reason="CV's sticker finder is not in this checkout")


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


@pytest.fixture(scope="module")
def report():
    return _load(ROOT / "scripts" / "quality_report.py", "quality_report")


@pytest.fixture(scope="module")
def make():
    import sys
    sys.path.insert(0, str(ROOT / "scripts"))
    import make_golden_vectors as G
    return G


def photo(sticker, mirror=False, x0=420, y0=260, size=360, bg=(175, 150, 130), washout=0.0):
    s = sticker[:, ::-1] if mirror else sticker
    H, W = 900, 1200
    img = np.full((H, W, 3), bg, np.uint8)
    src = np.float32([[0, 0], [499, 0], [499, 499], [0, 499]])
    dst = np.float32([[x0, y0], [x0 + size, y0], [x0 + size, y0 + size], [x0, y0 + size]])
    cv2.warpPerspective(np.ascontiguousarray(s), cv2.getPerspectiveTransform(src, dst), (W, H), flags=cv2.INTER_LINEAR,
                        borderMode=cv2.BORDER_TRANSPARENT, dst=img)
    if washout:  # glare: a veil of light over the sticker
        veil = np.zeros((H, W), np.float32)
        cv2.rectangle(veil, (x0 - 20, y0 - 20), (x0 + size + 20, y0 + size + 20), washout, -1)
        veil = cv2.GaussianBlur(veil, (0, 0), 25)[..., None]
        img = (img * (1 - veil) + 255 * veil).astype(np.uint8)
    return cv2.GaussianBlur(img, (0, 0), 1.0)


def test_a_clean_sticker_photo_is_ok(report, make):
    r = report.assess_photo(photo(make.sticker(seed=1)))
    assert r["level"] == "ok" and r["finder_code"] is None and r["marker_code"] == "" and r["n_stickers"] == 1


def test_a_mirrored_sticker_must_be_retaken_with_the_rear_camera(report, make):
    r = report.assess_photo(photo(make.sticker(seed=1), mirror=True))
    assert (r["level"], r["code"], r["finder_code"]) == ("retake", "mirrored", "mirrored")


def test_a_photo_without_a_sticker_is_retake_no_candidate(report):
    r = report.assess_photo(np.full((900, 1200, 3), (175, 150, 130), np.uint8))
    assert (r["level"], r["code"]) == ("retake", "no_candidate") and r["n_stickers"] == 0 and r["marker_code"] is None


def test_a_sticker_cut_by_the_photo_edge_is_retake_cut_by_edge(report, make):
    r = report.assess_photo(photo(make.sticker(seed=1), x0=-150))
    assert (r["level"], r["code"]) == ("retake", "cut_by_edge")


def _glare(img, cx, cy, r, a):
    veil = np.zeros(img.shape[:2], np.float32)
    cv2.circle(veil, (cx, cy), r, a, -1)
    veil = cv2.GaussianBlur(veil, (0, 0), r / 3)[..., None]
    return (img * (1 - veil) + 255 * veil).astype(np.uint8)


@pytest.mark.parametrize("how", ["veil 0.3", "veil 0.6", "veil 0.85", "patch glare small", "patch glare large", "glare on a corner"])
def test_glare_is_never_passed_whatever_code_the_finder_gives(report, make, how):
    """Safety property: a sticker under glare must be retaken. Which code the finder names depends on how the glare falls
    (the frame vanishes -> no_candidate; the patches wash out -> layout_mismatch; a faint frame -> weak_frame_contrast), so the
    code is not asserted here; the finder's own tests and the golden vectors cover the codes."""
    st = make.sticker(seed=1)
    img = {"veil 0.3": lambda: photo(st, washout=0.3), "veil 0.6": lambda: photo(st, washout=0.6), "veil 0.85": lambda: photo(st, washout=0.85),
           "patch glare small": lambda: _glare(photo(st), 600, 440, 90, 0.9), "patch glare large": lambda: _glare(photo(st), 600, 440, 140, 0.9),
           "glare on a corner": lambda: _glare(photo(st), 520, 330, 120, 1.0)}[how]()
    r = report.assess_photo(img)
    assert r["level"] == "retake" and r["n_stickers"] == 0


def test_a_mild_veil_that_keeps_the_frame_visible_is_still_read(report, make):
    r = report.assess_photo(photo(make.sticker(seed=1), washout=0.2))
    assert r["n_stickers"] == 1 and r["level"] in ("ok", "warn")


@pytest.mark.parametrize("gain,code", [((0.15, 0.15, 0.15), "grey_gain_out_of_range"), ((1.8, 1.0, 0.5), "grey_not_neutral")])
def test_a_found_sticker_in_extreme_light_is_a_warning_about_colour_only(report, make, gain, code):
    r = report.assess_photo(photo(make.sticker(gain=gain, seed=2)))
    assert (r["level"], r["code"], r["finder_code"]) == ("warn", code, None)


def test_with_several_stickers_the_photo_is_judged_by_the_best_one(report, make):
    good, bad = make.sticker(seed=1), make.sticker(gain=(0.15, 0.15, 0.15), seed=2)
    img = photo(bad, x0=60, y0=300, size=300)
    cv2.warpPerspective(np.ascontiguousarray(good), cv2.getPerspectiveTransform(
        np.float32([[0, 0], [499, 0], [499, 499], [0, 499]]), np.float32([[700, 300], [1000, 300], [1000, 600], [700, 600]])),
        (1200, 900), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_TRANSPARENT, dst=img)
    r = report.assess_photo(img)
    assert r["n_stickers"] == 2 and r["level"] == "ok"


def test_skin_pixels_are_only_judged_when_given(report, make):
    img = photo(make.sticker(seed=1))
    assert report.assess_photo(img)["level"] == "ok"
    assert report.assess_photo(img, skin_pixels=10)["code"] == "too_few_skin_pixels"


def test_the_row_has_the_columns_the_table_promises(report, make):
    r = report.assess_photo(photo(make.sticker(seed=1)))
    for k in ("finder_code", "n_stickers", "best_frame_contrast", "best_orientation_contrast", "marker_code", "level", "code", "message", "seconds"):
        assert k in r
    assert 0.0 <= r["best_frame_contrast"] <= 1.0 and 0.0 <= r["best_orientation_contrast"] <= 1.0


def test_main_writes_a_csv_with_one_row_per_photo(report, make, tmp_path):
    d = tmp_path / "photos"
    d.mkdir()
    cv2.imwrite(str(d / "a.jpg"), cv2.cvtColor(photo(make.sticker(seed=1)), cv2.COLOR_RGB2BGR))
    cv2.imwrite(str(d / "b.jpg"), np.full((300, 400, 3), 120, np.uint8))
    (d / "notes.txt").write_text("not a photo")
    out = tmp_path / "t.csv"
    assert report.main(["--photos", str(d), "--out", str(out)]) == 0
    import csv
    rows = list(csv.DictReader(out.open()))
    assert sorted(r["file"] for r in rows) == ["a.jpg", "b.jpg"]
    assert {r["file"]: r["level"] for r in rows} == {"a.jpg": "ok", "b.jpg": "retake"}
