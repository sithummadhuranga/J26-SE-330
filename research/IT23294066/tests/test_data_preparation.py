import contextlib
import csv
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
import dfuc_dedup as DD
import dfuc_prepare as DP
import dfuc_split as DS


def run_main(module, argv):
    out = io.StringIO()
    with mock.patch.object(sys, "argv", argv), contextlib.redirect_stdout(out):
        module.main()
    return out.getvalue()


def shape_mask(h=48, w=64, boxes=((10, 10, 20, 20),)):
    m = np.zeros((h, w), np.uint8)
    for x0, y0, x1, y1 in boxes:
        m[y0:y1, x0:x1] = 255
    return m


class PrepareTest(unittest.TestCase):
    def test_masks_are_thresholded_at_127(self):
        d = tempfile.mkdtemp()
        p = os.path.join(d, "m.png")
        cv2.imwrite(p, np.array([[126, 127, 128, 255, 0]], np.uint8))
        self.assertEqual(DP.load_binary(p).tolist(), [[0, 0, 1, 1, 0]])        # strictly greater than 127
        self.assertEqual(DP.load_binary(p, threshold=200).tolist(), [[0, 0, 0, 1, 0]])
        self.assertEqual(DP.load_binary(p).dtype, np.uint8)

    def test_one_polygon_per_component_largest_first(self):
        m = (shape_mask(boxes=((2, 2, 8, 8), (20, 10, 50, 40))) > 0).astype(np.uint8)
        polys = DP.polygons(m, 0.5)
        self.assertEqual(len(polys), 2)
        self.assertGreater(cv2.contourArea(polys[0].astype(np.int32)), cv2.contourArea(polys[1].astype(np.int32)))

    def test_single_pixels_and_lines_are_not_polygons(self):
        m = np.zeros((20, 20), np.uint8)
        m[5, 5] = 1
        self.assertEqual(DP.polygons(m, 0.5), [])

    def test_simplification_removes_points_but_keeps_the_shape(self):
        m = np.zeros((200, 200), np.uint8)
        cv2.circle(m, (100, 100), 70, 1, -1)
        exact, simple = DP.polygons(m, 0)[0], DP.polygons(m, 1.5)[0]
        self.assertLess(len(simple), len(exact) / 3)
        self.assertGreater(DP.dice(m, DP.rasterise([simple], m.shape)), 0.99)

    def test_rasterise_round_trip_at_the_chosen_epsilon_is_nearly_exact(self):
        m = np.zeros((120, 160), np.uint8)
        cv2.ellipse(m, (80, 60), (50, 30), 20, 0, 360, 1, -1)
        self.assertGreater(DP.dice(m, DP.rasterise(DP.polygons(m, 0.5), m.shape)), 0.995)

    def test_dice_of_masks(self):
        a = np.zeros((10, 10), np.uint8)
        a[:5] = 1
        self.assertEqual(DP.dice(a, a), 1.0)
        self.assertEqual(DP.dice(np.zeros_like(a), np.zeros_like(a)), 1.0)
        b = np.zeros((10, 10), np.uint8)
        b[:, :5] = 1
        self.assertAlmostEqual(DP.dice(a, b), 0.5)

    def test_yolo_label_lines_are_normalised_with_class_0(self):
        lines = DP.label_lines([np.array([[10, 5], [30, 5], [30, 25]])], 40, 50)
        self.assertEqual(lines, ["0 0.250000 0.100000 0.750000 0.100000 0.750000 0.500000"])

    def make_dfuc(self):
        d = tempfile.mkdtemp()
        os.makedirs(os.path.join(d, "DFUC2022_train_images"))
        os.makedirs(os.path.join(d, "DFUC2022_train_masks"))
        specs = {"100001": ((10, 10, 30, 30),), "100002": ((5, 5, 20, 20), (40, 25, 60, 45)), "100003": ((30, 10, 50, 40),)}
        for i, boxes in specs.items():
            cv2.imwrite(os.path.join(d, "DFUC2022_train_images", i + ".jpg"), np.zeros((48, 64, 3), np.uint8))
            cv2.imwrite(os.path.join(d, "DFUC2022_train_masks", i + ".png"), shape_mask(boxes=boxes))
        return d

    def test_main_writes_one_label_file_per_image_and_a_report(self):
        d = self.make_dfuc()
        labels, report = os.path.join(d, "labels"), os.path.join(d, "report.csv")
        text = run_main(DP, ["dfuc_prepare.py", "--dfuc-train", d, "--labels-out", labels, "--report", report])
        self.assertIn("wrote 3 label files", text)
        self.assertIn("ulcers total 4", text)
        self.assertEqual(sorted(os.listdir(labels)), ["100001.txt", "100002.txt", "100003.txt"])
        with open(os.path.join(labels, "100002.txt")) as f:
            lines = [l for l in f.read().splitlines() if l]
        self.assertEqual(len(lines), 2)                                      # two ulcers, two polygons
        for l in lines:
            parts = l.split()
            self.assertEqual(parts[0], "0")
            self.assertTrue(all(0.0 <= float(v) <= 1.0 for v in parts[1:]))
        with open(report) as f:
            rows = list(csv.DictReader(f))
        self.assertEqual([r["id"] for r in rows], ["100001", "100002", "100003"])
        self.assertEqual(rows[1]["ulcers"], "2")
        self.assertEqual(rows[0]["mask_pixels"], "400")
        self.assertGreater(float(rows[0]["polygon_dice"]), 0.99)

    def test_sweep_only_reports(self):
        d = self.make_dfuc()
        text = run_main(DP, ["dfuc_prepare.py", "--dfuc-train", d, "--sweep"])
        self.assertIn("epsilon  mean_Dice", text)
        self.assertIn("threshold sensitivity", text)
        self.assertFalse(os.path.exists(os.path.join(d, "labels")))


