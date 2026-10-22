import unittest

import cv2
import numpy as np

import common  # noqa: F401
import area_simulation as S
import make_sticker_sheet as MS
import sticker_finder as SF

_sheet = None


def small_sheet():
    """The print sheet at 15 pixels per mm (built once; it is large)."""
    global _sheet
    if _sheet is None:
        big, info = MS.build_sheet()
        q = S.SMALL_PPM / MS.PX_PER_MM
        _sheet = (cv2.resize(np.array(big), None, fx=q, fy=q, interpolation=cv2.INTER_AREA), info)
    return _sheet


def photo(tilt=0.0, rot=0.0, ppm=10.0, gain=1.0, blur=0.8, noise=2.0, seed=1):
    sheet, _ = small_sheet()
    Hmm = S.pose_homography(tilt, rot, ppm)
    return S.render(sheet, Hmm, gain, (1, 1, 1), blur, noise, np.random.default_rng(seed)), Hmm


def true_top_left(Hmm, centre_px):
    """Photo position of the printed top-left corner of the sticker whose centre is nearest to centre_px."""
    best = min(S.STICKER_ORIGINS, key=lambda o: np.linalg.norm(S.project(Hmm, [[o[0] + 25, o[1] + 25]])[0] - centre_px))
    return S.project(Hmm, [[best[0], best[1]]])[0], best


