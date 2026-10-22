import json
import unittest
from pathlib import Path

import numpy as np

import common  # noqa: F401
import lighting_test as LT
from colorimetric import marker as M

O = 0.7359
SAVED = Path("/Users/sithummadhuranga/Desktop/Research/Data set/derived/lighting_test/results.json")


def results(**over):
    """Result table of the lighting test where nothing changes; override single conditions."""
    r = {c: dict(raw=O, corrected=O, diff=0.0, diff_ci95=[0.0, 0.0], accepted=1.0) for c in LT.BASE}
    for k, v in over.items():
        r[k] = {**r[k], **v}
    return r


def hurt(c, raw_drop=0.05, gain=0.04, lo=0.01, corrected_drop=0.01):
    return {c: dict(raw=O - raw_drop, corrected=O - corrected_drop, diff=gain, diff_ci95=[lo, gain + 0.02], accepted=1.0)}


class RuleTest(unittest.TestCase):
    def test_nothing_counted_is_unclear_not_a_pass(self):
        out = LT.evaluate_rule(results(), O)
        self.assertTrue(out["P"]["P1"])
        self.assertEqual(out["verdict"].split(" ")[0], "UNCLEAR")

    def test_a_failing_identity_check_is_reported_even_when_nothing_counted(self):
        out = LT.evaluate_rule(results(identity=dict(corrected=O + 0.01)), O)
        self.assertFalse(out["P"]["P1"])
        self.assertTrue(out["verdict"].startswith("FAIL"))

    def test_identity_acceptance_below_99_percent_fails_p1(self):
        out = LT.evaluate_rule(results(identity=dict(accepted=0.98)), O)
        self.assertTrue(out["verdict"].startswith("FAIL"))

    def test_all_four_judged_conditions_counted_and_recovered_is_a_pass(self):
        r = results()
        for c in LT.JUDGED:
            r.update(hurt(c))
        out = LT.evaluate_rule(r, O)
        self.assertEqual(out["verdict"], "PASS")
        self.assertTrue(all(out["counted"].values()))

    def test_a_condition_counts_from_exactly_0_03_below_the_original(self):
        r = results(**hurt("warm", raw_drop=0.03))
        self.assertTrue(LT.evaluate_rule(r, O)["counted"]["warm"])
        r = results(**hurt("warm", raw_drop=0.0299))
        self.assertFalse(LT.evaluate_rule(r, O)["counted"]["warm"])

    def test_two_counted_conditions_without_a_clear_gain_fail_one_is_unclear(self):
        r = results()
        for c in LT.JUDGED:
            r.update(hurt(c))
        r["warm"] = dict(raw=O - 0.05, corrected=O - 0.04, diff=0.01, diff_ci95=[-0.01, 0.03], accepted=1.0)   # gain too small
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "UNCLEAR")
        r["cool"] = dict(raw=O - 0.05, corrected=O - 0.04, diff=0.01, diff_ci95=[-0.01, 0.03], accepted=1.0)
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "FAIL")

    def test_p3_one_sided_recovery_corrected_much_better_than_original_is_fine(self):
        r = results()
        for c in LT.JUDGED:
            r.update(hurt(c, corrected_drop=-0.03))                          # corrected is 0.03 ABOVE the original
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "PASS")
        r.update(hurt("cool", corrected_drop=0.03))                          # corrected 0.03 below the original
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "UNCLEAR")

    def test_p4_acceptance_below_95_percent_blocks_a_pass_except_at_minus2(self):
        r = results()
        for c in LT.JUDGED:
            r.update(hurt(c))
        r["minus2"]["accepted"] = 0.2
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "PASS")
        r["plus1"]["accepted"] = 0.9
        self.assertEqual(LT.evaluate_rule(r, O)["verdict"], "UNCLEAR")

    @unittest.skipUnless(SAVED.exists(), "saved lighting-test results are not on this machine")
    def test_the_saved_real_run_gives_the_recorded_verdict(self):
        saved = json.loads(SAVED.read_text())
        out = LT.evaluate_rule(saved, saved["original"]["mean_dice"])
        self.assertTrue(out["P"]["P1"])
        self.assertTrue(out["verdict"].startswith("UNCLEAR"))               # recorded on 2026-10-19


class ConditionTest(unittest.TestCase):
    def test_identity_changes_nothing(self):
        rgb = np.random.default_rng(0).integers(0, 256, (20, 20, 3), dtype=np.uint8)
        self.assertTrue(np.array_equal(LT.change_photo(rgb, dict(gain=(1, 1, 1))), rgb))

    def test_one_stop_darker_halves_linear_light(self):
        out = LT.camera(np.array([200.0, 200.0, 200.0]), (0.5, 0.5, 0.5))
        lin = lambda v: float(M._srgb_to_linear(np.asarray(v) / 255.0))
        self.assertAlmostEqual(lin(out[0]) / lin(200.0), 0.5, places=3)

    def test_gain_above_one_clips_at_white(self):
        self.assertEqual(LT.change_photo(np.full((2, 2, 3), 250, np.uint8), dict(gain=(4, 4, 4))).min(), 255)

    def test_tone_curve_and_mixing_keep_black_and_white_ends_sensible(self):
        black = LT.camera(np.zeros(3), (1, 1, 1), curve=1.25, mix=0.1)
        white = LT.camera(np.full(3, 255.0), (1, 1, 1), curve=0.8, mix=0.1)
        self.assertTrue(np.allclose(black, 0.0, atol=1e-6))
        self.assertTrue(np.allclose(white, 255.0, atol=0.5))

    def test_mixing_matrix_rows_sum_to_one_so_grey_stays_grey(self):
        m = LT.mixing_matrix(0.1)
        self.assertTrue(np.allclose(m.sum(axis=1), 1.0))
        grey = LT.camera(np.full(3, 128.0), (1, 1, 1), mix=0.1)
        self.assertTrue(np.allclose(grey, grey[0], atol=1e-6))

    def test_synthetic_sticker_gives_the_expected_gains(self):
        for cond, expected in ((dict(gain=(1, 1, 1)), 1.0), (dict(gain=(0.5, 0.5, 0.5)), 2.0)):
            res = M.estimate_gains(LT.sticker(cond, 0))
            self.assertTrue(res.used, res.reason)
            self.assertTrue(np.allclose(res.gains, expected, rtol=0.02), res.gains)

    def test_sticker_noise_is_only_on_the_patches(self):
        a, b = LT.sticker(dict(gain=(1, 1, 1)), 0), LT.sticker(dict(gain=(1, 1, 1)), 1)
        self.assertTrue(np.array_equal(a[:40], b[:40]))                      # frame and white: identical for any seed
        self.assertFalse(np.array_equal(a[200:300, 100:400], b[200:300, 100:400]))  # patches differ

    def test_a_wrong_printed_grey_moves_the_gains(self):
        right = M.estimate_gains(LT.sticker(dict(gain=(1, 1, 1)), 0)).gains
        darker = M.estimate_gains(LT.sticker(dict(gain=(1, 1, 1), labs={**LT.LABS, "grey": (44, 0, 0)}), 0)).gains
        self.assertTrue((darker > right).all())                              # darker printed grey -> brighten the photo

    def test_bootstrap_interval_contains_the_mean_and_is_ordered(self):
        d = np.random.default_rng(0).normal(0.02, 0.05, 300)
        lo, hi = LT.boot(d, n=800)
        self.assertLess(lo, d.mean())
        self.assertGreater(hi, d.mean())
        self.assertLess(lo, hi)


if __name__ == "__main__":
    unittest.main()
