import contextlib
import io
import json
import os
import sys
import tempfile
import types
import unittest
import zipfile
from unittest import mock

import cv2
import numpy as np

import common  # noqa: F401
import make_submission as MS
import train_seg as TS


def run(module, argv):
    out = io.StringIO()
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(out):
        module.main()
    return out.getvalue()


class TrainSegTest(unittest.TestCase):
    def fake_yolo(self, save_dir):
        calls = {}

        class FakeYOLO:
            def __init__(self, model):
                calls["model"] = model

            def train(self, **kw):
                calls["train"] = kw
                return types.SimpleNamespace(save_dir=save_dir)

        return FakeYOLO, calls

    def test_the_proposal_augmentation_preset_is_exactly_the_one_in_the_proposal(self):
        self.assertEqual(TS.AUG["proposal"], dict(degrees=15.0, fliplr=0.5, flipud=0.5, hsv_h=0.0, hsv_s=0.0, hsv_v=0.1))
        self.assertEqual(TS.AUG["default"], {})

    def test_device_choice_prefers_cuda_then_mps_then_cpu(self):
        with mock.patch.object(TS.torch.cuda, "is_available", return_value=True):
            self.assertEqual(TS.pick_device(), 0)
        with mock.patch.object(TS.torch.cuda, "is_available", return_value=False), \
                mock.patch.object(TS.torch.backends.mps, "is_available", return_value=True):
            self.assertEqual(TS.pick_device(), "mps")
        with mock.patch.object(TS.torch.cuda, "is_available", return_value=False), \
                mock.patch.object(TS.torch.backends.mps, "is_available", return_value=False):
            self.assertEqual(TS.pick_device(), "cpu")

    def test_main_passes_a_reproducible_configuration_and_records_it(self):
        save = tempfile.mkdtemp()
        Fake, calls = self.fake_yolo(save)
        with mock.patch.object(TS, "YOLO", Fake):
            text = run(TS, ["train_seg.py", "--data", "d.yaml", "--name", "run1", "--project", "/tmp/runs", "--seed", "3", "--device", "cpu"])
        kw = calls["train"]
        self.assertEqual(calls["model"], "yolo11n-seg.pt")
        self.assertEqual((kw["epochs"], kw["imgsz"], kw["batch"], kw["patience"], kw["seed"]), (100, 640, 16, 30, 3))
        self.assertTrue(kw["deterministic"])
        self.assertFalse(kw["exist_ok"])                                     # never overwrite an earlier run
        self.assertEqual(kw["name"], "run1")
        self.assertEqual(kw["project"], "/tmp/runs")
        self.assertEqual((kw["degrees"], kw["fliplr"], kw["flipud"], kw["hsv_v"]), (15.0, 0.5, 0.5, 0.1))
        with open(os.path.join(save, "args.json")) as f:
            meta = json.load(f)
        self.assertEqual((meta["aug_preset"], meta["seed"], meta["model"]), ("proposal", 3, "yolo11n-seg.pt"))
        self.assertIn("ultralytics", meta)
        self.assertIn("torch", meta)
        self.assertIn("saved to", text)

    def test_the_default_preset_adds_no_augmentation_settings_and_options_are_passed_on(self):
        Fake, calls = self.fake_yolo(tempfile.mkdtemp())
        with mock.patch.object(TS, "YOLO", Fake):
            run(TS, ["train_seg.py", "--data", "d.yaml", "--name", "r", "--aug", "default", "--epochs", "2", "--fraction", "0.1",
                     "--workers", "1", "--device", "cpu", "--model", "m.pt"])
        kw = calls["train"]
        self.assertNotIn("degrees", kw)
        self.assertEqual((kw["epochs"], kw["fraction"], kw["workers"]), (2, 0.1, 1))
        self.assertEqual(calls["model"], "m.pt")


def result(h, w, mask=None):
    r = types.SimpleNamespace(orig_shape=(h, w), masks=None)
    if mask is not None:
        class T:
            def __init__(self, a): self.a = a
            def cpu(self): return self
            def numpy(self): return self.a
        r.masks = types.SimpleNamespace(data=T(mask[None].astype(np.float32)))
    return r


class SubmissionTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.images, self.gt = os.path.join(self.tmp, "test"), os.path.join(self.tmp, "gt")
        os.makedirs(self.images)
        os.makedirs(self.gt)
        self.shapes = {"100001": (480, 640), "100002": (640, 480), "100003": (480, 640)}   # the second photo is portrait
        for i, (h, w) in self.shapes.items():
            cv2.imwrite(os.path.join(self.images, i + ".jpg"), np.zeros((h, w, 3), np.uint8))
            g = np.zeros((h, w), np.uint8)
            g[50:150, 50:150] = 255
            cv2.imwrite(os.path.join(self.gt, i + ".png"), g)

        class FakeYOLO:
            def __init__(inner, w): pass

            def predict(inner, paths, **kw):
                out = []
                for p in paths:
                    h, w = cv2.imread(p).shape[:2]
                    if os.path.basename(p).startswith("100003"):
                        out.append(result(h, w))                              # nothing detected
                    else:
                        m = np.zeros((h, w), np.float32)
                        m[50:150, 50:150] = 1
                        out.append(result(h, w, m))
                return out

        self.fake = FakeYOLO

    def go(self, *extra):
        out = os.path.join(self.tmp, "masks")
        with mock.patch.object(MS, "YOLO", self.fake):
            text = run(MS, ["make_submission.py", "--weights", "w.pt", "--images", self.images, "--out", out, "--conf", "0.25", *extra])
        return out, text

    def test_one_png_per_photo_with_the_photos_own_size_and_values_0_and_1(self):
        out, text = self.go()
        self.assertEqual(sorted(os.listdir(out)), ["100001.png", "100002.png", "100003.png"])
        for i, (h, w) in self.shapes.items():
            m = cv2.imread(os.path.join(out, i + ".png"), cv2.IMREAD_UNCHANGED)
            self.assertEqual(m.shape, (h, w))
            self.assertEqual(m.dtype, np.uint8)
            self.assertTrue(set(np.unique(m)) <= {0, 1})
        self.assertEqual(int(cv2.imread(os.path.join(out, "100001.png"), 0).sum()), 100 * 100)
        self.assertEqual(int(cv2.imread(os.path.join(out, "100003.png"), 0).sum()), 0)
        self.assertIn("3 masks written", text)
        self.assertIn("images with no detection: 1", text)
        self.assertIn("unreadable or wrong shape/values: 0", text)

    def test_scoring_against_ground_truth_reports_the_mean_dice(self):
        out, text = self.go("--gt", self.gt)
        self.assertIn("mean Dice of the written files against ground truth: 0.6667 over 3 images", text)  # 1, 1 and 0

    def test_a_zip_with_every_mask_is_written_next_to_the_folder(self):
        out, text = self.go("--zip")
        z = os.path.join(self.tmp, "submission.zip")
        self.assertTrue(os.path.exists(z))
        with zipfile.ZipFile(z) as zf:
            self.assertEqual(sorted(zf.namelist()), ["100001.png", "100002.png", "100003.png"])

    def test_a_wrong_number_of_predictions_stops_the_run(self):
        class Short:
            def __init__(inner, w): pass
            def predict(inner, paths, **kw): return []
        with mock.patch.object(MS, "YOLO", Short):
            with self.assertRaises(AssertionError):
                run(MS, ["make_submission.py", "--weights", "w.pt", "--images", self.images, "--out", os.path.join(self.tmp, "o"), "--conf", "0.25"])


if __name__ == "__main__":
    unittest.main()