class FinderTest(unittest.TestCase):
    def test_finds_all_six_stickers_on_a_flat_photo_with_the_right_scale(self):
        img, Hmm = photo()
        found = SF.find_stickers(img)
        self.assertEqual(len(found), 6)
        for s in found:
            self.assertAlmostEqual(s.px_per_mm, 10.0, delta=0.15)          # within 1.5%
            self.assertEqual(s.crop.shape, (500, 500, 3))
            self.assertGreater(s.orientation_contrast, 0.7)
            self.assertGreater(s.frame_contrast, 0.7)

    def test_corner_order_follows_the_print_for_every_in_plane_rotation(self):
        for rot in (0, 90, 180, 270, 33):
            img, Hmm = photo(rot=rot)
            found = SF.find_stickers(img)
            self.assertEqual(len(found), 6, f"rotation {rot}")
            for s in found:
                tl_true, _ = true_top_left(Hmm, s.corners.mean(axis=0))
                tl_h = cv2.perspectiveTransform(np.array([[[0.0, 0.0]]]), s.h_mm_to_px)[0, 0]
                self.assertLess(np.linalg.norm(tl_h - tl_true), 3.0, f"rotation {rot}: printed top-left is at the wrong corner")

    def test_homography_and_scale_hold_under_tilt(self):
        img, Hmm = photo(tilt=30)
        found = SF.find_stickers(img)
        self.assertEqual(len(found), 6)
        ppms = sorted(s.px_per_mm for s in found)
        self.assertGreater(ppms[-1] / ppms[0], 1.15)                       # near and far stickers really differ
        for s in found:
            _, (ox, oy) = true_top_left(Hmm, s.corners.mean(axis=0))
            true_ppm = S.local_ppm(Hmm, ox + 25, oy + 25)
            self.assertAlmostEqual(s.px_per_mm / true_ppm, 1.0, delta=0.015)
            true_corners = S.project(Hmm, [[ox, oy], [ox + 50, oy], [ox + 50, oy + 50], [ox, oy + 50]])
            self.assertLess(np.linalg.norm(s.corners - true_corners, axis=1).mean(), 4.0)

    def test_dim_and_bright_photos_still_find_the_stickers(self):
        for gain in (0.4, 1.8):
            img, _ = photo(gain=gain)
            self.assertEqual(len(SF.find_stickers(img)), 6, f"exposure x{gain}")

    def test_the_straightened_picture_matches_the_printed_sticker(self):
        img, Hmm = photo()
        sheet, _ = small_sheet()
        for s in SF.find_stickers(img)[:3]:
            _, (ox, oy) = true_top_left(Hmm, s.corners.mean(axis=0))
            x0, y0 = int(ox * S.SMALL_PPM), int(oy * S.SMALL_PPM)
            ideal = cv2.resize(sheet[y0:y0 + 750, x0:x0 + 750], (500, 500), interpolation=cv2.INTER_AREA)
            self.assertLess(np.abs(s.crop.astype(float) - ideal.astype(float)).mean(), 12.0)

    def test_a_different_real_side_scales_the_result(self):
        img, _ = photo()
        a = SF.find_stickers(img, side_mm=50.0)[0]
        b = SF.find_stickers(img, side_mm=49.0)[0]
        self.assertAlmostEqual(b.px_per_mm / a.px_per_mm, 50.0 / 49.0, delta=0.003)

    def test_things_that_are_not_stickers_are_rejected(self):
        rng = np.random.default_rng(0)
        skin = np.clip(rng.normal([180, 130, 110], 6, (1200, 1600, 3)), 0, 255).astype(np.uint8)
        self.assertEqual(SF.find_stickers(skin), [])
        black_square = skin.copy()
        black_square[400:800, 500:900] = 0                                  # a black block has no white inside
        self.assertEqual(SF.find_stickers(black_square), [])
        self.assertEqual(SF.find_stickers(np.zeros((100, 100, 3), np.uint8)), [])

    def test_a_frame_without_the_orientation_square_is_rejected(self):
        sheet, info = small_sheet()
        ox, oy = info["origins"][0]
        edit = sheet.copy()
        edit[int((oy + 5) * 15):int((oy + 11) * 15), int((ox + 5) * 15):int((ox + 11) * 15)] = 255   # paint the square white
        img = S.render(edit, S.pose_homography(0, 0, 10.0), 1.0, (1, 1, 1), 0.8, 2.0, np.random.default_rng(1))
        self.assertEqual(len(SF.find_stickers(img)), 5)                      # the other five are still found

    def test_a_frame_with_two_dark_inner_corners_is_rejected(self):
        sheet, info = small_sheet()
        ox, oy = info["origins"][0]
        edit = sheet.copy()
        edit[int((oy + 39) * 15):int((oy + 45) * 15), int((ox + 39) * 15):int((ox + 45) * 15)] = 0   # a second dark corner
        img = S.render(edit, S.pose_homography(0, 0, 10.0), 1.0, (1, 1, 1), 0.8, 2.0, np.random.default_rng(1))
        self.assertEqual(len(SF.find_stickers(img)), 5)

    def test_a_mirrored_sticker_is_rejected(self):
        """A sticker seen in a mirror (a front camera, a mirrored preview) still has one dark corner, but its patches end up in
        the wrong places, so the straightened picture would be misread. It must not be accepted."""
        sheet, info = small_sheet()
        ox, oy = info["origins"][0]
        edit = sheet.copy()
        x0, y0 = int(ox * 15), int(oy * 15)
        edit[y0:y0 + 750, x0:x0 + 750] = edit[y0:y0 + 750, x0:x0 + 750][:, ::-1]      # mirror this one sticker left to right
        img = S.render(edit, S.pose_homography(0, 0, 10.0), 1.0, (1, 1, 1), 0.8, 2.0, np.random.default_rng(1))
        self.assertEqual(len(SF.find_stickers(img)), 5)

    def test_a_vertically_flipped_sticker_is_rejected_too(self):
        sheet, info = small_sheet()
        ox, oy = info["origins"][1]
        edit = sheet.copy()
        x0, y0 = int(ox * 15), int(oy * 15)
        edit[y0:y0 + 750, x0:x0 + 750] = edit[y0:y0 + 750, x0:x0 + 750][::-1]
        img = S.render(edit, S.pose_homography(0, 0, 10.0), 1.0, (1, 1, 1), 0.8, 2.0, np.random.default_rng(1))
        self.assertEqual(len(SF.find_stickers(img)), 5)

    def test_a_whole_photo_in_a_mirror_gives_no_sticker(self):
        img, _ = photo(rot=30)
        self.assertEqual(len(SF.find_stickers(img)), 6)
        self.assertEqual(SF.find_stickers(cv2.flip(img, 1)), [])
        self.assertEqual(SF.find_stickers(cv2.flip(img, 0)), [])

    def test_the_patches_of_an_accepted_crop_are_where_the_layout_says(self):
        img, _ = photo(rot=200)
        for s in SF.find_stickers(img):
            gray = cv2.cvtColor(s.crop, cv2.COLOR_RGB2GRAY).astype(float)
            cell = lambda cx, cy: gray[int((cy - 3) * 10):int((cy + 3) * 10), int((cx - 3) * 10):int((cx + 3) * 10)].mean()
            self.assertLess(cell(12.5, 25), 200)                                # grey patch on the left of the middle row
            self.assertLess(cell(37.5, 25), 200)                                # darkest skin patch on the right
            self.assertGreater(cell(25, 12.5), 230)                             # white above and below the middle patch
            self.assertGreater(cell(25, 37.5), 230)

    def test_a_sticker_cut_by_the_edge_of_the_photo_is_not_returned(self):
        img, Hmm = photo()
        c0 = S.project(Hmm, [[40, 40]])[0]                                   # inside the first sticker (origin 15,15 + 25)
        cut = img[int(c0[1]):, :]                                            # cut the first row of stickers in half
        self.assertLessEqual(len(SF.find_stickers(cut)), 3)                  # at most the second row

    def test_stickers_are_sorted_largest_scale_first(self):
        img, _ = photo(tilt=30)
        ppms = [s.px_per_mm for s in SF.find_stickers(img)]
        self.assertEqual(ppms, sorted(ppms, reverse=True))


