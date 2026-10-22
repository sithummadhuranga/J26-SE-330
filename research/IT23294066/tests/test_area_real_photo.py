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


def blurred_red_rectangle(blur, w=160, h=100, size=(400, 500)):
    """A red rectangle (known pixel area) on white paper, blurred the way a lens does it: in linear light."""
    lin = np.ones(size + (3,), np.float32)
    lin[120:120 + h, 150:150 + w] = ((200 / 255 / 1.0) ** 2.2, (40 / 255) ** 2.2, (40 / 255) ** 2.2)
    if blur:
        lin = cv2.GaussianBlur(lin, (0, 0), blur)
    return (np.clip(lin, 0, 1) ** (1 / 2.2) * 255).round().astype(np.uint8), w * h


class ShapeAlphaTest(unittest.TestCase):
    def contour_of(self, img):
        cs, _ = cv2.findContours(R.red_shapes(img), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        return max(cs, key=cv2.contourArea)

    def test_summed_coverage_equals_the_true_area_whatever_the_blur(self):
        for blur in (0.5, 1.5, 2.5):
            img, true_px = blurred_red_rectangle(blur)
            a = R.shape_alpha(img, self.contour_of(img))
            self.assertAlmostEqual(float(a.sum()) / true_px, 1.0, delta=0.012, msg=f"blur {blur}")

    def test_coverage_is_between_zero_and_one_and_zero_far_from_the_shape(self):
        img, _ = blurred_red_rectangle(1.5)
        a = R.shape_alpha(img, self.contour_of(img))
        self.assertGreaterEqual(float(a.min()), 0.0)
        self.assertLessEqual(float(a.max()), 1.0)
        self.assertEqual(float(a[:20, :20].sum()), 0.0)

    def test_a_shape_the_same_colour_as_the_paper_falls_back_to_the_hard_mask(self):
        img = np.full((200, 200, 3), 255, np.uint8)
        c = np.array([[[50, 50]], [[150, 50]], [[150, 150]], [[50, 150]]], np.int32)
        a = R.shape_alpha(img, c)
        self.assertEqual(a.shape, (200, 200))
        self.assertEqual(set(np.unique(a)), {0.0, 1.0})


class MeasurePhotoTest(unittest.TestCase):
    def test_areas_on_a_tilted_photo_are_close_to_the_printed_areas_with_the_sticker_only(self):
        img, _ = photo(tilt=25, rot=40)
        stickers, rows = R.measure_photo(img, 50.0)
        self.assertEqual(len(stickers), 6)
        self.assertEqual({r["kind"] for r in rows}, {"rectangle", "circle", "triangle"})
        for r in rows:
            self.assertLess(abs(r["hom_pct"]), 3.0, r)
            self.assertGreater(abs(r["uni_pct"]), 10.0, r)

    def test_the_soft_coverage_area_is_at_least_as_good_as_the_hard_mask_and_within_two_percent(self):
        for tilt, blur in ((0, 1.5), (25, 1.5), (30, 2.5)):
            img, _ = photo(tilt=tilt, rot=20, blur=blur, ppm=11.0)
            _, rows = R.measure_photo(img, 50.0)
            self.assertEqual(len(rows), 3, f"tilt {tilt} blur {blur}")
            for r in rows:
                self.assertLess(abs(r["soft_pct"]), 2.0, (tilt, blur, r["kind"], r["soft_pct"]))
            self.assertLessEqual(np.mean([abs(r["soft_pct"]) for r in rows]), np.mean([abs(r["hom_pct"]) for r in rows]) + 0.3)

    def test_no_sticker_means_no_rows(self):
        stickers, rows = R.measure_photo(np.full((600, 800, 3), 150, np.uint8), 50.0)
        self.assertEqual((stickers, rows), ([], []))


if __name__ == "__main__":
    unittest.main()
