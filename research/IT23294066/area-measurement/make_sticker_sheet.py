"""Makes the print sheet for the reference sticker: A4, drawn from marker_layout.json at exactly true size.

The sheet holds
  * six stickers (50 mm, black frame, orientation square, grey patch, two skin patches, white area),
  * a 100 mm scale bar with millimetre ticks, to check that the printer did not shrink or stretch the page,
  * three shapes of known area (rectangle, circle, right triangle) for the area test with the sticker beside them.

The patch colours are the stand-in sRGB values from the colour component (grey 119,119,119; skin_a 118,81,68; skin_b 75,55,48).
A printer will not reproduce them exactly. That is expected: the reference session measures what the printed patches really look
like. The area measurement uses only the black frame and its real size, not the colours.

    python make_sticker_sheet.py --out-dir <folder>      (writes sticker_sheet_a4.pdf and sticker_sheet_a4.png, 600 dpi)
"""
import argparse
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

DPI = 600
PX_PER_MM = DPI / 25.4
A4_MM = (210.0, 297.0)
COLOURS = {"grey": (119, 119, 119), "skin_a": (118, 81, 68), "skin_b": (75, 55, 48)}
SHAPE_COLOUR = (200, 40, 40)
LAYOUT = Path(__file__).with_name("marker_layout.json")


def px(mm):
    return int(round(mm * PX_PER_MM))


def box(d, x0, y0, x1, y1, fill):
    d.rectangle([px(x0), px(y0), px(x1) - 1, px(y1) - 1], fill=fill)


def sticker(d, ox, oy, layout):
    side, frame = float(layout["side_mm"]), float(layout["frame_mm"])
    box(d, ox, oy, ox + side, oy + side, (0, 0, 0))                          # black frame
    box(d, ox + frame, oy + frame, ox + side - frame, oy + side - frame, (255, 255, 255))  # white inside
    sq = layout["orientation_square"]
    box(d, ox + sq["x"][0], oy + sq["y"][0], ox + sq["x"][1], oy + sq["y"][1], (0, 0, 0))   # orientation square
    for p in layout["patches"]:
        cx, cy, s = p["centre"][0], p["centre"][1], p["size"]
        box(d, ox + cx - s / 2, oy + cy - s / 2, ox + cx + s / 2, oy + cy + s / 2, COLOURS[p["name"]])


STICKER_ORIGINS = [(15 + 60 * c, 15 + 60 * r) for r in range(2) for c in range(3)]
BAR = {"x": 15.0, "y": 150.0, "length": 100.0}


def build_sheet():
    """Draws the sheet. Returns (PIL image, info) with the sticker origins (mm), the scale bar and the counted shape areas (mm2)."""
    layout = json.loads(LAYOUT.read_text())
    img = Image.new("RGB", (px(A4_MM[0]), px(A4_MM[1])), (255, 255, 255))
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", px(3.0))
    big = ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", px(4.0))

    # six stickers, 3 columns x 2 rows
    for ox, oy in STICKER_ORIGINS:
        sticker(d, ox, oy, layout)

    # 100 mm scale bar with ticks every 1 mm (2 mm long), 5 mm (4 mm) and 10 mm (6 mm, numbered)
    x0, y0 = BAR["x"], BAR["y"]
    box(d, x0, y0, x0 + BAR["length"], y0 + 0.6, (0, 0, 0))
    for i in range(101):
        length = 6 if i % 10 == 0 else 4 if i % 5 == 0 else 2
        box(d, x0 + i - 0.15, y0 - length, x0 + i + 0.15, y0, (0, 0, 0))
        if i % 10 == 0:
            d.text((px(x0 + i - 2), px(y0 + 2)), str(i), fill=(0, 0, 0), font=font)
    d.text((px(x0 + 103), px(y0 - 2)), "mm", fill=(0, 0, 0), font=font)
    d.text((px(x0), px(y0 - 14)), "This bar must measure exactly 100 mm with a ruler.", fill=(0, 0, 0), font=big)

    # three shapes of known area. The area written under each shape is counted from the pixels of this file (the shapes are
    # drawn with whole pixels, so a circle comes out about 1% under its formula), so the label is the area that is really printed.
    import numpy as np
    sy = 175.0
    box(d, 15, sy, 55, sy + 25, SHAPE_COLOUR)  # rectangle 40 x 25 mm
    r, cx, cy = 15.0, 90.0, sy + 15            # circle, diameter 30 mm
    d.ellipse([px(cx - r), px(cy - r), px(cx + r) - 1, px(cy + r) - 1], fill=SHAPE_COLOUR)
    d.polygon([(px(120), px(sy)), (px(160), px(sy)), (px(120), px(sy + 30))], fill=SHAPE_COLOUR)  # right triangle 40 x 30 mm
    arr = np.array(img)
    red = (arr[:, :, 0] > 150) & (arr[:, :, 1] < 80) & (arr[:, :, 2] < 80)

    def counted(x0, x1, y0, y1):
        return float(red[px(y0):px(y1), px(x0):px(x1)].sum()) / PX_PER_MM ** 2

    a_rect, a_circ, a_tri = counted(10, 60, sy - 5, sy + 30), counted(70, 110, sy - 5, sy + 35), counted(115, 165, sy - 5, sy + 35)
    d.text((px(15), px(sy + 28)), "rectangle 40 x 25 mm: %.1f mm2" % a_rect, fill=(0, 0, 0), font=font)
    d.text((px(70), px(sy + 33)), "circle d 30 mm: %.1f mm2" % a_circ, fill=(0, 0, 0), font=font)
    d.text((px(120), px(sy + 33)), "triangle 40 x 30 mm: %.1f mm2" % a_tri, fill=(0, 0, 0), font=font)
    d.text((px(15), px(sy + 40)), "Areas counted from this file at 100% print size (about 1% tolerance from the printer).", fill=(0, 0, 0), font=font)

    # instructions
    notes = ["PRINT AT 100% (actual size, NOT 'fit to page'). Use matte paper or matte sticker paper, no gloss.",
             "Check the 100 mm bar with a ruler before cutting. Cut along the outer edge of each black frame.",
             "Sticker: 50 mm square. Measure its real side with a ruler and write it down."]
    for i, t in enumerate(notes):
        d.text((px(15), px(262 + 6 * i)), t, fill=(0, 0, 0), font=font)

    return img, {"origins": list(STICKER_ORIGINS), "bar": dict(BAR), "areas": {"rectangle": a_rect, "circle": a_circ, "triangle": a_tri}}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", required=True)
    args = ap.parse_args()
    img, info = build_sheet()
    print("shape areas counted from the file:", {k: round(v, 1) for k, v in info["areas"].items()}, "mm2")
    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    img.save(out / "sticker_sheet_a4.png", dpi=(DPI, DPI))
    img.save(out / "sticker_sheet_a4.pdf", resolution=float(DPI))
    print("wrote", out / "sticker_sheet_a4.pdf", img.size, "pixels =", round(img.size[0] / PX_PER_MM, 2), "x", round(img.size[1] / PX_PER_MM, 2), "mm")


if __name__ == "__main__":
    main()
