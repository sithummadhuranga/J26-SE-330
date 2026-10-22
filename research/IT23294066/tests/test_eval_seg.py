import contextlib
import io
import json
import os
import sys
import tempfile
import types
import unittest
from unittest import mock

import cv2
import numpy as np

import common  # noqa: F401
import eval_seg as E


class Tensor:
    """Stands in for a torch tensor: .cpu().numpy()."""
    def __init__(self, a):
        self.a = a

    def cpu(self):
        return self

    def numpy(self):
        return self.a


def fake_result(masks=None, conf=None):
    r = types.SimpleNamespace(masks=None, boxes=None)
    if masks is not None:
        r.masks = types.SimpleNamespace(data=Tensor(np.asarray(masks, np.float32)))
        r.boxes = types.SimpleNamespace(conf=Tensor(np.asarray(conf, np.float32)))
    return r


def square(h, w, y, x, s):
    m = np.zeros((h, w), bool)
    m[y:y + s, x:x + s] = True
    return m


class MetricsTest(unittest.TestCase):
    def test_dice_and_iou_of_known_overlaps(self):
        a = square(40, 40, 0, 0, 10)
        self.assertEqual(E.dice_iou(a, a), (1.0, 1.0))
        self.assertEqual(E.dice_iou(a, square(40, 40, 20, 20, 10)), (0.0, 0.0))
        d, j = E.dice_iou(a, square(40, 40, 0, 5, 10))                     # half overlap: 50 shared of 100 + 100
        self.assertAlmostEqual(d, 0.5)
        self.assertAlmostEqual(j, 1 / 3)

    def test_two_empty_masks_count_as_perfect_and_one_empty_as_zero(self):
        e = np.zeros((10, 10), bool)
        self.assertEqual(E.dice_iou(e, e), (1.0, 1.0))
        self.assertEqual(E.dice_iou(e, square(10, 10, 0, 0, 3)), (0.0, 0.0))

    def test_bootstrap_interval(self):
        lo0, hi0 = E.bootstrap_ci(np.full(50, 0.7))
        self.assertAlmostEqual(lo0, 0.7)
        self.assertAlmostEqual(hi0, 0.7)
        v = np.random.default_rng(0).uniform(0, 1, 200)
        lo, hi = E.bootstrap_ci(v)
        self.assertLess(lo, v.mean())
        self.assertGreater(hi, v.mean())
        self.assertEqual(E.bootstrap_ci(v), E.bootstrap_ci(v))             # same seed, same answer


class ScoreTest(unittest.TestCase):
    def setUp(self):
        self.gt = square(40, 40, 5, 5, 10)
        self.hit = self.gt.copy()
        self.other = square(40, 40, 25, 25, 8)
        self.records = [("a", self.gt, np.stack([self.hit, self.other]), np.array([0.9, 0.3])),
                        ("b", self.gt, np.zeros((0, 40, 40), bool), np.zeros(0))]

    def test_instances_above_the_threshold_are_merged_into_one_mask(self):
        low = E.score(self.records, 0.25)
        high = E.score(self.records, 0.5)
        self.assertEqual(low[0]["n_pred"], 2)
        self.assertEqual(low[0]["pred_px"], 100 + 64)
        self.assertEqual(high[0]["n_pred"], 1)
        self.assertEqual(high[0]["dice"], 1.0)
        self.assertEqual(low[0]["inter"], 100)

    def test_an_image_without_detection_scores_zero(self):
        row = E.score(self.records, 0.25)[1]
        self.assertEqual((row["dice"], row["pred_px"], row["n_pred"], row["gt_px"]), (0.0, 0, 0, 100))

    def test_summary_counts_missed_images_and_wound_size_groups(self):
        sizes = [500, 2000, 5000, 20000]                                    # one wound in each size group
        rows = [dict(id=str(i), dice=d, iou=d, gt_px=g, pred_px=p, inter=int(g * d)) for i, (g, d, p) in
                enumerate(zip(sizes, [0.9, 0.8, 0.0, 0.5], [500, 2000, 0, 15000]))]
        s = E.summarise(rows)
        self.assertEqual(s["images"], 4)
        self.assertEqual(s["images_missed"], 1)
        self.assertEqual(s["images_dice_below_0_5"], 1)
        self.assertAlmostEqual(s["mean_dice"], (0.9 + 0.8 + 0 + 0.5) / 4)
        self.assertEqual({k: v["images"] for k, v in s["mean_dice_by_wound_size_quartile"].items()}, {"Q1": 1, "Q2": 1, "Q3": 1, "Q4": 1})
        self.assertAlmostEqual(s["mean_dice_by_wound_size_quartile"]["Q3"]["mean_dice"], 0.0)
        lo, hi = s["dice_ci95"]
        self.assertLessEqual(lo, s["mean_dice"])
        self.assertGreaterEqual(hi, s["mean_dice"])


