import json
import unittest
from pathlib import Path

import numpy as np

import common  # noqa: F401
import ita_stability_test as T
import lighting_test as LT

SAVED = Path("/Users/sithummadhuranga/Desktop/Research/Data set/derived/lighting_test/ita_results.json")


def res(raw_shift=40.0, cor_shift=1.0, raw_flip=0.8, cor_flip=0.02, ci_lo=0.5, accepted=1.0):
    return dict(raw_shift_median=raw_shift, cor_shift_median=cor_shift, raw_flip=raw_flip, cor_flip=cor_flip,
                flip_gain_ci95=[ci_lo, 0.9], accepted=accepted)


def table(**over):
    r = {c: res() for c in list(LT.BASE) + list(T.GREY_ARMS)}
    r["identity"] = res(0.0, 0.0, 0.0, 0.0, 0.0)
    r.update(over)
    return r


class RuleTest(unittest.TestCase):
    def test_all_judged_conditions_stable_is_a_pass(self):
        out = T.evaluate_rule(table(), 0.0, 0.0)
        self.assertEqual(out["verdict"], "PASS")
        self.assertTrue(all(out["counted"].values()))

    def test_a_condition_that_hardly_moves_the_raw_reading_does_not_count(self):
        r = table(warm=res(raw_shift=2.0, raw_flip=0.05))
        out = T.evaluate_rule(r, 0.0, 0.0)
        self.assertFalse(out["counted"]["warm"])
        self.assertEqual(out["verdict"], "PASS")                            # the other three still decide

    def test_nothing_counted_is_unclear(self):
        r = table()
        for c in T.L.JUDGED:
            r[c] = res(raw_shift=1.0, raw_flip=0.0)
        self.assertTrue(T.evaluate_rule(r, 0.0, 0.0)["verdict"].startswith("UNCLEAR"))

    def test_identity_check_comes_first(self):
        r = table()
        for c in T.L.JUDGED:
            r[c] = res(raw_shift=1.0, raw_flip=0.0)
        self.assertTrue(T.evaluate_rule(r, 3.0, 0.0)["verdict"].startswith("FAIL"))   # S1 fails: median difference 3 deg > 1
        self.assertTrue(T.evaluate_rule(r, 0.0, 0.05)["verdict"].startswith("FAIL"))  # S1 fails: 5% class change > 2%

    def test_s2_needs_at_least_a_halving_in_both_shift_and_class_change_and_a_positive_interval(self):
        self.assertEqual(T.evaluate_rule(table(cool=res(cor_shift=25.0)), 0.0, 0.0)["verdict"], "UNCLEAR")   # shift not halved
        self.assertEqual(T.evaluate_rule(table(cool=res(cor_flip=0.5)), 0.0, 0.0)["verdict"], "UNCLEAR")     # flips not halved
        self.assertEqual(T.evaluate_rule(table(cool=res(ci_lo=-0.1)), 0.0, 0.0)["verdict"], "UNCLEAR")       # interval includes 0

    def test_two_failed_s2_conditions_fail(self):
        r = table(cool=res(cor_shift=25.0), warm=res(cor_shift=25.0))
        self.assertEqual(T.evaluate_rule(r, 0.0, 0.0)["verdict"], "FAIL")

    def test_s3_corrected_median_shift_above_3_degrees_blocks_a_pass(self):
        r = table(cool=res(raw_shift=60.0, cor_shift=3.5))                  # halved, but still above 3 degrees
        self.assertEqual(T.evaluate_rule(r, 0.0, 0.0)["verdict"], "UNCLEAR")

    def test_s4_acceptance(self):
        self.assertEqual(T.evaluate_rule(table(minus1=res(accepted=0.9)), 0.0, 0.0)["verdict"], "UNCLEAR")
        self.assertEqual(T.evaluate_rule(table(minus2=res(accepted=0.1)), 0.0, 0.0)["verdict"], "PASS")

    @unittest.skipUnless(SAVED.exists(), "saved skin-type results are not on this machine")
    def test_the_saved_real_run_gives_the_recorded_verdict(self):
        saved = json.loads(SAVED.read_text())
        out = T.evaluate_rule(saved, saved["rule"]["P"]["S1_values"]["median_difference"], saved["rule"]["P"]["S1_values"]["class_change"])
        self.assertEqual(out["verdict"], "PASS")                            # recorded on 2026-10-19


class ReadingTest(unittest.TestCase):
    def test_a_photo_without_skin_has_no_class(self):
        ita, cls = T.read(np.zeros((60, 80, 3), np.uint8))
        self.assertTrue(np.isnan(ita))
        self.assertIsNone(cls)

    def test_a_skin_coloured_photo_has_a_class_and_a_darker_one_has_a_lower_angle(self):
        light = np.full((100, 100, 3), (224, 172, 150), np.uint8)
        dark = np.full((100, 100, 3), (112, 72, 52), np.uint8)
        (ita_l, cls_l), (ita_d, cls_d) = T.read(light), T.read(dark)
        self.assertIn(cls_l, T.ORDER)
        self.assertGreater(ita_l, ita_d)
        self.assertGreater(T.ORDER.index(cls_d), T.ORDER.index(cls_l))

    def test_flip_rate_bootstrap_is_positive_when_the_first_flips_much_more(self):
        a = np.array([True] * 80 + [False] * 20)
        b = np.array([False] * 95 + [True] * 5)
        lo, hi = T.rate_boot(a, b, n=800)
        self.assertGreater(lo, 0.5)
        self.assertGreater(hi, lo)

    def test_grey_error_arms_change_only_the_grey(self):
        self.assertEqual(sorted(T.GREY_ARMS), ["grey_+3", "grey_+6", "grey_-3", "grey_-6"])
        self.assertEqual(T.GREY_ARMS["grey_-6"]["labs"]["grey"], (44, 0, 0))
        self.assertEqual(T.GREY_ARMS["grey_-6"]["labs"]["skin_a"], LT.LABS["skin_a"])


if __name__ == "__main__":
    unittest.main()
