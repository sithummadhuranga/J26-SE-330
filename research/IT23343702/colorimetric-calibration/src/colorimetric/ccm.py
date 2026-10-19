"""Adaptive 3x3 Color Correction Matrix (CCM) — Pipeline Step 4.

Per the research guide: for FST IV-VI images, compute a per-image 3x3
linear CCM via least squares against light-skin reference priors, applied
in LAB space. This is the core algorithmic contribution — computed fresh
for every image, not fixed globally.

Reference priors sourced from literature review (docs/literature_review.md):
Chairat et al. (2023) calibrate against a 24-patch Macbeth/X-Rite
ColorChecker but don't publish its values. The classic ColorChecker chart
has two patches literally labeled "dark skin" and "light skin" — those
published sRGB/LAB values (D65) are used here as the starting reference
priors. VERIFY against an authoritative ColorChecker spec sheet before
relying on these for real evaluation; they were recalled from the
standard chart spec, not read out of either paper.
"""

from __future__ import annotations

import cv2
import numpy as np

from .ita import SKIN_V_MIN, FitzpatrickType, skin_mask_hsv

# Classic X-Rite/Macbeth ColorChecker patches A1 (dark skin) and A2 (light
# skin), sRGB under D65. LAB values computed via OpenCV RGB2LAB for
# internal consistency with the rest of this pipeline (guide's Step 4
# operates in LAB).
REFERENCE_PRIORS: dict[str, dict[str, np.ndarray]] = {
    "dark_skin": {"rgb": np.array([115, 82, 68], dtype=np.float64)},
    "light_skin": {"rgb": np.array([194, 150, 130], dtype=np.float64)},
}


def _rgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    """Convert a single sRGB triple (0-255) to LAB (L in [0,100], a/b in [-128,127])."""
    patch = np.uint8([[rgb]])
    lab = cv2.cvtColor(patch, cv2.COLOR_RGB2LAB).astype(np.float64)[0, 0]
    return np.array([lab[0] * (100.0 / 255.0), lab[1] - 128.0, lab[2] - 128.0])


DEFAULT_MAX_KMEANS_PIXELS = 20_000
DEFAULT_L_CEILING = 95.0  # relative CCM: keep the brightest 1% of the image below this L*

# FST-stratified correction (SO3): fraction of the gap to the light-skin prior that is
# closed, from the guide's per-type strength (ita.CORRECTION_STRENGTH: none / light /
# moderate / strong / maximum). Starting values, to be tuned in the ablation.
CORRECTION_FRACTION: dict[FitzpatrickType, float] = {
    FitzpatrickType.I: 0.0,
    FitzpatrickType.II: 0.0,
    FitzpatrickType.III: 0.25,
    FitzpatrickType.IV: 0.5,
    FitzpatrickType.V: 0.75,
    FitzpatrickType.VI: 1.0,
}

# L* variance normalisation target: median per-image skin L* std dev of Fitzpatrick17k
# FST I-III images after Step 1 + MSR (8.9 / 8.4 / 8.3, n=298). FST IV-VI were similar
# (8.5 / 9.3 / 9.1), so this mainly undoes the spread the CCM gain adds.
REFERENCE_SKIN_L_STD = 8.5
L_STD_GAIN_RANGE = (0.8, 1.25)

for _name in REFERENCE_PRIORS:
    REFERENCE_PRIORS[_name]["lab"] = _rgb_to_lab(REFERENCE_PRIORS[_name]["rgb"])


def compute_ccm(source_colors: np.ndarray, target_colors: np.ndarray) -> np.ndarray:
    """Solve the 3x3 CCM mapping source_colors -> target_colors via least squares.

    source_colors, target_colors: (N, 3) arrays of corresponding color
    triples (same space, e.g. both LAB). N should be >= 3 for a
    well-posed fit, more points give a more stable/generalizable matrix.

    Returns M such that `source_colors @ M ~= target_colors` (see apply_ccm).
    """
    if source_colors.shape != target_colors.shape or source_colors.shape[1] != 3:
        raise ValueError("source_colors and target_colors must both be (N, 3)")
    M, _, _, _ = np.linalg.lstsq(source_colors, target_colors, rcond=None)
    return M


def apply_ccm(colors: np.ndarray, M: np.ndarray) -> np.ndarray:
    """Apply a 3x3 CCM to an (N, 3) array of color triples (or an (H, W, 3) image).

    M must be the matrix returned by compute_ccm (source_colors @ M ~= target_colors).
    """
    flat = colors.reshape(-1, 3)
    corrected = flat @ M
    return corrected.reshape(colors.shape)


