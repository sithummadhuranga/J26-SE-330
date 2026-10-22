"""Reference-session photo names -> the manifest read by scripts/reference_from_photos.py.

Photos are named S<sticker number>_<A|B|C>_<shot>_<phone>.jpg (A daylight, B warm lamp, C cool lamp; the phone is everything
after the third underscore). The straightened sticker and the grey-card patch of a photo are looked up by the photo's file
stem as <stem>.png in a sticker folder and in a card folder. Photos that cannot be used are returned as problems with a
reason; nothing is dropped silently.
"""

from __future__ import annotations

import csv
import os
import re
from dataclasses import dataclass
from pathlib import Path

COLUMNS = ("phone", "sticker_id", "light", "shot", "sticker_crop", "card_crop")
PHOTO_EXTENSIONS = (".jpg", ".jpeg", ".png")
_NAME = re.compile(r"^(S\d+)_([ABC])_([^_]+)_(.+)\.(?i:jpg|jpeg|png)$")  # only the extension is case-insensitive


@dataclass(frozen=True)
class PhotoName:
    sticker_id: str
    light: str
    shot: str
    phone: str


def parse_photo_name(name: str) -> PhotoName:
    """Split a photo file name; raises ValueError if it does not follow the agreed pattern."""
    m = _NAME.match(name)
    if m is None or "/" in name:
        raise ValueError(f"{name!r} does not follow the pattern S<number>_<A|B|C>_<shot>_<phone>.jpg")
    return PhotoName(m.group(1), m.group(2), m.group(3), m.group(4))


def build_rows(photos: Path, sticker_dir: Path, card_dir: Path, relative_to: Path) -> tuple[list[dict], list[dict]]:
    """(rows, problems) for every photo file in `photos`. Crop paths in the rows are relative to `relative_to`
    (the folder that will hold the manifest)."""
    rows, problems, seen = [], [], {}
    files = sorted(p for p in Path(photos).iterdir() if p.is_file() and p.suffix.lower() in PHOTO_EXTENSIONS)
    for f in files:
        try:
            n = parse_photo_name(f.name)
        except ValueError as e:
            problems.append({"file": f.name, "reason": str(e)})
            continue
        key = (n.phone, n.sticker_id, n.light, n.shot)
        if key in seen:
            problems.append({"file": f.name, "reason": f"same photo as {seen[key]} (same phone, sticker, light and shot)"})
            continue
        seen[key] = f.name
        sticker, card = Path(sticker_dir) / f"{f.stem}.png", Path(card_dir) / f"{f.stem}.png"
        missing = [what for what, p in (("sticker crop", sticker), ("card crop", card)) if not p.is_file()]
        if missing:
            problems.append({"file": f.name, "reason": "missing " + " and ".join(missing)})
            continue
        rel = lambda p: os.path.relpath(p.resolve(), Path(relative_to).resolve())  # noqa: E731
        rows.append({"phone": n.phone, "sticker_id": n.sticker_id, "light": n.light, "shot": n.shot,
                     "sticker_crop": rel(sticker), "card_crop": rel(card)})
    rows.sort(key=lambda r: (r["phone"], r["sticker_id"], r["light"], r["shot"]))
    return rows, problems


def write_manifest(rows: list[dict], path: Path) -> None:
    with Path(path).open("w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=COLUMNS)
        w.writeheader()
        w.writerows(rows)
