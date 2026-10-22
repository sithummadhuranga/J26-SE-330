"""Dark-skin regression guard: fails if a change to the pipeline hurts dark skin.

Synthetic photos at seven skin tones (ITA from +57 to -61, FST I to VI), each with the same red lesion and a white and black
reference bar like the sticker's frame (a real photo has an intensity range; skin alone would make the intensity stretch of Step 1
inflate colour). The default pipeline is run on each and five properties are checked per skin type. The limits come from the first
measured run (given in each comment), with margin: they guard against a regression, they are not a claim about real patients.

A guard that cannot fail proves nothing, so test_the_guard_catches_a_pipeline_without_the_ccm switches the CCM off on purpose.
"""

from dataclasses import replace

import cv2
import numpy as np
import pytest

from colorimetric.pipeline import PipelineConfig, process_image
from colorimetric.preprocess import rgb_to_lab

SKIN_L = (72, 62, 52, 44, 36, 30, 25)          # L* of the skin (a* = b* = 14), light to dark
DARK_ITA = 28.0                                 # the CCM gate: ITA at or below this is FST IV-VI

# limits (first measured run in brackets)
MIN_DARK_LIFT = 5.0          # skin L* lift for dark skin [8.8 .. 12.4]
MIN_CONTRAST_RATIO = 0.95    # lesion-vs-skin colour distance after / before, every skin type [1.03 .. 1.11]
MAX_CONTRAST_SPREAD = 0.15   # max - min of that ratio across skin types [0.08]
MAX_CHROMA_SPREAD = 1.0      # spread of the skin a* change across skin types, in a* units [0.7]
MAX_BLOWN = 0.005            # share of skin pixels above L* 95 [0.0]


def synth(L, seed=0, size=(480, 640)):
    rng = np.random.default_rng(seed)
    h, w = size
    yy, xx = np.mgrid[0:h, 0:w]
    shade = 4.0 * np.sin(xx / w * np.pi) * np.cos(yy / h * np.pi)                       # slow lighting gradient
    tex = cv2.GaussianBlur(rng.normal(0, 3.0, (h, w)).astype(np.float32), (0, 0), 2)    # skin texture
    lab = np.stack([np.full((h, w), L, np.float32) + shade + tex, np.full((h, w), 14.0, np.float32), np.full((h, w), 14.0, np.float32)], -1)
    lesion = ((xx - w * 0.5) ** 2 + (yy - h * 0.5) ** 2) < (min(h, w) * 0.18) ** 2
    lab[lesion, 0] -= 8.0
    lab[lesion, 1] += 14.0                                                              # redder: erythema
    px = np.empty_like(lab)
    px[..., 0] = np.clip(lab[..., 0], 0, 100) * 255 / 100
    px[..., 1:] = lab[..., 1:] + 128
    rgb = cv2.cvtColor(np.clip(np.rint(px), 0, 255).astype(np.uint8), cv2.COLOR_LAB2RGB)
    rgb[: int(h * 0.07)] = 245
    rgb[-int(h * 0.07):] = 18
    skin = np.ones((h, w), bool)
    skin[: int(h * 0.09)] = False
    skin[-int(h * 0.09):] = False
    return rgb, lesion, skin


def measure(config):
    """Per skin tone: ITA, whether the CCM ran, skin L* lift, skin a* change, lesion contrast ratio, share of blown skin pixels."""
    rows = []
    for L in SKIN_L:
        rgb, lesion, skin = synth(L)
        res = process_image(rgb, config)
        ring = skin & ~cv2.dilate(lesion.astype(np.uint8), np.ones((41, 41), np.uint8)).astype(bool)
        before, after = rgb_to_lab(rgb), rgb_to_lab(res.image)

        def mean(lab, mask, c):
            return float(lab[..., c][mask].mean())

        def contrast(lab):
            return float(np.linalg.norm([mean(lab, lesion, c) - mean(lab, ring, c) for c in range(3)]))

        rows.append(dict(L=L, ita=res.ita_angle, ccm=res.ccm_applied, lift=mean(after, ring, 0) - mean(before, ring, 0),
                         d_a=mean(after, ring, 1) - mean(before, ring, 1), contrast_ratio=contrast(after) / contrast(before),
                         blown=float((after[..., 0][skin] > 95).mean())))
    return rows


def violations(rows):
    """Names of the properties that do not hold. Empty when the pipeline treats every skin type acceptably."""
    bad = []
    dark = [r for r in rows if r["ita"] is not None and r["ita"] <= DARK_ITA]
    if not dark or any(r["lift"] < MIN_DARK_LIFT for r in dark):
        bad.append("dark skin is not lifted enough")
    if any(r["lift"] < 0 for r in rows if r["ccm"]):
        bad.append("a corrected image got darker")
    if any(r["contrast_ratio"] < MIN_CONTRAST_RATIO for r in rows):
        bad.append("a skin type lost lesion contrast")
    if max(r["contrast_ratio"] for r in rows) - min(r["contrast_ratio"] for r in rows) > MAX_CONTRAST_SPREAD:
        bad.append("lesion contrast is treated unequally across skin types")
    if max(r["d_a"] for r in rows) - min(r["d_a"] for r in rows) > MAX_CHROMA_SPREAD:
        bad.append("redness (a*) is changed unequally across skin types")
    if any(r["blown"] > MAX_BLOWN for r in rows):
        bad.append("highlights are blown out")
    if any(bool(r["ccm"]) != (r["ita"] <= DARK_ITA) for r in rows if r["ita"] is not None):
        bad.append("the CCM does not follow the dark/light gate")
    return bad


@pytest.fixture(scope="module")
def default_rows():
    return measure(PipelineConfig(check_agreement=False))


def test_the_default_pipeline_does_not_hurt_dark_skin(default_rows):
    assert violations(default_rows) == []


def test_every_skin_type_from_I_to_VI_is_covered(default_rows):
    assert min(r["ita"] for r in default_rows) < -50 and max(r["ita"] for r in default_rows) > 50
    assert sum(r["ccm"] for r in default_rows) >= 4 and sum(not r["ccm"] for r in default_rows) >= 2


def test_dark_skin_is_lifted_more_the_darker_it_is(default_rows):
    lifts = [r["lift"] for r in default_rows if r["ccm"]]          # ordered light to dark
    assert all(b >= a - 0.5 for a, b in zip(lifts, lifts[1:]))


def test_the_guard_catches_a_pipeline_without_the_ccm():
    rows = measure(replace(PipelineConfig(check_agreement=False), use_ccm=False))
    assert "dark skin is not lifted enough" in violations(rows)


def test_the_guard_catches_the_old_v1_ccm():
    """v1 adds a fixed +27.8 L* to every corrected image (about +24 measured here). On these synthetic photos it does not blow
    highlights out, but it changes redness unequally across skin types, which is the property that trips."""
    rows = measure(PipelineConfig(check_agreement=False, ccm_shift="fixed", ccm_stratified=False, ccm_l_variance=False))
    assert "redness (a*) is changed unequally across skin types" in violations(rows)


def test_the_pipeline_is_deterministic():
    rgb, _, _ = synth(30)
    cfg = PipelineConfig(check_agreement=False)
    assert np.array_equal(process_image(rgb, cfg).image, process_image(rgb, cfg).image)
