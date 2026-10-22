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


COIN_DIAMETER_MM = 26.4   # Sri Lankan ten rupee coin as published (to be confirmed with a caliper)
COIN_RING_MM = 27.4       # inside diameter of the guide ring: 0.5 mm of room all round the coin
RING_LINE_MM = 0.3
STICKER_ORIGINS_V2 = [(15 + 60 * c, 15 + 120 * r) for r in range(2) for c in range(3)]
COIN_RINGS_V2 = [(173.0, 102.5), (40.0, 250.0), (100.0, 250.0)]
RING_GREY = (160, 160, 160)


def build_sheet_v2():
    """Sheet v2: two rows of three stickers, the three shapes in the band between them (about 20-25 mm from the row above and 17-20 mm
    from the row below, like a wound beside a sticker), the 100 mm bar, and three thin grey rings that show where to put a coin.
    Returns (PIL image, info): origins, bar, counted areas (mm2), shape boxes (mm) and coin rings (cx, cy, ring diameter)."""
    import numpy as np
    layout = json.loads(LAYOUT.read_text())
    img = Image.new("RGB", (px(A4_MM[0]), px(A4_MM[1])), (255, 255, 255))
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", px(3.0))
    for ox, oy in STICKER_ORIGINS_V2:
        sticker(d, ox, oy, layout)

    boxes = {"rectangle": (15.0, 90.0, 55.0, 115.0), "circle": (67.0, 87.5, 97.0, 117.5), "triangle": (105.0, 87.5, 145.0, 117.5)}
    x0, y0, x1, y1 = boxes["rectangle"]
    box(d, x0, y0, x1, y1, SHAPE_COLOUR)
    x0, y0, x1, y1 = boxes["circle"]
    d.ellipse([px(x0), px(y0), px(x1) - 1, px(y1) - 1], fill=SHAPE_COLOUR)
    x0, y0, x1, y1 = boxes["triangle"]
    d.polygon([(px(x0), px(y0)), (px(x1), px(y0)), (px(x0), px(y1))], fill=SHAPE_COLOUR)
    arr = np.array(img)
    red = (arr[:, :, 0] > 150) & (arr[:, :, 1] < 80) & (arr[:, :, 2] < 80)
    areas = {}
    for name, (bx0, by0, bx1, by1) in boxes.items():
        areas[name] = float(red[px(by0 - 2):px(by1 + 2), px(bx0 - 2):px(bx1 + 2)].sum()) / PX_PER_MM ** 2
    labels = [(15.0, 119.5, "rectangle 40 x 25 mm: %.1f mm2" % areas["rectangle"]),
              (62.0, 124.5, "circle d 30 mm: %.1f mm2" % areas["circle"]),
              (105.0, 119.5, "triangle 40 x 30 mm: %.1f mm2" % areas["triangle"])]
    label_boxes = []
    for lx, ly, text in labels:
        d.text((px(lx), px(ly)), text, fill=(0, 0, 0), font=font)
        bx0, by0, bx1, by1 = d.textbbox((px(lx), px(ly)), text, font=font)
        label_boxes.append((bx0 / PX_PER_MM, by0 / PX_PER_MM, bx1 / PX_PER_MM, by1 / PX_PER_MM))

    # 100 mm bar, same drawing as the first sheet
    bar = {"x": 15.0, "y": 205.0, "length": 100.0}
    bx, by = bar["x"], bar["y"]
    box(d, bx, by, bx + bar["length"], by + 0.6, (0, 0, 0))
    for i in range(101):
        length = 6 if i % 10 == 0 else 4 if i % 5 == 0 else 2
        box(d, bx + i - 0.15, by - length, bx + i + 0.15, by, (0, 0, 0))
        if i % 10 == 0:
            d.text((px(bx + i - 2), px(by + 2)), str(i), fill=(0, 0, 0), font=font)
    d.text((px(bx + 103), px(by - 2)), "mm", fill=(0, 0, 0), font=font)
    d.text((px(bx), px(by - 12)), "This bar must measure exactly 100 mm with a ruler.", fill=(0, 0, 0), font=ImageFont.truetype("/System/Library/Fonts/Helvetica.ttc", px(4.0)))

    # coin guide rings: thin mid-grey (not dark, so a sticker finder does not take them for a frame)
    r = COIN_RING_MM / 2 + RING_LINE_MM                                       # outer radius: the line is drawn inwards from it
    for cx, cy in COIN_RINGS_V2:
        d.ellipse([px(cx - r), px(cy - r), px(cx + r) - 1, px(cy + r) - 1], outline=RING_GREY, width=px(RING_LINE_MM))
        d.text((px(cx - 10), px(cy + r + 2)), "coin here", fill=(0, 0, 0), font=font)

    notes = ["PRINT AT 100% (actual size, NOT 'fit to page'). Matte paper or matte sticker paper, no gloss. Lay the sheet flat on a stiff board.",
             "Photograph the whole sheet. Shapes lie between the rows of stickers. Put a ten rupee coin inside a grey ring when asked.",
             "Sticker: 50 mm square. Measure its real side with a ruler and write it down."]
    for i, t in enumerate(notes):
        d.text((px(15), px(272 + 6 * i)), t, fill=(0, 0, 0), font=font)
    return img, {"origins": list(STICKER_ORIGINS_V2), "bar": bar, "areas": areas, "shape_boxes": boxes,
                 "coin_rings": [(cx, cy, COIN_RING_MM) for cx, cy in COIN_RINGS_V2], "label_boxes": label_boxes}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--layout", type=int, choices=(1, 2), default=1, help="1: the first sheet (default), 2: shapes between two rows of stickers + coin rings")
    args = ap.parse_args()
    if args.layout == 2:
        img, info = build_sheet_v2()
        name = "sticker_sheet_v2_a4"
    else:
        img, info = build_sheet()
        name = "sticker_sheet_a4"
    print("shape areas counted from the file:", {k: round(v, 1) for k, v in info["areas"].items()}, "mm2")
    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    img.save(out / (name + ".png"), dpi=(DPI, DPI))
    img.save(out / (name + ".pdf"), resolution=float(DPI))
    print("wrote", out / (name + ".pdf"), img.size, "pixels =", round(img.size[0] / PX_PER_MM, 2), "x", round(img.size[1] / PX_PER_MM, 2), "mm")


if __name__ == "__main__":
    main()
