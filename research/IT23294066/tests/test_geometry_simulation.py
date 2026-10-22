import unittest

import numpy as np

import common  # noqa: F401
import geometry_simulation as G


class GeometryTest(unittest.TestCase):
    def test_a_flat_plane_maps_its_origin_to_the_image_centre_with_one_scale(self):
        px = G.project(np.array([[0.0, 0.0], [10.0, 0.0], [0.0, 10.0]]), 0.0)
        self.assertTrue(np.allclose(px[0], [G.W / 2, G.H / 2]))
        scale = G.F / G.Z
        self.assertAlmostEqual(px[1][0] - px[0][0], 10 * scale, places=6)
        self.assertAlmostEqual(px[2][1] - px[0][1], 10 * scale, places=6)

    def test_tilt_makes_the_far_side_smaller(self):
        px = G.project(np.array([[-50.0, 0.0], [50.0, 0.0]]), 40.0)
        near_far = abs(G.project(np.array([[40.0, 0.0], [50.0, 0.0]]), 40.0)[1, 0] - G.project(np.array([[40.0, 0.0]]), 40.0)[0, 0])
        far_near = abs(G.project(np.array([[-50.0, 0.0], [-40.0, 0.0]]), 40.0)[1, 0] - G.project(np.array([[-50.0, 0.0]]), 40.0)[0, 0])
        self.assertNotAlmostEqual(near_far, far_near, places=2)

    def test_shoelace_area(self):
        self.assertAlmostEqual(G.shoelace(np.array([[0, 0], [1, 0], [1, 1], [0, 1]], float)), 1.0)
        self.assertAlmostEqual(G.shoelace(np.array([[0, 0], [4, 0], [0, 3]], float)), 6.0)

    def test_homography_recovers_a_known_projective_map(self):
        H = np.array([[1.2, 0.1, 5.0], [-0.05, 0.9, -3.0], [0.0005, 0.0003, 1.0]])
        src = np.array([[0, 0], [50, 0], [50, 50], [0, 50]], float)
        dst = G.through(H, src)
        est = G.homography(src, dst)
        probe = np.array([[10, 20], [33, 7], [49, 49]], float)
        self.assertTrue(np.allclose(G.through(est, probe), G.through(H, probe), atol=1e-6))

    def test_homography_area_is_exact_without_corner_noise_at_any_tilt(self):
        rng = np.random.default_rng(0)
        for tilt in (0, 20, 40):
            err = G.trial(tilt, 30.0, 50.0, 0.0, rng)["homography"]
            self.assertLess(err, 0.01, f"tilt {tilt}")  # the 360-sided polygon stands in for a circle: 0.005% by construction

    def test_a_single_scale_is_fine_when_the_phone_is_square_and_wrong_when_tilted(self):
        rng = np.random.default_rng(0)
        self.assertLess(G.trial(0, 30.0, 50.0, 0.0, rng)["side length"], 1.0)
        self.assertGreater(G.trial(40, 30.0, 50.0, 0.0, rng)["side length"], 5.0)

    def test_corner_noise_hurts_the_homography_more_for_a_small_marker(self):
        rng = np.random.default_rng(1)
        small = G.median_error("homography", 20, 20.0, 50.0, 0.5, rng, n=300)
        big = G.median_error("homography", 20, 50.0, 50.0, 0.5, rng, n=300)
        self.assertGreater(small, big)

    def test_the_error_of_a_single_scale_grows_with_tilt(self):
        rng = np.random.default_rng(2)
        errs = [G.median_error("side length", t, 30.0, 50.0, 0.1, rng, n=100) for t in (0, 10, 20, 30)]
        self.assertEqual(errs, sorted(errs))


if __name__ == "__main__":
    unittest.main()