def sample_skin_lab_colors(
    rgb_image: np.ndarray,
    wound_bbox: tuple[int, int, int, int] | None = None,
    k: int = 6,
    max_pixels: int | None = DEFAULT_MAX_KMEANS_PIXELS,
    v_min: float = SKIN_V_MIN,
) -> np.ndarray:
    """k representative skin colours (LAB), see `_cluster_skin`."""
    return _cluster_skin(rgb_image, wound_bbox, k, max_pixels, v_min)[0]


def _cluster_skin(
    rgb_image: np.ndarray,
    wound_bbox: tuple[int, int, int, int] | None,
    k: int,
    max_pixels: int | None,
    v_min: float = SKIN_V_MIN,
) -> tuple[np.ndarray, np.ndarray]:
    """Cluster non-wound skin pixels (LAB space) into k representative colors via k-means.

    Multiple representative colors (rather than one mean color) give the
    least-squares CCM solve something to actually fit against, instead of
    being underdetermined from a single point.

    k-means runs on a fixed-seed random sample of at most `max_pixels` skin pixels
    (None = all of them); k=6 centres are stable with far fewer pixels than a photo has.
    Returns (centres (k, 3), pixel count per centre).
    """
    mask = skin_mask_hsv(rgb_image, wound_bbox=wound_bbox, v_min=v_min)
    lab = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB).astype(np.float32)
    lab[..., 0] *= 100.0 / 255.0
    lab[..., 1] -= 128.0
    lab[..., 2] -= 128.0

    pixels = lab[mask]
    if len(pixels) < k:
        raise ValueError(f"Only {len(pixels)} skin pixels found, need >= k={k} for clustering")
    if max_pixels is not None and len(pixels) > max_pixels:
        idx = np.random.default_rng(0).choice(len(pixels), max_pixels, replace=False)
        pixels = pixels[np.sort(idx)]

    criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 100, 0.2)
    _, labels, centers = cv2.kmeans(pixels, k, None, criteria, 10, cv2.KMEANS_PP_CENTERS)
    counts = np.bincount(labels.ravel(), minlength=k).astype(np.float64)
    return centers.astype(np.float64), counts


def estimate_adaptive_ccm(
    rgb_image: np.ndarray,
    fitzpatrick_type: FitzpatrickType,
    wound_bbox: tuple[int, int, int, int] | None = None,
    k: int = 6,
    correct: bool | None = None,
    shift: str = "relative",
    l_ceiling: float | None = DEFAULT_L_CEILING,
    stratified: bool = True,
    v_min: float = SKIN_V_MIN,
) -> np.ndarray | None:
    """Estimate a per-image 3x3 CCM for FST IV-VI images; None for FST I-III (no correction).

    `correct` overrides the FST gate: True/False forces/skips the correction (used to
    evaluate other gates, e.g. `needs_ccm(ita, GATE_CUTOFF_FITTED)`); None keeps the
    default FST IV-VI rule.

    Sample k representative skin colours, build target colours, solve the 3x3 by least
    squares. `shift` chooses the targets:

    "relative" (default, v2): raise L* only, by the gap between the skin's measured
        mean L* and the light-skin prior's L* (never below 0, so skin already at or above
        the prior is left alone). a*/b* targets equal the sources, so the fitted a*/b*
        columns are exactly identity and redness (erythema) is kept.
        A 3x3 can only lift L* by scaling it, which also scales bright pixels; the gap is
        therefore capped so the image's 99th-percentile L* lands at or below `l_ceiling`
        (None = no cap). Without the cap FST VI images in Fitzpatrick17k had 7% of pixels
        pushed past L*=95, i.e. wound/lesion detail lost on exactly the darkest skin.
        With `stratified` (SO3) only `CORRECTION_FRACTION[fitzpatrick_type]` of the gap is
        closed (IV half, V three quarters, VI all of it); False closes the whole gap.
    "fixed" (v1): add the full ColorChecker dark->light LAB offset (+27.8 L*, +2 a*,
        +3 b*) to every image. Kept for the ablation. On 8 ISIC images that took the
        CCM path after Step 1 + MSR it pushed L* to 85-93, flagged 51-89% of the frame
        as specular, and cut mean a* by up to half.
    """
    if shift not in ("relative", "fixed"):
        raise ValueError(f"unknown shift {shift!r}")
    if correct is None:
        correct = fitzpatrick_type not in (FitzpatrickType.I, FitzpatrickType.II, FitzpatrickType.III)
    if not correct:
        return None

    source_colors, counts = _cluster_skin(rgb_image, wound_bbox, k, DEFAULT_MAX_KMEANS_PIXELS, v_min)
    if shift == "fixed":
        lab_shift = REFERENCE_PRIORS["light_skin"]["lab"] - REFERENCE_PRIORS["dark_skin"]["lab"]
    else:
        skin_L = float(np.average(source_colors[:, 0], weights=counts))
        gap = max(0.0, REFERENCE_PRIORS["light_skin"]["lab"][0] - skin_L)
        if stratified:
            gap *= CORRECTION_FRACTION[fitzpatrick_type]
        if l_ceiling is not None and gap > 0:
            L_img = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB)[..., 0]
            p99 = float(np.percentile(L_img, 99)) * 100.0 / 255.0
            max_gain = max(1.0, l_ceiling / max(p99, 1e-6))
            gap = min(gap, (max_gain - 1.0) * skin_L)
        lab_shift = np.array([gap, 0.0, 0.0])
    target_colors = source_colors + lab_shift

    # Fit the change from identity, not M itself: identical when the sampled colours span
    # all three LAB axes, but when they don't (near-uniform skin) the minimum-norm solution
    # is then "leave that direction alone" instead of an arbitrary matrix mixing a*/b*.
    return np.eye(3) + compute_ccm(source_colors, target_colors - source_colors)