class SplitTest(unittest.TestCase):
    def groups_and_strata(self):
        ids = [f"i{n:03d}" for n in range(200)]
        groups, k = [], 0
        for size in [2] * 10 + [3] * 4 + [1] * 168:                          # some duplicate groups, rest single images
            groups.append(ids[k:k + size])
            k += size
        strata = {i: (n % 4, n % 2) for n, i in enumerate(ids)}
        for g in groups:
            for i in g:
                strata[i] = strata[g[0]]                                     # a group lives in one stratum
        return ids, groups, strata

    def test_every_image_ends_up_on_exactly_one_side_and_groups_stay_together(self):
        ids, groups, strata = self.groups_and_strata()
        held, rest = DS.stratified_group_split(groups, strata, 0.2, 42)
        self.assertEqual(sorted(held + rest), sorted(ids))
        self.assertEqual(set(held) & set(rest), set())
        for g in groups:
            self.assertEqual(len({i in set(held) for i in g}), 1, f"group split: {g}")

    def test_about_the_asked_fraction_is_held_out_in_every_stratum(self):
        ids, groups, strata = self.groups_and_strata()
        held, rest = DS.stratified_group_split(groups, strata, 0.2, 42)
        self.assertAlmostEqual(len(held) / len(ids), 0.2, delta=0.03)
        for s in set(strata.values()):
            members = [i for i in ids if strata[i] == s]
            share = sum(i in set(held) for i in members) / len(members)
            self.assertAlmostEqual(share, 0.2, delta=0.12, msg=str(s))

    def test_same_seed_same_split_different_seed_different_split(self):
        ids, groups, strata = self.groups_and_strata()
        a = DS.stratified_group_split(groups, strata, 0.2, 1)
        self.assertEqual(a, DS.stratified_group_split(groups, strata, 0.2, 1))
        self.assertNotEqual(set(a[0]), set(DS.stratified_group_split(groups, strata, 0.2, 2)[0]))

    def make_dataset(self, n=80, mismatch=False):
        d = tempfile.mkdtemp()
        train = os.path.join(d, "train_release")
        labels = os.path.join(d, "labels")
        os.makedirs(os.path.join(train, "DFUC2022_train_images"))
        os.makedirs(labels)
        rng = np.random.default_rng(0)
        rows, ids = [], [f"{100000 + k}" for k in range(n)]
        for i in ids:
            cv2.imwrite(os.path.join(train, "DFUC2022_train_images", i + ".jpg"), np.zeros((8, 8, 3), np.uint8))
            with open(os.path.join(labels, i + ".txt"), "w") as f:
                f.write("0 0.1 0.1 0.2 0.1 0.2 0.2\n")
            rows.append((i, int(rng.choice([1, 1, 1, 2])), int(rng.integers(300, 20000))))
        report = os.path.join(d, "report.csv")
        with open(report, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["id", "ulcers", "mask_pixels", "polygon_dice"])
            w.writerows([(i, u, a, "1.0") for i, u, a in rows])
        pairs = [[ids[2 * k] + ".jpg", ids[2 * k + 1] + ".jpg"] for k in range(8)]       # 8 duplicate pairs
        singles = [[i + ".jpg"] for i in ids[16:]]
        if mismatch:
            singles = singles[:-1]
        clusters = os.path.join(d, "clusters.json")
        with open(clusters, "w") as f:
            json.dump({"clusters": pairs + singles}, f)
        return d, train, labels, report, clusters, ids, pairs

    @staticmethod
    def read(path):
        with open(path) as f:
            return f.read().split()

    def test_main_builds_the_yolo_folders_without_leaks(self):
        d, train, labels, report, clusters, ids, pairs = self.make_dataset()
        out = os.path.join(d, "out")
        text = run_main(DS, ["dfuc_split.py", "--dfuc-train", train, "--labels", labels, "--clusters", clusters, "--report", report, "--out", out])
        self.assertIn("wound-size quartile edges", text)
        sets = {s: set(self.read(os.path.join(out, f"{s}.txt"))) for s in ("train", "val", "test")}
        self.assertEqual(sum(len(v) for v in sets.values()), 80)
        self.assertEqual(sets["train"] | sets["val"] | sets["test"], set(ids))
        self.assertAlmostEqual(len(sets["test"]) / 80, 0.2, delta=0.06)
        self.assertGreater(len(sets["val"]), 0)
        for a, b in pairs:                                                   # duplicates never on two sides
            a, b = a[:-4], b[:-4]
            self.assertEqual([a in sets[s] for s in sets], [b in sets[s] for s in sets])
        for s in sets:                                                       # symlinks for images and labels, pointing at real files
            for i in list(sets[s])[:3]:
                for sub, ext in (("images", ".jpg"), ("labels", ".txt")):
                    link = os.path.join(out, sub, s, i + ext)
                    self.assertTrue(os.path.islink(link))
                    self.assertTrue(os.path.exists(link))
        with open(os.path.join(out, "data.yaml")) as f:
            yaml = f.read()
        for line in ("train: images/train", "val: images/val", "test: images/test", "0: ulcer"):
            self.assertIn(line, yaml)

    def test_a_cluster_file_that_does_not_match_the_report_is_refused(self):
        d, train, labels, report, clusters, ids, pairs = self.make_dataset(mismatch=True)
        with self.assertRaises(AssertionError):
            run_main(DS, ["dfuc_split.py", "--dfuc-train", train, "--labels", labels, "--clusters", clusters, "--report", report, "--out", os.path.join(d, "o")])


