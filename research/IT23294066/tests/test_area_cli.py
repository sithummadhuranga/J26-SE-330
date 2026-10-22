import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

import cv2
import numpy as np
from PIL import Image

import common  # noqa: F401
import area_real_photo as AR
import area_simulation as AS
import make_sticker_sheet as MSS
import sticker_finder as SF
from test_sticker_finder import photo


def run(module, argv):
    out = io.StringIO()
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(out):
        module.main()
    return out.getvalue()


class SheetCliTest(unittest.TestCase):
    def test_writes_the_png_and_the_pdf_at_a4_size(self):
        d = tempfile.mkdtemp()
        text = run(MSS, ["make_sticker_sheet.py", "--out-dir", d])
        self.assertEqual(sorted(os.listdir(d)), ["sticker_sheet_a4.pdf", "sticker_sheet_a4.png"])
        self.assertEqual(Image.open(os.path.join(d, "sticker_sheet_a4.png")).size, (4961, 7016))
        self.assertIn("shape areas counted from the file", text)
        self.assertIn("rectangle", text)


class PhotoCliTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dir = tempfile.mkdtemp()
        img, _ = photo(tilt=15, rot=20)
        cls.path = os.path.join(cls.dir, "sheet.jpg")
        cv2.imwrite(cls.path, cv2.cvtColor(img, cv2.COLOR_RGB2BGR))

    def test_sticker_finder_lists_the_stickers_and_writes_a_debug_picture(self):
        dbg = os.path.join(self.dir, "dbg.png")
        text = run(SF, ["sticker_finder.py", self.path, "--debug", dbg])
        self.assertIn("6 sticker(s) found", text)
        self.assertEqual(text.count("px/mm at the centre"), 6)
        self.assertTrue(os.path.exists(dbg))

    def test_real_photo_tool_prints_each_shape_and_a_summary_and_a_debug_picture(self):
        text = run(AR, ["area_real_photo.py", self.path, "--debug-dir", os.path.join(self.dir, "d")])
        for kind in ("rectangle", "circle", "triangle"):
            self.assertIn(kind, text)
        self.assertIn("All shapes: |error| median", text)
        self.assertIn("3 measurements", text)
        self.assertEqual(text.count("| edge coverage"), 3)                   # one per shape: the reported figure
        self.assertIn("with edge coverage", text)
        self.assertTrue(os.path.exists(os.path.join(self.dir, "d", "sheet_debug.jpg")))

    def test_an_unreadable_file_is_reported_not_a_crash(self):
        text = run(AR, ["area_real_photo.py", os.path.join(self.dir, "missing.jpg")])
        self.assertIn("cannot read", text)

    def test_a_photo_without_a_sticker_is_reported_as_zero_stickers(self):
        blank = os.path.join(self.dir, "blank.jpg")
        cv2.imwrite(blank, np.full((600, 800, 3), 150, np.uint8))
        text = run(AR, ["area_real_photo.py", blank])
        self.assertIn("0 sticker(s) found", text)
        self.assertNotIn("All shapes", text)


class SimulationCliTest(unittest.TestCase):
    def test_a_small_run_writes_the_truth_and_one_row_per_trial(self):
        d = tempfile.mkdtemp()
        sheet = os.path.join(d, "sheet.png")
        big, _ = MSS.build_sheet()
        big.save(sheet)
        out = os.path.join(d, "r.json")
        text = run(AS, ["area_simulation.py", "--sheet", sheet, "--out", out, "--trials", "1", "--tilts", "0,25"])
        self.assertIn("true areas counted from the sheet", text)
        self.assertEqual(text.count("sticker found in 1/1 photos"), 2)
        with open(out) as f:
            data = json.load(f)
        self.assertEqual(sorted(data["truth"]), ["circle", "rectangle", "triangle"])
        self.assertEqual([r["tilt"] for r in data["rows"]], [0.0, 25.0])
        for r in data["rows"]:
            self.assertEqual(r["found"], 6)
            self.assertLess(abs(r["rectangle"]["hom"]), 3.0)


if __name__ == "__main__":
    unittest.main()
