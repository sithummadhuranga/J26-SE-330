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
import litert_reference as R
import make_accuracy_set as MA
import make_resize_golden as MG
from colorimetric import marker as M

REPO = common.REPO
LUT_JSON = REPO / "mobile" / "test" / "measurement" / "golden" / "lut_full.json"


def run(module, argv):
    out = io.StringIO()
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(out):
        module.main()
    return out.getvalue()


class ResizeGoldenTest(unittest.TestCase):
    def test_writes_three_large_photos_their_canvases_and_the_numbers_the_dart_code_must_reproduce(self):
        d = tempfile.mkdtemp()
        np.random.default_rng(0).integers(0, 256, (480, 640, 3), dtype=np.uint8).tofile(os.path.join(d, "100020.rgb.u8"))
        run(MG, ["make_resize_golden.py", "--golden", d, "--id", "100020"])
        with open(os.path.join(d, "resize_manifest.json")) as f:
            m = json.load(f)
        self.assertEqual(sorted(m), ["landscape", "portrait", "square"])
        self.assertEqual((m["landscape"]["width"], m["landscape"]["height"], m["landscape"]["pad_left"]), (3200, 2400, 0))
        self.assertAlmostEqual(m["landscape"]["scale"], 0.2)
        self.assertEqual((m["portrait"]["width"], m["portrait"]["height"], m["portrait"]["pad_left"]), (2400, 3200, 140))
        self.assertAlmostEqual(m["portrait"]["scale"], 0.15)
        self.assertEqual((m["square"]["width"], m["square"]["pad_left"], m["square"]["pad_top"]), (2000, 80, 0))
        for name, v in m.items():
            self.assertEqual(os.path.getsize(os.path.join(d, f"resize_{name}.canvas.rgb.u8")), 640 * 480 * 3)
            self.assertEqual(os.path.getsize(os.path.join(d, f"resize_{name}.big.rgb.u8")), v["width"] * v["height"] * 3)


class AccuracySetTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.images, self.train = os.path.join(self.tmp, "imgs"), os.path.join(self.tmp, "dfuc")
        os.makedirs(self.images)
        os.makedirs(os.path.join(self.train, "DFUC2022_train_masks"))
        self.gt = np.zeros((480, 640), bool)
        self.gt[100:200, 100:200] = True
        for i in ("a", "b"):
            cv2.imwrite(os.path.join(self.images, i + ".jpg"), np.zeros((480, 640, 3), np.uint8))
            cv2.imwrite(os.path.join(self.train, "DFUC2022_train_masks", i + ".png"), self.gt.astype(np.uint8) * 255)
        fake = types.ModuleType("ai_edge_litert.interpreter")
        fake.Interpreter = lambda model_path: types.SimpleNamespace(allocate_tensors=lambda: None)
        pkg = types.ModuleType("ai_edge_litert")
        self.patches = mock.patch.dict(sys.modules, {"ai_edge_litert": pkg, "ai_edge_litert.interpreter": fake})
        self.patches.start()
        self.addCleanup(self.patches.stop)

    def test_copies_the_images_writes_the_masks_and_the_python_scores(self):
        out = os.path.join(self.tmp, "out")
        shifted = np.zeros((480, 640), np.uint8)
        shifted[100:200, 150:250] = 1                                        # half of the wound found: Dice 0.5
        with mock.patch.object(R, "segment", lambda it, rgb: {"union": shifted}):
            run(MA, ["make_accuracy_set.py", "--model", "m.tflite", "--images", self.images, "--dfuc-train", self.train, "--out", out])
        with open(os.path.join(out, "manifest.json")) as f:
            m = json.load(f)
        self.assertEqual(m["ids"], ["a", "b"])
        self.assertAlmostEqual(m["python_mean_dice"], 0.5)
        self.assertAlmostEqual(m["python_mean_iou"], 1 / 3)
        self.assertAlmostEqual(m["python_pooled_dice"], 0.5)
        self.assertEqual(m["python_missed"], 0)
        self.assertEqual(m["model"], "m.tflite")
        gt = np.fromfile(os.path.join(out, "a.gt.u8"), np.uint8)
        self.assertEqual(gt.size, 640 * 480)
        self.assertEqual(int(gt.sum()), 100 * 100)
        self.assertTrue(os.path.exists(os.path.join(out, "a.jpg")))

    def test_a_model_that_finds_nothing_is_counted_as_missed(self):
        with mock.patch.object(R, "segment", lambda it, rgb: {"union": np.zeros((480, 640), np.uint8)}):
            run(MA, ["make_accuracy_set.py", "--model", "m.tflite", "--images", self.images, "--dfuc-train", self.train, "--out", os.path.join(self.tmp, "o2")])
        with open(os.path.join(self.tmp, "o2", "manifest.json")) as f:
            self.assertEqual(json.load(f)["python_missed"], 2)

    def test_an_image_that_is_not_640_by_480_is_refused(self):
        cv2.imwrite(os.path.join(self.images, "c.jpg"), np.zeros((100, 100, 3), np.uint8))
        with mock.patch.object(R, "segment", lambda it, rgb: {"union": np.zeros((480, 640), np.uint8)}):
            with self.assertRaises(AssertionError):
                run(MA, ["make_accuracy_set.py", "--model", "m.tflite", "--images", self.images, "--dfuc-train", self.train, "--out", os.path.join(self.tmp, "o3")])


class LutGoldenTest(unittest.TestCase):
    def test_the_committed_tables_are_what_the_colour_components_code_gives_today(self):
        data = json.loads(LUT_JSON.read_text())
        self.assertGreaterEqual(len(data["cases"]), 7)
        ramp = np.stack([np.arange(256, dtype=np.uint8)] * 3, axis=-1)[None]
        for c in data["cases"]:
            out = M.apply_gains(ramp, np.array(c["gains"]))[0]
            for ch in range(3):
                self.assertEqual(out[:, ch].astype(int).tolist(), c["lut"][ch], f"gains {c['gains']} channel {ch}")

    def test_the_tables_have_256_entries_that_never_decrease(self):
        for c in json.loads(LUT_JSON.read_text())["cases"]:
            for lut in c["lut"]:
                self.assertEqual(len(lut), 256)
                self.assertTrue(all(b >= a for a, b in zip(lut, lut[1:])))
                self.assertEqual(lut[0], 0)

    def test_gain_one_is_the_identity_table(self):
        case = next(c for c in json.loads(LUT_JSON.read_text())["cases"] if c["gains"] == [1.0, 1.0, 1.0])
        self.assertEqual(case["lut"][0], list(range(256)))


if __name__ == "__main__":
    unittest.main()
