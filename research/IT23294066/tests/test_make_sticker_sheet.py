import io
import re
import unittest

import numpy as np

import common  # noqa: F401
import make_sticker_sheet as MS

PPM = MS.PX_PER_MM


class StickerSheetTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.img, cls.info = MS.build_sheet()
        cls.arr = np.array(cls.img)

    def run_len(self, row, x_from_mm, x_to_mm):
        black = np.where(row.sum(axis=1) < 30)[0]
        black = black[(black >= int(x_from_mm * PPM)) & (black <= int(x_to_mm * PPM))]
        return (black.max() + 1 - black.min()) / PPM

    def test_page_is_a4_at_600_dpi(self):
        self.assertEqual(self.img.size, (4961, 7016))
        self.assertAlmostEqual(self.img.size[0] / PPM, 210.0, delta=0.05)
        self.assertAlmostEqual(self.img.size[1] / PPM, 297.0, delta=0.05)

    def test_six_stickers_at_the_stated_places(self):
        self.assertEqual(self.info["origins"], [(15, 15), (75, 15), (135, 15), (15, 75), (75, 75), (135, 75)])
        for ox, oy in self.info["origins"]:
            # the middle of the top frame edge is black, the white inside is white
            self.assertTrue((self.arr[int((oy + 2.5) * PPM), int((ox + 25) * PPM)] == 0).all())
            self.assertTrue((self.arr[int((oy + 40) * PPM), int((ox + 25) * PPM)] == 255).all())

    def test_a_sticker_is_exactly_50_mm_wide_with_a_5_mm_frame(self):
        ox, oy = self.info["origins"][0]
        row = self.arr[int((oy + 2.5) * PPM)]
        self.assertAlmostEqual(self.run_len(row, ox - 1, ox + 51), 50.0, delta=0.06)
        mid = self.arr[int((oy + 25) * PPM)]  # a row through the patches: frame 5 mm at both ends
        left = np.where(mid[int(ox * PPM):int((ox + 20) * PPM)].sum(axis=1) < 30)[0]
        self.assertAlmostEqual((left.max() + 1) / PPM, 5.0, delta=0.06)

    def test_orientation_square_only_in_the_top_left_inner_corner(self):
        ox, oy = self.info["origins"][0]
        dark = lambda x, y: (self.arr[int((oy + y) * PPM), int((ox + x) * PPM)] == 0).all()
        self.assertTrue(dark(8, 8))             # inner top-left: orientation square
        self.assertFalse(dark(42, 8))           # the other three inner corners are white
        self.assertFalse(dark(42, 42))
        self.assertFalse(dark(8, 42))

    def test_patch_colours_are_the_stand_in_values(self):
        ox, oy = self.info["origins"][0]
        for (cx, name) in ((12.5, "grey"), (25, "skin_a"), (37.5, "skin_b")):
            px = self.arr[int((oy + 25) * PPM), int((ox + cx) * PPM)]
            self.assertEqual(tuple(int(v) for v in px), MS.COLOURS[name])

    def test_scale_bar_is_100_mm(self):
        bar = self.info["bar"]
        row = self.arr[int((bar["y"] + 0.3) * PPM)]
        self.assertAlmostEqual(self.run_len(row, bar["x"] - 1, bar["x"] + bar["length"] + 1), 100.0, delta=0.1)

    def test_shape_areas_are_counted_from_the_file_and_close_to_their_formulas(self):
        a = self.info["areas"]
        self.assertAlmostEqual(a["rectangle"], 1000.0, delta=3.0)
        self.assertAlmostEqual(a["circle"], math_pi_r2(15.0), delta=0.02 * math_pi_r2(15.0))
        self.assertAlmostEqual(a["triangle"], 600.0, delta=0.01 * 600.0)

    def test_pdf_page_is_a4(self):
        buf = io.BytesIO()
        self.img.save(buf, "PDF", resolution=float(MS.DPI))
        m = re.search(rb"/MediaBox\s*\[\s*([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s+([\d.\-]+)\s*\]", buf.getvalue())
        w, h = float(m.group(3)) - float(m.group(1)), float(m.group(4)) - float(m.group(2))
        self.assertAlmostEqual(w / 72 * 25.4, 210.0, delta=0.1)
        self.assertAlmostEqual(h / 72 * 25.4, 297.0, delta=0.1)


