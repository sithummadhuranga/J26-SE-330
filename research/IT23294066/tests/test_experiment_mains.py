import contextlib
import io
import json
import os
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

import cv2
import numpy as np

import common  # noqa: F401
import ita_noclass_followup as NC
import ita_stability_test as T
import lighting_test as LT


def run(module, argv):
    out = io.StringIO()
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(out):
        module.main()
    return out.getvalue()


def make_world(n=3):
    """A tiny stand-in for the DFUC folders: n skin-coloured photos with one square wound each."""
    root = Path(tempfile.mkdtemp())
    val = root / "derived" / "yolo_dfuc" / "images" / "val"
    masks = root / "dfuc" / "DFUC2022_train_masks"
    val.mkdir(parents=True)
    masks.mkdir(parents=True)
    rng = np.random.default_rng(0)
    for k in range(n):
        img = np.clip(rng.normal([190, 140, 115], 5, (60, 80, 3)), 0, 255).astype(np.uint8)
        cv2.imwrite(str(val / f"{100000 + k}.jpg"), cv2.cvtColor(img, cv2.COLOR_RGB2BGR))
        m = np.zeros((60, 80), np.uint8)
        m[20:40, 30:50] = 255
        cv2.imwrite(str(masks / f"{100000 + k}.png"), m)
    return root


class PerfectModel:
    """Predicts the true wound for every picture, whatever was done to it, so only the plumbing is under test."""
    def __init__(self, mask_dir):
        self.mask_dir = mask_dir

    def predict(self, paths, **kw):
        out = []
        for p in paths:
            gt = cv2.imread(os.path.join(self.mask_dir, os.path.basename(p)[:-4] + ".png"), 0) > 127
            class T_:
                def __init__(s, a): s.a = a
                def cpu(s): return s
                def numpy(s): return s.a
            out.append(types.SimpleNamespace(masks=types.SimpleNamespace(data=T_(gt[None].astype(np.float32))),
                                             boxes=types.SimpleNamespace(conf=T_(np.array([0.9], np.float32)))))
        return out


class LightingMainTest(unittest.TestCase):
    def test_runs_every_condition_and_writes_the_table_and_the_rule(self):
        world = make_world()
        out = world / "results.json"
        mask_dir = str(world / "dfuc" / "DFUC2022_train_masks")
        with mock.patch.object(LT, "DERIVED", world / "derived"), mock.patch.object(LT, "DFUC", world / "dfuc"), \
                mock.patch.object(LT, "YOLO", lambda w: PerfectModel(mask_dir)):
            text = run(LT, ["lighting_test.py", "--out", str(out), "--limit", "3"])
        self.assertIn("self-check", text)
        self.assertIn("original photos: mean Dice 1.0000", text)
        data = json.loads(out.read_text())
        expected = set(LT.BASE) | set(LT.SWEEP) | set(LT.CONTROL) | {"original", "rule"}
        self.assertEqual(set(data), expected)
        for c in list(LT.BASE) + list(LT.SWEEP) + list(LT.CONTROL):
            self.assertEqual(data[c]["raw"], 1.0)                            # the stand-in model is perfect in every light
            self.assertEqual(data[c]["corrected"], 1.0)
            self.assertEqual(data[c]["diff"], 0.0)
        self.assertEqual(data["identity"]["accepted"], 1.0)
        self.assertTrue(data["rule"]["verdict"].startswith("UNCLEAR"))       # nothing hurts a perfect model, so nothing counts
        self.assertTrue(data["rule"]["P"]["P1"])
        self.assertFalse(list(Path(tempfile.gettempdir()).glob("lighting_test_*")))   # the temporary photos were removed


class StabilityMainTest(unittest.TestCase):
    def test_writes_shifts_flip_rates_and_a_verdict_for_every_condition(self):
        world = make_world()
        out = world / "ita.json"
        with mock.patch.object(LT, "DERIVED", world / "derived"):
            text = run(T, ["ita_stability_test.py", "--out", str(out), "--limit", "3"])
        self.assertIn("identity light, raw class counts", text)
        data = json.loads(out.read_text())
        conditions = list(LT.BASE) + list(LT.SWEEP) + list(LT.CONTROL) + list(T.GREY_ARMS)
        for c in conditions:
            for key in ("raw_shift_median", "cor_shift_median", "raw_flip", "cor_flip", "flip_gain_ci95", "accepted"):
                self.assertIn(key, data[c], f"{c} {key}")
        self.assertEqual(data["identity"]["raw_shift_median"], 0.0)
        self.assertGreater(data["minus1"]["raw_shift_median"], data["minus1"]["cor_shift_median"])    # the grey patch removes the shift
        self.assertEqual(data["control_mismatch"]["raw_shift_median"], data["control_mismatch"]["cor_shift_median"])
        self.assertIn("verdict", data["rule"])
        self.assertEqual(data["n"], 3)


class NoClassMainTest(unittest.TestCase):
    def test_reports_the_share_of_photos_without_a_class(self):
        world = make_world()
        out = world / "nc.json"
        with mock.patch.object(LT, "DERIVED", world / "derived"):
            text = run(NC, ["ita_noclass_followup.py", "--out", str(out)])
        data = json.loads(out.read_text())
        self.assertEqual(data["identity_no_class"], {"raw": 0.0, "corrected": 0.0})
        self.assertIn("condition", text)
        for c in list(LT.BASE) + list(LT.SWEEP) + list(LT.CONTROL):
            self.assertIn(c, data)
            self.assertEqual(data[c]["n_valid_raw"] + round(data[c]["no_class_raw"] * 3), 3)   # every photo is either valid or counted as no class


if __name__ == "__main__":
    unittest.main()
