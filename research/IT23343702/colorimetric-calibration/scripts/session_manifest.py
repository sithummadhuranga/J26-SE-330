"""Build the manifest for reference_from_photos.py from reference-session photo names.

    python scripts/session_manifest.py --photos session/photos --sticker session/sticker --card session/card --out session/manifest.csv

Photos: S<sticker number>_<A|B|C>_<shot>_<phone>.jpg. Crops: <stem>.png in the sticker and card folders.
Prints every photo that was left out and why; exit code 1 if no row could be made.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from colorimetric.session import build_rows, write_manifest  # noqa: E402


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--photos", type=Path, required=True)
    ap.add_argument("--sticker", type=Path, required=True)
    ap.add_argument("--card", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True, help="manifest.csv to write")
    args = ap.parse_args(argv)
    rows, problems = build_rows(args.photos, args.sticker, args.card, relative_to=args.out.parent)
    for p in problems:
        print(f"left out {p['file']}: {p['reason']}", file=sys.stderr)
    if not rows:
        print("no usable photos", file=sys.stderr)
        return 1
    args.out.parent.mkdir(parents=True, exist_ok=True)
    write_manifest(rows, args.out)
    print(f"{len(rows)} rows -> {args.out}" + (f" ({len(problems)} left out)" if problems else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
