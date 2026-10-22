import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

import numpy as np

import common  # noqa: F401
import colour_regions as C


def picture(parts, h=40, w=50):
    """An image whose masked pixels are filled with the given (rgb, count) parts in row order."""
    rgb = np.zeros((h, w, 3), np.uint8)
    mask = np.zeros((h, w), np.uint8)
    flat_rgb, flat_mask = rgb.reshape(-1, 3), mask.reshape(-1)
    pos = 0
    for colour, n in parts:
        flat_rgb[pos:pos + n] = colour
        flat_mask[pos:pos + n] = 1
        pos += n
    return rgb, mask


class RegionsTest(unittest.TestCase):
    def test_no_wound_gives_no_regions(self):
        self.assertEqual(C.colour_regions(np.zeros((10, 10, 3), np.uint8), np.zeros((10, 10), np.uint8)), [])

    def test_one_colour_is_one_region_with_all_pixels(self):
        out = C.colour_regions(*picture([((180, 90, 60), 300)]))
        self.assertEqual(out, [{"cluster": 1, "percent": 100.0, "centre": [180.0, 90.0, 60.0]}])

    def test_two_colours_are_split_70_30_largest_first(self):
        out = C.colour_regions(*picture([((200, 40, 40), 700), ((60, 60, 200), 300)]))
        self.assertEqual([(r["cluster"], r["percent"]) for r in out], [(1, 70.0), (2, 30.0)])
        self.assertEqual(out[0]["centre"], [200.0, 40.0, 40.0])
        self.assertEqual(out[1]["centre"], [60.0, 60.0, 200.0])

    def test_percentages_always_add_up_to_exactly_100(self):
        rng = np.random.default_rng(0)
        for _ in range(20):
            n = int(rng.integers(50, 1500))
            rgb = rng.integers(0, 256, (40, 50, 3), dtype=np.uint8)
            mask = np.zeros((40, 50), np.uint8)
            mask.reshape(-1)[:n] = 1
            out = C.colour_regions(rgb, mask)
            self.assertAlmostEqual(sum(r["percent"] for r in out), 100.0, places=9)
            self.assertLessEqual(len(out), C.K)

    def test_groups_are_numbered_from_largest_to_smallest(self):
        out = C.colour_regions(*picture([((10, 10, 10), 100), ((120, 120, 120), 500), ((240, 240, 240), 300)]))
        pct = [r["percent"] for r in out]
        self.assertEqual(pct, sorted(pct, reverse=True))
        self.assertEqual([r["cluster"] for r in out], [1, 2, 3])

    def test_the_result_does_not_depend_on_any_random_start(self):
        pic = picture([((200, 40, 40), 400), ((60, 60, 200), 300), ((30, 200, 30), 200)])
        self.assertEqual(C.colour_regions(*pic), C.colour_regions(*pic))

    def test_fewer_distinct_colours_than_k_gives_fewer_regions(self):
        out = C.colour_regions(*picture([((200, 40, 40), 100), ((60, 60, 200), 100)]), k=5)
        self.assertEqual(len(out), 2)

    def test_large_wounds_are_subsampled_but_still_split_correctly(self):
        rgb = np.zeros((200, 200, 3), np.uint8)
        rgb[:120] = (200, 40, 40)
        rgb[120:] = (60, 60, 200)
        mask = np.ones((200, 200), np.uint8)                                 # 40,000 pixels > MAX_SAMPLE
        out = C.colour_regions(rgb, mask)
        self.assertEqual([r["percent"] for r in out], [60.0, 40.0])

    def test_rounding_is_half_up_so_python_and_the_phone_agree(self):
        self.assertEqual(float(C._half_up(2.5, 1)), 3.0)
        self.assertEqual(float(C._half_up(25.0, 10)), 2.5)
        self.assertEqual(float(C._half_up(-0.4, 1)), 0.0)


class MainTest(unittest.TestCase):
    def test_golden_files_are_checked_and_regions_are_written(self):
        d = tempfile.mkdtemp()
        h, w = 48, 64
        rgb, mask = picture([((200, 40, 40), 700), ((60, 60, 200), 300)], h, w)
        rgb.tofile(os.path.join(d, "a1.rgb.u8"))
        mask.tofile(os.path.join(d, "a1.union.u8"))
        with open(os.path.join(d, "manifest.json"), "w") as f:
            json.dump({"input_hw": [h, w], "images": [{"id": "a1"}]}, f)
        out = io.StringIO()
        with mock.patch.object(sys, "argv", ["colour_regions.py", "--golden", d, "--write"]), contextlib.redirect_stdout(out):
            C.main()
        self.assertIn("a1", out.getvalue())
        with open(os.path.join(d, "a1.regions.json")) as f:
            self.assertEqual([r["percent"] for r in json.load(f)], [70.0, 30.0])


if __name__ == "__main__":
    unittest.main()