def textured(seed, h=240, w=320):
    rng = np.random.default_rng(seed)
    img = cv2.GaussianBlur(rng.integers(0, 255, (h, w, 3), dtype=np.uint8), (0, 0), 2)
    for _ in range(25):
        cv2.circle(img, (int(rng.integers(0, w)), int(rng.integers(0, h))), int(rng.integers(5, 25)), [int(v) for v in rng.integers(0, 255, 3)], -1)
    return img


def second_view(img):
    h, w = img.shape[:2]
    M = cv2.getRotationMatrix2D((w / 2, h / 2), 4, 1.05)
    return cv2.convertScaleAbs(cv2.warpAffine(img, M, (w, h)), alpha=0.9, beta=8)


class DedupTest(unittest.TestCase):
    def features(self, img):
        kps, des = cv2.SIFT_create(nfeatures=800).detectAndCompute(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), None)
        return [kp.pt for kp in kps], des

    def test_descriptors_are_unit_length_and_identical_for_identical_images(self):
        a = textured(1)
        g1, h1 = DD.global_descriptor(a)
        g2, h2 = DD.global_descriptor(a.copy())
        self.assertAlmostEqual(float(np.linalg.norm(g1)), 1.0, places=5)
        self.assertAlmostEqual(float(np.linalg.norm(h1)), 1.0, places=4)     # normalised with a tiny epsilon
        self.assertTrue(np.array_equal(g1, g2) and np.array_equal(h1, h2))
        self.assertLess(float(g1 @ DD.global_descriptor(textured(2))[0]), 0.9)

    def test_a_rotated_rescaled_copy_has_many_inliers_an_unrelated_photo_has_few(self):
        a = textured(1)
        matcher = cv2.BFMatcher()
        same = DD.sift_inliers(self.features(a), self.features(second_view(a)), matcher)
        other = DD.sift_inliers(self.features(a), self.features(textured(2)), matcher)
        self.assertGreater(same, 15)                                         # measured on these pictures: 18-27 against 0-4
        self.assertLess(other, 8)
        self.assertGreater(same, 3 * other)

    def test_too_few_keypoints_give_zero_inliers(self):
        flat = np.full((100, 100, 3), 128, np.uint8)
        self.assertEqual(DD.sift_inliers(self.features(flat), self.features(textured(1)), cv2.BFMatcher()), 0)

    def test_main_groups_the_copies_and_leaves_the_rest_alone(self):
        d = tempfile.mkdtemp()
        imgs = os.path.join(d, "imgs")
        os.makedirs(imgs)
        base = {n: textured(10 + n) for n in range(10)}
        for n, img in base.items():
            cv2.imwrite(os.path.join(imgs, f"{100000 + n}.jpg"), img)
        for n in (0, 5):                                                     # photos 0 and 5 get a second view under new ids
            cv2.imwrite(os.path.join(imgs, f"{100100 + n}.jpg"), second_view(base[n]))
        out = os.path.join(d, "clusters.json")
        run_main(DD, ["dfuc_dedup.py", "--images", imgs, "--out", out, "--neighbours", "3", "--negatives", "10"])
        with open(out) as f:
            data = json.load(f)
        groups = [sorted(c) for c in data["clusters"] if len(c) > 1]
        self.assertEqual(groups, [["100000.jpg", "100100.jpg"], ["100005.jpg", "100105.jpg"]])
        self.assertEqual(sum(len(c) for c in data["clusters"]), 12)
        self.assertGreater(data["threshold_inliers"], 5)


if __name__ == "__main__":
    unittest.main()
