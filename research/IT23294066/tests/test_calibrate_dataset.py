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

import common  # noqa: F401
import calibrate_dataset as CD
import ita_noclass_followup as NC


def make_dataset(per_split=2):
    d = tempfile.mkdtemp()
    rng = np.random.default_rng(0)
    for split in ("train", "val", "test"):
        os.makedirs(os.path.join(d, "images", split))
        os.makedirs(os.path.join(d, "labels", split))
        for k in range(per_split):
            img = np.clip(rng.normal([190, 140, 115], 6, (48, 64, 3)), 0, 255).astype(np.uint8)
            cv2.imwrite(os.path.join(d, "images", split, f"{split}{k}.jpg"), cv2.cvtColor(img, cv2.COLOR_RGB2BGR))
            with open(os.path.join(d, "labels", split, f"{split}{k}.txt"), "w") as f:
                f.write("0 0.1 0.1 0.2 0.1 0.2 0.2\n")
    return d


def run(variant, dataset, out):
    buf = io.StringIO()
    with mock.patch.object(sys, "argv", ["calibrate_dataset.py", "--dataset", dataset, "--variant", variant, "--out", out]), \
            contextlib.redirect_stdout(buf):
        CD.main()
    return buf.getvalue()


class VariantsTest(unittest.TestCase):
    def test_our_own_variant_is_added_to_the_colour_components_list(self):
        for k in ("none", "gray_world", "full", "full_ccm_v1"):
            self.assertIn(k, CD.VARIANTS)
        c = CD.VARIANTS["ccm_only"]
        self.assertFalse(c.stretch)
        self.assertEqual(c.illumination, "none")
        self.assertTrue(c.use_ccm)
        self.assertFalse(c.use_specular)
        self.assertFalse(c.use_clahe)


class MainTest(unittest.TestCase):
    def test_writes_calibrated_png_copies_with_the_same_names_and_size(self):
        d, out = make_dataset(), os.path.join(tempfile.mkdtemp(), "out")
        text = run("ccm_only", d, out)
        self.assertIn("train", text)
        for split in ("train", "val", "test"):
            self.assertEqual(sorted(os.listdir(os.path.join(out, "images", split))), [f"{split}0.png", f"{split}1.png"])
            img = cv2.imread(os.path.join(out, "images", split, f"{split}0.png"))
            self.assertEqual(img.shape, (48, 64, 3))

    def test_labels_are_linked_not_copied_and_still_valid(self):
        d, out = make_dataset(), os.path.join(tempfile.mkdtemp(), "out")
        run("none", d, out)
        link = os.path.join(out, "labels", "train", "train0.txt")
        self.assertTrue(os.path.islink(link))
        with open(link) as f:
            self.assertEqual(f.read(), "0 0.1 0.1 0.2 0.1 0.2 0.2\n")

    def test_the_variant_none_leaves_the_pictures_unchanged(self):
        d, out = make_dataset(), os.path.join(tempfile.mkdtemp(), "out")
        run("none", d, out)
        before = cv2.imread(os.path.join(d, "images", "train", "train0.jpg"))
        after = cv2.imread(os.path.join(out, "images", "train", "train0.png"))
        self.assertLessEqual(int(np.abs(before.astype(int) - after.astype(int)).max()), 3)   # only the colour handling may differ slightly

    def test_the_manifest_records_the_variant_the_settings_and_the_old_skin_mask_limit(self):
        d, out = make_dataset(), os.path.join(tempfile.mkdtemp(), "out")
        run("ccm_only", d, out)
        with open(os.path.join(out, "manifest.json")) as f:
            m = json.load(f)
        self.assertEqual(m["variant"], "ccm_only")
        self.assertEqual(m["config"]["skin_v_min"], 35.0)                    # pinned so the study datasets stay reproducible
        self.assertEqual(m["config"]["use_ccm"], True)
        self.assertEqual(sorted(m["splits"]), ["test", "train", "val"])
        self.assertEqual(m["splits"]["train"]["images"], 2)
        self.assertIsInstance(m["calibration_commit"], str)

    def test_data_yaml_points_at_the_three_splits(self):
        d, out = make_dataset(), os.path.join(tempfile.mkdtemp(), "out")
        run("none", d, out)
        with open(os.path.join(out, "data.yaml")) as f:
            yaml = f.read()
        for line in ("train: images/train", "val: images/val", "test: images/test", "0: ulcer"):
            self.assertIn(line, yaml)

    def test_an_unknown_variant_is_refused(self):
        with self.assertRaises(SystemExit):
            with contextlib.redirect_stderr(io.StringIO()):
                run("nonsense", make_dataset(), tempfile.mkdtemp())


class FollowupTest(unittest.TestCase):
    def test_the_followup_script_loads_and_has_a_main(self):
        self.assertTrue(callable(NC.main))


if __name__ == "__main__":
    unittest.main()