def apply_adaptive_ccm(rgb_image: np.ndarray, M: np.ndarray) -> np.ndarray:
    """Apply a CCM (fit in LAB space) to a full RGB image, returning corrected RGB."""
    lab = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB).astype(np.float64)
    lab[..., 0] *= 100.0 / 255.0
    lab[..., 1] -= 128.0
    lab[..., 2] -= 128.0

    corrected_lab = apply_ccm(lab, M)

    corrected_lab[..., 0] = np.clip(corrected_lab[..., 0] * (255.0 / 100.0), 0, 255)
    corrected_lab[..., 1] = np.clip(corrected_lab[..., 1] + 128.0, 0, 255)
    corrected_lab[..., 2] = np.clip(corrected_lab[..., 2] + 128.0, 0, 255)

    corrected_rgb = cv2.cvtColor(corrected_lab.astype(np.uint8), cv2.COLOR_LAB2RGB)
    return corrected_rgb


def normalise_skin_L_variance(
    rgb_image: np.ndarray,
    skin_mask: np.ndarray,
    target_std: float = REFERENCE_SKIN_L_STD,
    gain_range: tuple[float, float] = L_STD_GAIN_RANGE,
    l_ceiling: float | None = DEFAULT_L_CEILING,
) -> np.ndarray:
    """Scale L* about the skin's mean L* so the skin's L* std dev becomes `target_std`.

    The second half of Step 4 ("normalise L* variance"). The CCM sets the skin's mean L*;
    a 3x3 can only do that by scaling L*, which also scales skin texture and noise, so this
    puts the spread back to the light-skin reference. The mean of the skin is unchanged and
    a*/b* are not touched (FR12). The gain is limited to `gain_range` and, when above 1,
    also so the image's 99th-percentile L* stays at or below `l_ceiling`.
    `skin_mask` is passed in (taken before the CCM) because the HSV skin test can change
    once the image has been lightened. Returns the input unchanged if fewer than 50 skin pixels.
    """
    if skin_mask.sum() < 50:
        return rgb_image.copy()
    lab = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB).astype(np.float32)
    L = lab[..., 0] * (100.0 / 255.0)
    mean, std = float(L[skin_mask].mean()), float(L[skin_mask].std())
    if std < 1e-6:
        return rgb_image.copy()
    gain = float(np.clip(target_std / std, *gain_range))
    if gain > 1.0 and l_ceiling is not None:
        p99 = float(np.percentile(L, 99))
        if p99 > mean:
            gain = max(1.0, min(gain, (l_ceiling - mean) / (p99 - mean)))
    lab[..., 0] = np.clip((mean + (L - mean) * gain) * (255.0 / 100.0), 0, 255)
    return cv2.cvtColor(np.round(lab).astype(np.uint8), cv2.COLOR_LAB2RGB)
