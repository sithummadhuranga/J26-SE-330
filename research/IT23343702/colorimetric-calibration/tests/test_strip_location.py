"""scripts/strip_location.py: copy photos for sharing with the location removed and nothing else changed.

Phone photos carry GPS tags (the area-test photos did, although the guideline says to switch location off). The photo files are shared
between team members, so the copy that leaves the folder must not carry where it was taken. Pillow and piexif are tool dependencies,
not part of the calibration module, so these tests are skipped where they are not installed.
"""

import importlib.util
import sys
from pathlib import Path

import numpy as np
import pytest

PIL = pytest.importorskip("PIL.Image")
piexif = pytest.importorskip("piexif")
cv2 = pytest.importorskip("cv2")

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("strip_location", ROOT / "scripts" / "strip_location.py")
tool = importlib.util.module_from_spec(spec)
sys.modules["strip_location"] = tool       # a dataclass in the module needs it registered
spec.loader.exec_module(tool)


def make_photo(path, gps=True, orientation=6, time="2026:10:22 14:24:09"):
    rng = np.random.default_rng(0)
    img = PIL.fromarray(rng.integers(0, 255, (60, 80, 3), dtype=np.uint8))
    exif = {"0th": {piexif.ImageIFD.Orientation: orientation, piexif.ImageIFD.Model: b"Test Phone"},
            "Exif": {piexif.ExifIFD.DateTimeOriginal: time.encode()}, "GPS": {}, "1st": {}, "thumbnail": None}
    if gps:
        exif["GPS"] = {piexif.GPSIFD.GPSLatitudeRef: b"N", piexif.GPSIFD.GPSLatitude: ((6, 1), (55, 1), (30, 1)),
                       piexif.GPSIFD.GPSLongitudeRef: b"E", piexif.GPSIFD.GPSLongitude: ((79, 1), (50, 1), (0, 1))}
    img.save(path, "JPEG", quality=92, exif=piexif.dump(exif))


def gps_tags(path):
    return piexif.load(str(path))["GPS"]


def test_location_is_removed_and_everything_else_is_kept(tmp_path):
    src, dst = tmp_path / "a.jpg", tmp_path / "out" / "b.jpg"
    make_photo(src)
    assert gps_tags(src)
    r = tool.strip_location(src, dst)
    assert r.had_location and gps_tags(dst) == {}
    e0, e1 = piexif.load(str(src)), piexif.load(str(dst))
    assert e1["0th"][piexif.ImageIFD.Orientation] == 6 and e1["0th"][piexif.ImageIFD.Model] == b"Test Phone"
    assert e1["Exif"][piexif.ExifIFD.DateTimeOriginal] == e0["Exif"][piexif.ExifIFD.DateTimeOriginal]


def test_the_pixels_are_identical_the_image_is_not_re_encoded(tmp_path):
    src, dst = tmp_path / "a.jpg", tmp_path / "b.jpg"
    make_photo(src)
    tool.strip_location(src, dst)
    a = cv2.imread(str(src), cv2.IMREAD_IGNORE_ORIENTATION)
    b = cv2.imread(str(dst), cv2.IMREAD_IGNORE_ORIENTATION)
    assert np.array_equal(a, b)


def test_the_source_file_is_never_changed(tmp_path):
    src, dst = tmp_path / "a.jpg", tmp_path / "b.jpg"
    make_photo(src)
    before = src.read_bytes()
    tool.strip_location(src, dst)
    assert src.read_bytes() == before


def test_a_photo_without_location_is_copied_and_reported_as_such(tmp_path):
    src, dst = tmp_path / "a.jpg", tmp_path / "b.jpg"
    make_photo(src, gps=False)
    r = tool.strip_location(src, dst)
    assert not r.had_location and gps_tags(dst) == {}


def test_a_file_without_any_exif_is_copied_unchanged(tmp_path):
    src, dst = tmp_path / "a.jpg", tmp_path / "b.jpg"
    PIL.fromarray(np.zeros((20, 20, 3), np.uint8)).save(src, "JPEG")
    r = tool.strip_location(src, dst)
    assert not r.had_location and dst.read_bytes() == src.read_bytes()


def test_it_refuses_to_overwrite_the_source_or_an_existing_file(tmp_path):
    src, other = tmp_path / "a.jpg", tmp_path / "b.jpg"
    make_photo(src)
    make_photo(other)
    with pytest.raises(FileExistsError):
        tool.strip_location(src, src)
    with pytest.raises(FileExistsError):
        tool.strip_location(src, other)


def test_a_non_jpeg_is_refused_not_silently_copied_with_its_location(tmp_path):
    src = tmp_path / "a.heic"
    src.write_bytes(b"not a jpeg")
    with pytest.raises(ValueError):
        tool.strip_location(src, tmp_path / "b.heic")


def test_main_copies_a_folder_and_reports_how_many_had_location(tmp_path, capsys):
    d, out = tmp_path / "in", tmp_path / "out"
    d.mkdir()
    make_photo(d / "a.jpg")
    make_photo(d / "b.jpg", gps=False)
    (d / "notes.txt").write_text("x")
    assert tool.main(["--photos", str(d), "--out", str(out)]) == 0
    assert sorted(p.name for p in out.iterdir()) == ["a.jpg", "b.jpg"]
    assert all(gps_tags(p) == {} for p in out.iterdir())
    assert "2 photos, 1 had a location" in capsys.readouterr().out


def test_main_fails_if_a_copy_still_has_a_location(tmp_path, monkeypatch):
    d, out = tmp_path / "in", tmp_path / "out"
    d.mkdir()
    make_photo(d / "a.jpg")
    monkeypatch.setattr(tool, "_remove_gps", lambda data: data)      # a broken stripper must be noticed, not trusted
    assert tool.main(["--photos", str(d), "--out", str(out)]) == 1
