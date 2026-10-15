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

from .ita import FitzpatrickType, skin_mask_hsv

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
) -> np.ndarray:
    """Cluster non-wound skin pixels (LAB space) into k representative colors via k-means.

    Multiple representative colors (rather than one mean color) give the
    least-squares CCM solve something to actually fit against, instead of
    being underdetermined from a single point.

    k-means runs on a fixed-seed random sample of at most `max_pixels` skin pixels
    (None = all of them); k=6 centres are stable with far fewer pixels than a photo has.
    """
    mask = skin_mask_hsv(rgb_image, wound_bbox=wound_bbox)
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
    _, _, centers = cv2.kmeans(pixels, k, None, criteria, 10, cv2.KMEANS_PP_CENTERS)
    return centers.astype(np.float64)


def estimate_adaptive_ccm(
    rgb_image: np.ndarray,
    fitzpatrick_type: FitzpatrickType,
    wound_bbox: tuple[int, int, int, int] | None = None,
    k: int = 6,
    correct: bool | None = None,
) -> np.ndarray | None:
    """Estimate a per-image 3x3 CCM for FST IV-VI images; None for FST I-III (no correction).

    `correct` overrides the FST gate: True/False forces/skips the correction (used to
    evaluate other gates, e.g. `needs_ccm(ita, GATE_CUTOFF_FITTED)`); None keeps the
    default FST IV-VI rule.

    v1 approach (documented approximation, revisit during Phase 4
    evaluation): sample k representative skin colors from the image, then
    build synthetic target correspondences by applying the fixed LAB
    offset between the ColorChecker's dark-skin and light-skin reference
    patches to each sampled color. This assumes the same additive
    lighting/tone shift applies uniformly across the sampled skin tones,
    which is a simplification — validate against real annotated
    before/after pairs once available, per the Phase 4 ablation study.
    """
    if correct is None:
        correct = fitzpatrick_type not in (FitzpatrickType.I, FitzpatrickType.II, FitzpatrickType.III)
    if not correct:
        return None

    source_colors = sample_skin_lab_colors(rgb_image, wound_bbox=wound_bbox, k=k)
    lab_shift = REFERENCE_PRIORS["light_skin"]["lab"] - REFERENCE_PRIORS["dark_skin"]["lab"]
    target_colors = source_colors + lab_shift

    return compute_ccm(source_colors, target_colors)


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