class FontFallbackTest(unittest.TestCase):
    """The sheet must build on a machine without the macOS fonts (the CI runs on Linux)."""

    def test_a_missing_font_file_falls_back_to_a_built_in_font_of_the_right_size(self):
        f = MS._font(3.0, paths=("/no/such/font.ttf",))
        left, top, right, bottom = f.getbbox("100 mm")
        self.assertGreater(bottom - top, 0.5 * MS.px(3.0))                    # drawn at about 3 mm, not at the tiny default size
        self.assertLess(bottom - top, 2.0 * MS.px(3.0))

    def test_both_sheets_build_without_any_system_font(self):
        old = MS.FONT_PATHS
        MS.FONT_PATHS = ()
        try:
            img1, _ = MS.build_sheet()
            img2, info2 = MS.build_sheet_v2()
        finally:
            MS.FONT_PATHS = old
        self.assertEqual(img1.size, (4961, 7016))
        self.assertEqual(img2.size, (4961, 7016))
        self.assertEqual(len(info2["label_boxes"]), 3)


class SheetV2Test(unittest.TestCase):
    """Sheet v2: the shapes sit between two rows of stickers (about 20-30 mm from each row), as a wound would sit next to a sticker,
    and there are places for a ten rupee coin."""

    @classmethod
    def setUpClass(cls):
        cls.img, cls.info = MS.build_sheet_v2()
        cls.arr = np.array(cls.img)

    def test_page_is_a4_at_600_dpi(self):
        self.assertEqual(self.img.size, (4961, 7016))

    def test_two_rows_of_three_stickers_with_a_free_band_between(self):
        self.assertEqual(self.info["origins"], [(15, 15), (75, 15), (135, 15), (15, 135), (75, 135), (135, 135)])
        for ox, oy in self.info["origins"]:
            self.assertTrue((self.arr[int((oy + 2.5) * PPM), int((ox + 25) * PPM)] == 0).all())      # frame
            self.assertTrue((self.arr[int((oy + 40) * PPM), int((ox + 25) * PPM)] == 255).all())     # white inside
            self.assertTrue((self.arr[int((oy + 8) * PPM), int((ox + 8) * PPM)] == 0).all())         # orientation square
        gap = self.arr[int(66 * PPM):int(86 * PPM)]
        self.assertFalse(((gap.sum(axis=2) < 30)).any(), "nothing black between the upper stickers and the shapes")

    def test_every_shape_is_close_to_a_sticker_row_on_both_sides(self):
        for name, (x0, y0, x1, y1) in self.info["shape_boxes"].items():
            self.assertGreaterEqual(y0 - 65, 15, name)
            self.assertLessEqual(y0 - 65, 30, name)              # gap to the bottom edge of the upper stickers
            self.assertLessEqual(135 - y1, 30, name)             # gap to the top edge of the lower stickers

    def test_shape_areas_are_counted_from_the_file(self):
        a = self.info["areas"]
        self.assertAlmostEqual(a["rectangle"], 1000.0, delta=3.0)
        self.assertAlmostEqual(a["circle"], math_pi_r2(15.0), delta=0.02 * math_pi_r2(15.0))
        self.assertAlmostEqual(a["triangle"], 600.0, delta=0.01 * 600.0)
        red = (self.arr[:, :, 0] > 150) & (self.arr[:, :, 1] < 80) & (self.arr[:, :, 2] < 80)
        self.assertAlmostEqual(red.sum() / PPM ** 2, sum(a.values()), delta=2.0)     # nothing else on the page is red

    def test_shapes_do_not_touch_each_other_or_the_stickers(self):
        boxes = list(self.info["shape_boxes"].values())
        for i, (a, b) in enumerate(((boxes[i], boxes[j]) for i in range(3) for j in range(i + 1, 3))):
            self.assertTrue(a[2] + 5 <= b[0] or b[2] + 5 <= a[0], "shapes at least 5 mm apart in x")

    def test_coin_rings_are_thin_grey_circles_a_little_wider_than_the_coin(self):
        rings = self.info["coin_rings"]
        self.assertGreaterEqual(len(rings), 3)
        for cx, cy, d in rings:
            self.assertAlmostEqual(d, MS.COIN_RING_MM, delta=1e-6)
            self.assertGreater(d, MS.COIN_DIAMETER_MM + 0.8)                                      # room to place the coin
            r = d / 2
            on = self.arr[int(round(cy * PPM)), int(round((cx + r + 0.15) * PPM))]                # the middle of the 0.3 mm line
            self.assertTrue((on > 120).all() and (on < 215).all(), "ring is mid grey, not black: the finder must not see it")
            self.assertTrue((self.arr[int(cy * PPM), int(cx * PPM)] == 255).all(), "inside the ring is empty paper")
            self.assertTrue((self.arr[int(cy * PPM), int((cx + r + 2) * PPM)] == 255).all(), "outside the ring is empty paper")
            self.assertTrue((self.arr[int(cy * PPM), int((cx + r - 0.3) * PPM)] == 255).all(), "the room inside the line is empty paper")
            row = self.arr[int(round(cy * PPM)), :, 0]
            xs = np.where(row[int((cx - r - 1) * PPM):int((cx + r + 1) * PPM)] < 215)[0]
            self.assertAlmostEqual((xs.max() - xs.min()) / PPM, d + 0.6, delta=0.3)               # outer width of the ring line

    def test_one_coin_ring_is_near_the_stickers_and_two_are_far_from_them(self):
        def edge_gap(cx, cy, ox, oy):
            return max(ox - cx, cx - (ox + 50), oy - cy, cy - (oy + 50), 0)        # distance from the ring centre to the sticker square (rows are far apart: good enough)
        d = [min(edge_gap(cx, cy, ox, oy) for ox, oy in self.info["origins"]) for cx, cy, _ in self.info["coin_rings"]]
        self.assertLess(min(d), 45)
        self.assertGreater(sorted(d)[-2], 50)

    def test_scale_bar_is_100_mm(self):
        bar = self.info["bar"]
        row = self.arr[int((bar["y"] + 0.3) * PPM)]
        black = np.where(row.sum(axis=1) < 30)[0]
        black = black[(black >= int((bar["x"] - 1) * PPM)) & (black <= int((bar["x"] + 101) * PPM))]
        self.assertAlmostEqual((black.max() + 1 - black.min()) / PPM, 100.0, delta=0.1)

    def test_the_old_sheet_is_unchanged(self):
        _, info = MS.build_sheet()
        self.assertEqual(info["origins"], [(15, 15), (75, 15), (135, 15), (15, 75), (75, 75), (135, 75)])

    def test_the_texts_under_the_shapes_do_not_overlap_each_other_or_the_stickers(self):
        boxes = self.info["label_boxes"]                       # (x0, y0, x1, y1) in mm
        self.assertEqual(len(boxes), 3)
        for i in range(3):
            for j in range(i + 1, 3):
                a, b = boxes[i], boxes[j]
                self.assertTrue(a[2] <= b[0] or b[2] <= a[0] or a[3] <= b[1] or b[3] <= a[1], f"labels {i} and {j} overlap")
            self.assertLess(boxes[i][3], 135 - 2, "label stays clear of the lower stickers")

    def test_command_line_writes_the_v2_files(self):
        import sys, tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as d:
            old = sys.argv
            sys.argv = ["make_sticker_sheet.py", "--out-dir", d, "--layout", "2"]
            try:
                MS.main()
            finally:
                sys.argv = old
            self.assertTrue((Path(d) / "sticker_sheet_v2_a4.pdf").exists())
            self.assertTrue((Path(d) / "sticker_sheet_v2_a4.png").exists())


def math_pi_r2(r):
    import math
    return math.pi * r * r


if __name__ == "__main__":
    unittest.main()
