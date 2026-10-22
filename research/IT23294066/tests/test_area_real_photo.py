import unittest

import cv2
import numpy as np

import common  # noqa: F401
import area_real_photo as R
from test_sticker_finder import photo


class ClassifyTest(unittest.TestCase):
    def contour(self, draw):
        img = np.zeros((300, 300), np.uint8)
        draw(img)
        return max(cv2.findContours(img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)[0], key=cv2.contourArea)

    def test_shapes_are_told_apart(self):
        self.assertEqual(R.classify(self.contour(lambda i: cv2.rectangle(i, (50, 60), (200, 160), 255, -1))), "rectangle")
        self.assertEqual(R.classify(self.contour(lambda i: cv2.circle(i, (150, 150), 80, 255, -1))), "circle")
        self.assertEqual(R.classify(self.contour(lambda i: cv2.fillPoly(i, [np.array([[50, 50], [250, 50], [50, 200]])], 255))), "triangle")

    def test_a_thin_line_is_nothing(self):
        self.assertIsNone(R.classify(self.contour(lambda i: cv2.line(i, (10, 10), (280, 20), 255, 2))))


class RedShapesTest(unittest.TestCase):
    def test_red_is_found_other_colours_are_not(self):
        img = np.zeros((100, 400, 3), np.uint8)
        img[20:80, 10:90] = (200, 40, 40)
        img[20:80, 110:190] = (40, 200, 40)
        img[20:80, 210:290] = (255, 255, 255)
        img[20:80, 310:390] = (30, 30, 200)
        m = R.red_shapes(img)
        self.assertGreater(m[:, :100].sum(), 3000)
        self.assertEqual(int(m[:, 100:].sum()), 0)


class MeasurePhotoTest(unittest.TestCase):
    def test_areas_on_a_tilted_photo_are_close_to_the_printed_areas_with_the_sticker_only(self):
        img, _ = photo(tilt=25, rot=40)
        stickers, rows = R.measure_photo(img, 50.0)
        self.assertEqual(len(stickers), 6)
        self.assertEqual({r["kind"] for r in rows}, {"rectangle", "circle", "triangle"})
        for r in rows:
            self.assertLess(abs(r["hom_pct"]), 3.0, r)
            self.assertGreater(abs(r["uni_pct"]), 10.0, r)

    def test_no_sticker_means_no_rows(self):
        stickers, rows = R.measure_photo(np.full((600, 800, 3), 150, np.uint8), 50.0)
        self.assertEqual((stickers, rows), ([], []))


if __name__ == "__main__":
    unittest.main()
