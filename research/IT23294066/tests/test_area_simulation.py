import math
import unittest

import numpy as np

import common  # noqa: F401
import area_simulation as S
import make_sticker_sheet as MS


class RenderBlurTest(unittest.TestCase):
    def test_blur_mixes_light_not_display_values(self):
        """A lens blurs light: the middle of a black-to-white edge is half the light, which is 188 in sRGB, not 128."""
        sheet = np.zeros((2400, 2000, 3), np.uint8)
        edge = int(S.P0[0] * S.SMALL_PPM)                                      # the edge runs through the middle of the photo
        sheet[:, edge:] = 255
        Hmm = S.pose_homography(0, 0, S.SMALL_PPM)
        img = S.render(sheet, Hmm, 1.0, (1, 1, 1), 3.0, 0.0, np.random.default_rng(0))
        self.assertAlmostEqual(float(img[S.H // 2, S.W // 2 - 1:S.W // 2 + 1, 1].mean()), 188, delta=8)   # the pixels either side of the edge


class SimulationHelpersTest(unittest.TestCase):
    def test_sticker_origins_match_the_print_sheet(self):
        _, info = MS.build_sheet()
        self.assertEqual(S.STICKER_ORIGINS, info["origins"])

    def test_truth_areas_come_from_connected_regions(self):
        big, info = MS.build_sheet()
        truth = S.truth_areas(np.array(big))
        for k in ("rectangle", "circle", "triangle"):
            self.assertAlmostEqual(truth[k], info["areas"][k], delta=0.05)
        self.assertAlmostEqual(truth["triangle"], 602.5, delta=1.0)           # the earlier window-based count gave 618

    def test_straight_pose_puts_the_look_at_point_in_the_centre_with_the_asked_scale(self):
        Hmm = S.pose_homography(0, 0, 10.0)
        c = S.project(Hmm, [list(S.P0)])[0]
        self.assertAlmostEqual(c[0], S.W / 2, delta=0.01)
        self.assertAlmostEqual(c[1], S.H / 2, delta=0.01)
        self.assertAlmostEqual(S.local_ppm(Hmm, *S.P0), 10.0, delta=0.01)

    def test_in_plane_rotation_turns_the_picture(self):
        Hmm = S.pose_homography(0, 90, 10.0)
        a, b = S.project(Hmm, [[S.P0[0], S.P0[1]], [S.P0[0] + 10, S.P0[1]]])
        v = b - a                                                            # +x on the sheet should now point along image +y
        self.assertAlmostEqual(v[0], 0.0, delta=0.5)
        self.assertGreater(v[1], 90.0)

    def test_tilt_shortens_the_tilted_direction_by_cosine_at_the_look_at_point(self):
        Hmm = S.pose_homography(40, 0, 10.0)
        a, b = S.project(Hmm, [[S.P0[0], S.P0[1]], [S.P0[0], S.P0[1] + 0.01]])  # along the tilted (y) axis, tiny step: a derivative
        c, d = S.project(Hmm, [[S.P0[0], S.P0[1]], [S.P0[0] + 0.01, S.P0[1]]])  # along the untilted (x) axis
        self.assertAlmostEqual(np.linalg.norm(b - a) / np.linalg.norm(d - c), math.cos(math.radians(40)), delta=0.01)

    def test_red_mask_picks_red_and_nothing_else(self):
        img = np.zeros((4, 1, 3), np.uint8)
        img[0] = (200, 40, 40); img[1] = (0, 0, 0); img[2] = (255, 255, 255); img[3] = (40, 200, 40)
        self.assertEqual(S.red_mask(img).ravel().tolist(), [1, 0, 0, 0])

    def test_render_has_the_camera_size_and_a_background(self):
        small = np.full((300, 200, 3), 255, np.uint8)
        img = S.render(small, S.pose_homography(0, 0, 10.0), 1.0, (1, 1, 1), 0.5, 0.0, np.random.default_rng(0))
        self.assertEqual(img.shape, (S.H, S.W, 3))
        self.assertLess(np.abs(img[5, 5].astype(int) - np.array(S.BG)).max(), 8)  # outside the sheet: the table colour

    def test_exposure_gain_changes_brightness_in_linear_light(self):
        small = np.full((3000, 3000, 3), 128, np.uint8)                       # a big grey sheet covering the camera view
        Hmm = S.pose_homography(0, 0, 10.0)
        dim = S.render(small, Hmm, 0.5, (1, 1, 1), 0.0, 0.0, np.random.default_rng(0))
        norm = S.render(small, Hmm, 1.0, (1, 1, 1), 0.0, 0.0, np.random.default_rng(0))
        lin = lambda v: ((v / 255 + 0.055) / 1.055) ** 2.4
        sel = (slice(1400, 1600), slice(1900, 2100))
        a, b = lin(float(norm[sel][..., 0].mean())), lin(float(dim[sel][..., 0].mean()))
        self.assertGreater(a, 0.1)
        self.assertAlmostEqual(b / a, 0.5, delta=0.05)


if __name__ == "__main__":
    unittest.main()
