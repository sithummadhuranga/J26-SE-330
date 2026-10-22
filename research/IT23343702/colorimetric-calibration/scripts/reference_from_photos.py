"""Turn reference-session crops into marker reference files (one per phone) and a report.

Input: a CSV manifest with the columns  phone, sticker_id, light, shot, sticker_crop, card_crop  (crop paths are relative
to the CSV). A sticker crop is the straightened sticker from the sticker finder; a card crop is a flat piece of the 18% grey
card cut from the same photo. See colorimetric/reference.py for the method and its assumptions.

Writes <out>/marker_reference_<phone>.json for each phone, and <out>/reference_report.json (spread, dropped rows, the
grey decision of reply 7). It never touches src/colorimetric/sticker/marker_reference.json: copy a file there by hand, and
only after reading the report.

    python scripts/reference_from_photos.py --manifest session/manifest.csv --out session/out
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.marker import load_layout  # noqa: E402
from colorimetric.reference import ReferenceRow, build_reference, patch_labs_from_card  # noqa: E402

COLUMNS = ("phone", "sticker_id", "light", "shot", "sticker_crop", "card_crop")


def read_rgb(path: Path):
    bgr = cv2.imread(str(path))
    if bgr is None:
        raise ValueError(f"cannot read {path}")
    return cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manifest", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--layout", type=Path, default=None, help="marker_layout.json (default: the packaged copy)")
    args = ap.parse_args(argv)

    layout = load_layout(args.layout)
    rows, dropped = [], []
    with args.manifest.open(newline="") as f:
        reader = csv.DictReader(f)
        missing = [c for c in COLUMNS if c not in (reader.fieldnames or [])]
        if missing:
            print(f"manifest is missing columns: {missing}", file=sys.stderr)
            return 1
        for r in reader:
            try:
                labs = patch_labs_from_card(read_rgb(args.manifest.parent / r["sticker_crop"]),
                                            read_rgb(args.manifest.parent / r["card_crop"]), layout)
                rows.append(ReferenceRow(r["phone"], r["sticker_id"], r["light"], r["shot"], labs))
            except ValueError as e:
                dropped.append({k: r[k] for k in COLUMNS[:4]} | {"reason": str(e)})
    if not rows:
        print("no usable rows", file=sys.stderr)
        return 1

    phones = build_reference(rows)
    args.out.mkdir(parents=True, exist_ok=True)
    report = {"rows_used": len(rows), "rows_dropped": dropped, "phones": {}}
    for name, ref in phones.items():
        (args.out / f"marker_reference_{name}.json").write_text(json.dumps(ref.reference_json(), indent=1) + "\n")
        report["phones"][name] = ref.reference_json()
        print(f"{name}: {ref.n_rows} photos, grey L* {ref.lab['grey'][0]:.2f} (spread {ref.spread['grey']['L_std']:.2f}), "
              f"keep printed grey: {ref.keep_printed_grey}  [{ref.reason}]")
    (args.out / "reference_report.json").write_text(json.dumps(report, indent=1) + "\n")
    if dropped:
        print(f"{len(dropped)} row(s) dropped, see reference_report.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
