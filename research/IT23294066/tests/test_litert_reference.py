import types
import unittest

import cv2
import numpy as np

import common  # noqa: F401
import litert_reference as R


def pred_tensor(dets):
    """(1, 37, 6300) with the given detections: (cx, cy, w, h normalised, score, coefficient 0)."""
    p = np.zeros((1, 37, 6300), np.float32)
    for k, (cx, cy, w, h, s, c0) in enumerate(dets):
        p[0, 0:5, k] = [cx, cy, w, h, s]
        p[0, 5, k] = c0
    return p


class LetterboxTest(unittest.TestCase):
    def test_a_640_by_480_photo_is_not_changed(self):
        img = np.random.default_rng(0).integers(0, 256, (480, 640, 3), dtype=np.uint8)
        canvas, r, pad = R.letterbox(img)
        self.assertTrue(np.array_equal(canvas, img))
        self.assertEqual((r, pad), (1.0, (0, 0)))

    def test_shrinking_uses_area_averaging(self):
        yy, xx = np.mgrid[0:960, 0:1280]
        check = (((xx + yy) % 2) * 255).astype(np.uint8)                    # finest possible checkerboard
        canvas, r, pad = R.letterbox(np.stack([check] * 3, axis=-1))
        self.assertEqual(r, 0.5)
        self.assertLess(canvas.std(), 2.0)                                  # area average: flat grey, no aliasing pattern
        self.assertAlmostEqual(float(canvas.mean()), 127.5, delta=1.0)

    def test_a_portrait_photo_is_padded_left_and_right_with_114(self):
        img = np.full((640, 480, 3), 200, np.uint8)
        canvas, r, (left, top) = R.letterbox(img)
        self.assertEqual(canvas.shape, (480, 640, 3))
        self.assertAlmostEqual(r, 0.75)
        self.assertEqual((left, top), (140, 0))
        self.assertTrue((canvas[:, :140] == 114).all())
        self.assertTrue((canvas[:, 500:] == 114).all())
        self.assertTrue((canvas[:, 140:500] == 200).all())

    def test_an_odd_padding_puts_the_extra_pixel_on_the_right_like_ultralytics(self):
        canvas, r, (left, top) = R.letterbox(np.full((480, 639, 3), 50, np.uint8))
        self.assertEqual((left, top), (0, 0))
        self.assertTrue((canvas[:, 639] == 114).all())                      # one padded column at the right edge


class InputTest(unittest.TestCase):
    def test_to_input_is_float_nchw_in_0_1(self):
        canvas = np.zeros((480, 640, 3), np.uint8)
        canvas[..., 0] = 255
        x = R.to_input(canvas)
        self.assertEqual((x.shape, x.dtype), ((1, 3, 480, 640), np.float32))
        self.assertEqual(float(x[0, 0].min()), 1.0)                         # channel 0 (red) first
        self.assertEqual(float(x[0, 1].max()), 0.0)
        self.assertTrue(x.flags["C_CONTIGUOUS"])


class IouTest(unittest.TestCase):
    def test_known_overlaps(self):
        box = np.array([0, 0, 10, 10], np.float32)
        others = np.array([[0, 0, 10, 10], [20, 20, 30, 30], [5, 0, 15, 10]], np.float32)
        iou = R.iou_one_to_many(box, others)
        self.assertAlmostEqual(float(iou[0]), 1.0, places=6)
        self.assertAlmostEqual(float(iou[1]), 0.0, places=6)
        self.assertAlmostEqual(float(iou[2]), 1 / 3, places=6)