class ScaleBiasTest(unittest.TestCase):
    """The scale must not depend on how blurry the photo is (a darkness threshold shrinks a blurred black frame)."""

    def mean_scale_error_pct(self, **kw):
        img, Hmm = photo(ppm=11.0, **kw)
        found = SF.find_stickers(img)
        self.assertEqual(len(found), 6)
        errs = []
        for s in found:
            _, (ox, oy) = true_top_left(Hmm, s.corners.mean(axis=0))
            errs.append(100 * (s.px_per_mm / S.local_ppm(Hmm, ox + 25, oy + 25) - 1))
        return float(np.mean(errs))

    def test_scale_is_unbiased_for_sharp_and_blurry_photos(self):
        for blur in (0.8, 1.5, 2.5):
            for tilt in (0, 30):
                self.assertLess(abs(self.mean_scale_error_pct(blur=blur, tilt=tilt)), 0.2, f"blur {blur} tilt {tilt}")

    def test_scale_is_unbiased_with_noise_and_rotation(self):
        self.assertLess(abs(self.mean_scale_error_pct(blur=2.0, noise=6.0, rot=33, tilt=15)), 0.25)

    def test_edge_fit_keeps_the_corners_when_there_is_no_contrast(self):
        flat = np.full((400, 400), 128, np.uint8)
        q = np.array([[100, 100], [300, 100], [300, 300], [100, 300]], float)
        np.testing.assert_allclose(SF._subpixel_corners(flat, q), q)

    def test_edge_fit_moves_a_shrunken_outline_back_to_the_half_level(self):
        lin = np.full((400, 400), 0.8, np.float32)
        lin[100:300, 100:300] = 0.01                                            # black square: pixels 100..299, its edges lie at 99.5 and 299.5
        lin = cv2.GaussianBlur(lin, (0, 0), 2.0)                                # a lens blurs light
        img = (lin ** (1 / 2.2) * 255).astype(np.uint8)
        q = np.array([[103, 103], [297, 103], [297, 297], [103, 297]], float)    # a threshold-style outline, 3 px inside
        out = SF._subpixel_corners(img, q, frame_px=40.0)
        self.assertLess(np.abs(out - [[99.5, 99.5], [299.5, 99.5], [299.5, 299.5], [99.5, 299.5]]).max(), 0.3)


class AreaTest(unittest.TestCase):
    def shape_mask(self, img, name, Hmm):
        m = cv2.morphologyEx(S.red_mask(img), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
        n, lab = cv2.connectedComponents(m)
        cp = S.project(Hmm, [list(S.SHAPES[name])])[0]
        k = min(range(1, n), key=lambda k: np.linalg.norm(np.argwhere(lab == k).mean(axis=0)[::-1] - cp))
        return lab == k

    def test_homography_area_corrects_tilt_and_one_scale_does_not(self):
        _, info = small_sheet()
        img, Hmm = photo(tilt=25, rot=40)
        found = SF.find_stickers(img)
        mask = self.shape_mask(img, "rectangle", Hmm)
        ctr = np.argwhere(mask).mean(axis=0)[::-1]
        st = min(found, key=lambda t: np.linalg.norm(t.corners.mean(axis=0) - ctr))
        true = info["areas"]["rectangle"]
        self.assertLess(abs(SF.area_mm2(mask, st) - true) / true, 0.02)
        self.assertGreater(abs(SF.area_uniform_mm2(mask, st) - true) / true, 0.10)

    def test_area_of_an_empty_mask_is_zero(self):
        img, _ = photo()
        st = SF.find_stickers(img)[0]
        self.assertEqual(SF.area_mm2(np.zeros(img.shape[:2], np.uint8), st), 0.0)
        self.assertEqual(SF.area_uniform_mm2(np.zeros(img.shape[:2], np.uint8), st), 0.0)

    def test_area_of_a_known_square_on_a_flat_photo(self):
        img, _ = photo()
        st = SF.find_stickers(img)[0]
        mask = np.zeros(img.shape[:2], np.uint8)
        side_px = int(round(20 * st.px_per_mm))                              # a 20 mm square
        mask[1000:1000 + side_px, 1000:1000 + side_px] = 1
        self.assertAlmostEqual(SF.area_mm2(mask, st), 400.0, delta=8.0)


if __name__ == "__main__":
    unittest.main()