class CollectTest(unittest.TestCase):
    def make_dir(self, n, h=40, w=40):
        d = tempfile.mkdtemp()
        paths = []
        for i in range(n):
            cv2.imwrite(os.path.join(d, f"{i}.png"), (square(h, w, 5, 5, 10) * 255).astype(np.uint8))
            paths.append(dict(id=str(i), image="unused", mask=os.path.join(d, f"{i}.png")))
        return paths

    def test_predicts_in_chunks_of_50_and_reads_the_ground_truth_at_127(self):
        paths = self.make_dir(120)
        model = mock.Mock()
        model.predict.side_effect = lambda imgs, **kw: [fake_result(np.stack([square(40, 40, 5, 5, 10)]), [0.9]) for _ in imgs]
        out = E.collect(model, paths, "cpu", 640)
        self.assertEqual(model.predict.call_count, 3)                       # 50 + 50 + 20
        self.assertEqual([len(c.args[0]) for c in model.predict.call_args_list], [50, 50, 20])
        self.assertEqual(len(out), 120)
        self.assertEqual(int(out[0][1].sum()), 100)
        self.assertEqual(model.predict.call_args.kwargs["conf"], 0.01)       # lowest useful threshold, filtered later

    def test_too_few_predictions_stop_the_run(self):
        model = mock.Mock()
        model.predict.side_effect = lambda imgs, **kw: [fake_result()]       # the Ultralytics list-length bug returned fewer
        with self.assertRaises(AssertionError):
            E.collect(model, self.make_dir(5), "cpu", 640)

    def test_a_model_that_finds_nothing_is_refused(self):
        model = mock.Mock()
        model.predict.side_effect = lambda imgs, **kw: [fake_result() for _ in imgs]
        with self.assertRaises(RuntimeError):
            E.collect(model, self.make_dir(10), "cpu", 640)


class MainTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.split = os.path.join(self.tmp, "split")
        self.masks = os.path.join(self.tmp, "DFUC2022_train_masks")
        os.makedirs(self.split)
        os.makedirs(self.masks)
        for i in range(6):
            cv2.imwrite(os.path.join(self.split, f"{i}.jpg"), np.zeros((40, 40, 3), np.uint8))
            cv2.imwrite(os.path.join(self.masks, f"{i}.png"), (square(40, 40, 5, 5, 10) * 255).astype(np.uint8))
        self.weights = os.path.join(self.tmp, "w.pt")
        with open(self.weights, "wb") as f:
            f.write(b"fake weights")

    class FakeYOLO:
        def __init__(self, w):
            pass

        def predict(self, imgs, **kw):
            gt = square(40, 40, 5, 5, 10)
            return [fake_result(np.stack([gt, square(40, 40, 30, 30, 5)]), [0.8, 0.12]) for _ in imgs]

    def run_main(self, *extra):
        argv = ["eval_seg.py", "--weights", self.weights, "--split-dir", self.split, "--dfuc-train", self.tmp, "--device", "cpu", *extra]
        out = io.StringIO()
        with mock.patch.object(sys, "argv", argv), mock.patch.object(E, "YOLO", self.FakeYOLO), contextlib.redirect_stdout(out):
            E.main()
        return out.getvalue()

    def test_sweep_prints_a_table_and_the_best_threshold(self):
        text = self.run_main("--sweep")
        self.assertIn("best confidence threshold on this split:", text)
        self.assertIn("0.05", text)
        best = float(text.strip().splitlines()[-1].split(":")[1])
        self.assertGreaterEqual(best, 0.15)                                  # at 0.05 and 0.1 the 0.12 false instance hurts

    def test_scoring_run_writes_a_summary_and_per_image_results(self):
        out_file = os.path.join(self.tmp, "out.json")
        self.run_main("--conf", "0.25", "--out", out_file)
        with open(out_file) as f:
            data = json.load(f)
        self.assertEqual(sorted(data), ["per_image", "summary"])
        self.assertEqual(len(data["per_image"]), 6)
        self.assertEqual(data["summary"]["mean_dice"], 1.0)
        self.assertEqual(data["summary"]["conf"], 0.25)
        self.assertEqual(len(data["summary"]["weights_sha256"]), 16)

    def test_neither_conf_nor_sweep_is_an_error(self):
        with mock.patch.object(sys, "argv", ["eval_seg.py", "--weights", self.weights, "--split-dir", self.split, "--dfuc-train", self.tmp]):
            with self.assertRaises(SystemExit):
                with contextlib.redirect_stderr(io.StringIO()):
                    E.main()


if __name__ == "__main__":
    unittest.main()