class DecodeTest(unittest.TestCase):
    def test_boxes_are_converted_from_normalised_centre_size_to_pixel_corners(self):
        boxes, scores, coef = R.decode(pred_tensor([(0.5, 0.5, 0.25, 0.5, 0.9, 3.0)]))
        self.assertTrue(np.allclose(boxes, [[240, 120, 400, 360]]))         # 640 x 480 input
        self.assertTrue(np.allclose(scores, [0.9]))
        self.assertEqual(coef[0, 0], 3.0)

    def test_scores_below_the_threshold_are_dropped(self):
        boxes, scores, _ = R.decode(pred_tensor([(0.5, 0.5, 0.2, 0.2, 0.24, 0), (0.2, 0.2, 0.1, 0.1, 0.26, 0)]))
        self.assertEqual(len(scores), 1)
        self.assertAlmostEqual(float(scores[0]), 0.26, places=6)

    def test_nothing_above_the_threshold_gives_empty_arrays(self):
        boxes, scores, coef = R.decode(pred_tensor([(0.5, 0.5, 0.2, 0.2, 0.1, 0)]))
        self.assertEqual((boxes.shape, scores.shape, coef.shape), ((0, 4), (0,), (0, 32)))

    def test_overlapping_boxes_keep_only_the_best(self):
        boxes, scores, _ = R.decode(pred_tensor([(0.5, 0.5, 0.3, 0.3, 0.6, 0), (0.51, 0.5, 0.3, 0.3, 0.9, 0), (0.1, 0.1, 0.05, 0.05, 0.5, 0)]))
        self.assertEqual(len(scores), 2)
        self.assertAlmostEqual(float(scores[0]), 0.9, places=6)             # highest first

    def test_at_most_max_det_boxes_are_returned(self):
        dets = [(0.01 + 0.015 * (k % 60), 0.01 + 0.02 * (k // 60), 0.005, 0.005, 0.9 - k * 1e-4, 0) for k in range(300)]
        boxes, scores, _ = R.decode(pred_tensor(dets), max_det=5)
        self.assertEqual(len(scores), 5)


class MaskTest(unittest.TestCase):
    def proto(self, value=1.0):
        p = np.zeros((1, 32, 120, 160), np.float32)
        p[0, 0] = value
        return p

    def test_mask_is_cropped_to_the_box(self):
        coef = np.zeros((1, 32), np.float32)
        coef[0, 0] = 1.0
        box = np.array([[100, 100, 300, 250]], np.float32)
        m = R.instance_masks(self.proto(), coef, box)
        self.assertEqual(m.shape, (1, 480, 640))
        self.assertTrue((m[0, 100:250, 100:300] == 1).all())
        self.assertEqual(int(m[0].sum()), 150 * 200)                        # nothing outside the box

    def test_negative_logits_give_an_empty_mask_and_no_instances_give_no_masks(self):
        coef = np.zeros((1, 32), np.float32)
        coef[0, 0] = 1.0
        self.assertEqual(int(R.instance_masks(self.proto(-1.0), coef, np.array([[0, 0, 640, 480]], np.float32)).sum()), 0)
        self.assertEqual(R.instance_masks(self.proto(), np.zeros((0, 32), np.float32), np.zeros((0, 4), np.float32)).shape, (0, 480, 640))


class FakeInterpreter:
    def __init__(self, pred, proto, swap=False):
        self.t = [proto, pred] if swap else [pred, proto]
        self.fed = None

    def get_input_details(self):
        return [{"index": 0}]

    def get_output_details(self):
        return [{"index": 0}, {"index": 1}]

    def set_tensor(self, i, x):
        self.fed = x

    def invoke(self):
        pass

    def get_tensor(self, i):
        return self.t[i]


class SegmentTest(unittest.TestCase):
    def run_segment(self, swap):
        proto = np.zeros((1, 32, 120, 160), np.float32)
        proto[0, 0] = 1.0
        pred = pred_tensor([(0.5, 0.5, 0.25, 0.5, 0.9, 1.0)])
        interp = FakeInterpreter(pred, proto, swap)
        out = R.segment(interp, np.full((480, 640, 3), 90, np.uint8))
        return out, interp

    def test_runs_the_model_and_returns_the_union_mask(self):
        out, interp = self.run_segment(False)
        self.assertEqual(interp.fed.shape, (1, 3, 480, 640))
        self.assertEqual(int(out["union"].sum()), 160 * 240)                # the box 240..400 x 120..360
        self.assertEqual(len(out["scores"]), 1)

    def test_output_order_does_not_matter(self):
        a, _ = self.run_segment(False)
        b, _ = self.run_segment(True)
        self.assertTrue(np.array_equal(a["union"], b["union"]))

    def test_no_detection_gives_an_empty_union(self):
        interp = FakeInterpreter(pred_tensor([]), np.zeros((1, 32, 120, 160), np.float32))
        out = R.segment(interp, np.full((480, 640, 3), 90, np.uint8))
        self.assertEqual(int(out["union"].sum()), 0)
        self.assertEqual(out["union"].shape, (480, 640))


if __name__ == "__main__":
    unittest.main()
