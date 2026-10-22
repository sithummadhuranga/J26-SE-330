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


def math_pi_r2(r):
    import math
    return math.pi * r * r


if __name__ == "__main__":
    unittest.main()
