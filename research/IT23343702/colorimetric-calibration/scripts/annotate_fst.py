"""Manual Fitzpatrick skin type (FST) annotation tool for the ISIC subset (T12/T13).

Shows one image at a time and writes your label into the `fitzpatrick_type`
column of the metadata CSV after every key press, so you can quit any time and
resume later. The ITA auto-label is hidden until you press 'g' so it doesn't
bias your label.

Usage (from research/IT23343702/colorimetric-calibration):
    python scripts/annotate_fst.py \
        --images data/isic_subset \
        --csv data/annotations/isic_subset_metadata.csv \
        --ita data/annotations/ita_results.csv

Review mode (--review) walks only images where your label and the ITA guess differ
by --min-diff steps or more (default 2), showing both labels. A backup of the CSV is
written next to it first.

Keys:
    1-6   label I-VI and go to the next unlabelled image
    g     reveal the ITA auto-label for this image
    b     go back one image (to fix a label)
    s     skip (leave unlabelled)
    u     clear this image's label
    k     (review mode) keep the current label
    q/Esc quit (progress is already saved)
"""

from __future__ import annotations

import argparse
import csv
import os
import shutil
import time
import sys
from pathlib import Path

import cv2

ROMAN = {"1": "I", "2": "II", "3": "III", "4": "IV", "5": "V", "6": "VI"}
LABEL_COL = "fitzpatrick_type"
DISPLAY_WIDTH = 768
WINDOW = "FST annotation"


def load_rows(path: Path) -> tuple[list[str], list[dict[str, str]]]:
    with path.open(newline="") as f:
        reader = csv.DictReader(f)
        fields = list(reader.fieldnames or [])
        rows = list(reader)
    if LABEL_COL not in fields:
        fields.append(LABEL_COL)
        for r in rows:
            r[LABEL_COL] = ""
    return fields, rows


def save_rows(path: Path, fields: list[str], rows: list[dict[str, str]]) -> None:
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    os.replace(tmp, path)


def load_ita(path: Path | None) -> dict[str, str]:
    if path is None or not path.exists():
        return {}
    with path.open(newline="") as f:
        return {r["filename"]: r["fitzpatrick_type"] for r in csv.DictReader(f)}


def render(img_path: Path, header: str, footer: str):
    img = cv2.imread(str(img_path))
    if img is None:
        return None
    scale = DISPLAY_WIDTH / img.shape[1]
    img = cv2.resize(img, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    pad = 60
    canvas = cv2.copyMakeBorder(img, pad, pad, 0, 0, cv2.BORDER_CONSTANT, value=(30, 30, 30))
    cv2.putText(canvas, header, (10, 38), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
    cv2.putText(canvas, footer, (10, canvas.shape[0] - 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (180, 220, 255), 1)
    return canvas


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--images", type=Path, default=Path("data/isic_subset"))
    p.add_argument("--csv", type=Path, default=Path("data/annotations/isic_subset_metadata.csv"))
    p.add_argument("--ita", type=Path, default=Path("data/annotations/ita_results.csv"))
    p.add_argument("--review", action="store_true", help="re-check labels that disagree with ITA")
    p.add_argument("--min-diff", type=int, default=2, help="review: minimum steps of disagreement")
    args = p.parse_args()

    if not args.csv.exists() or not args.images.is_dir():
        print(f"Missing --csv ({args.csv}) or --images ({args.images}).", file=sys.stderr)
        return 1

    fields, rows = load_rows(args.csv)
    ita = load_ita(args.ita)
    total = len(rows)

    order = list(ROMAN.values())
    if args.review:
        queue = [
            i for i, r in enumerate(rows)
            if r[LABEL_COL].strip() in order and ita.get(r["filename"]) in order
            and abs(order.index(r[LABEL_COL].strip()) - order.index(ita[r["filename"]])) >= args.min_diff
        ]
        if not queue:
            print("Nothing to review.")
            return 0
        backup = args.csv.with_name(f"{args.csv.stem}.backup-{time.strftime('%Y%m%d-%H%M%S')}{args.csv.suffix}")
        shutil.copy2(args.csv, backup)
        print(f"Backup: {backup}\nReviewing {len(queue)} images.")
    else:
        queue = [i for i, r in enumerate(rows) if not r[LABEL_COL].strip()]
        if not queue:
            print("All images are already labelled.")
            return 0

    cv2.namedWindow(WINDOW, cv2.WINDOW_AUTOSIZE)
    pos = 0
    reveal = args.review
    while 0 <= pos < len(queue):
        idx = queue[pos]
        row = rows[idx]
        done = sum(1 for r in rows if r[LABEL_COL].strip())
        current = row[LABEL_COL].strip() or "-"
        header = f"{pos + 1}/{len(queue)}  {row['filename']}  labelled {done}/{total}  yours: {current}"
        footer = "1-6 label | g ITA guess | b back | s skip | u clear | q quit"
        if args.review:
            footer = "k keep | " + footer
        if reveal:
            header += f"  ITA: {ita.get(row['filename'], 'n/a')}"
        canvas = render(args.images / row["filename"], header, footer)
        if canvas is None:
            print(f"Could not read {row['filename']}, skipping.", file=sys.stderr)
            pos += 1
            continue
        cv2.imshow(WINDOW, canvas)
        key = cv2.waitKey(0) & 0xFF
        ch = chr(key) if key < 128 else ""

        if ch in ROMAN:
            row[LABEL_COL] = ROMAN[ch]
            save_rows(args.csv, fields, rows)
            pos, reveal = pos + 1, args.review
        elif ch == "k" and args.review:
            pos += 1
        elif ch == "g":
            reveal = True
        elif ch == "u":
            row[LABEL_COL] = ""
            save_rows(args.csv, fields, rows)
        elif ch == "b":
            pos, reveal = max(pos - 1, 0), args.review
        elif ch == "s":
            pos, reveal = pos + 1, args.review
        elif ch == "q" or key == 27:
            break

    cv2.destroyAllWindows()
    done = sum(1 for r in rows if r[LABEL_COL].strip())
    print(f"Saved. {done}/{total} labelled -> {args.csv}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
