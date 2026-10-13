"""ITA (Individual Typology Angle) computation and Fitzpatrick Skin Type
(FST) classification.

Pipeline Step 3 of the Member 2 colorimetric bias-correction pipeline.

    ITA = arctan((L* - 50) / b*) * (180 / pi)

L* and b* are read from the CIELAB representation of non-wound skin
pixels sampled via an HSV skin mask. The resulting angle is bucketed
into one of six Fitzpatrick types using the boundaries from the
research guide (Section 2, "The Fitzpatrick Skin Type (FST) scale").
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum

import cv2
import numpy as np


class FitzpatrickType(str, Enum):
    I = "I"
    II = "II"
    III = "III"
    IV = "IV"
    V = "V"
    VI = "VI"


# (lower_bound_exclusive, upper_bound_inclusive) in degrees, checked top-down.
# Matches the research guide table exactly:
#   I   > 55
#   II  41-55
#   III 28-41
#   IV  10-28
#   V   -30 to 10
#   VI  < -30
ITA_BOUNDARIES: list[tuple[FitzpatrickType, float, float]] = [
    (FitzpatrickType.I, 55.0, float("inf")),
    (FitzpatrickType.II, 41.0, 55.0),
    (FitzpatrickType.III, 28.0, 41.0),
    (FitzpatrickType.IV, 10.0, 28.0),
    (FitzpatrickType.V, -30.0, 10.0),
    (FitzpatrickType.VI, float("-inf"), -30.0),
]

# Correction strength Step 4 (adaptive CCM) should apply per FST class.
CORRECTION_STRENGTH: dict[FitzpatrickType, str] = {
    FitzpatrickType.I: "none",
    FitzpatrickType.II: "none",
    FitzpatrickType.III: "light",
    FitzpatrickType.IV: "moderate",
    FitzpatrickType.V: "strong",
    FitzpatrickType.VI: "maximum",
}


def compute_ita(L: np.ndarray | float, b: np.ndarray | float) -> np.ndarray | float:
    """ITA = arctan((L* - 50) / b*) * (180 / pi).

    L, b are CIELAB channels (L* in [0, 100], b* in [-128, 127]).
    Accepts scalars or arrays; uses arctan2 so b* == 0 doesn't raise.
    """
    return np.degrees(np.arctan2(np.asarray(L, dtype=np.float64) - 50.0, np.asarray(b, dtype=np.float64)))


def classify_fitzpatrick(ita: float) -> FitzpatrickType:
    """Bucket a single ITA angle (degrees) into a Fitzpatrick type."""
    for fst, lower, upper in ITA_BOUNDARIES:
        if lower < ita <= upper:
            return fst
    # ita exactly at -inf edge case (shouldn't happen with finite input)
    return FitzpatrickType.VI


def skin_mask_hsv(
    rgb_image: np.ndarray,
    wound_bbox: tuple[int, int, int, int] | None = None,
) -> np.ndarray:
    """Build a boolean skin mask per Step 3 of the pipeline.

    HSV thresholds: H in [0, 25], S in [15%, 80%], V in [35%, 100%].
    wound_bbox, if given, is (x, y, w, h) in pixel coords and is excluded
    from the mask so wound tissue never contaminates the skin-tone sample.
    """
    if rgb_image.dtype != np.uint8:
        raise ValueError("rgb_image must be uint8 RGB in [0, 255]")

    hsv = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2HSV)
    h, s, v = hsv[..., 0], hsv[..., 1], hsv[..., 2]

    # OpenCV HSV ranges: H in [0,179], S,V in [0,255]. Research guide gives
    # H in degrees [0,25] (0-360 scale) and S,V as percentages.
    h_deg = h.astype(np.float32) * 2.0  # 0-179 -> 0-358 degrees
    s_pct = s.astype(np.float32) / 255.0 * 100.0
    v_pct = v.astype(np.float32) / 255.0 * 100.0

    mask = (h_deg >= 0) & (h_deg <= 25) & (s_pct >= 15) & (s_pct <= 80) & (v_pct >= 35) & (v_pct <= 100)

    if wound_bbox is not None:
        x, y, w, h_box = wound_bbox
        mask[y : y + h_box, x : x + w] = False

    return mask


@dataclass
class ITAResult:
    ita_angle: float
    fitzpatrick_type: FitzpatrickType
    mean_L: float
    mean_b: float
    skin_pixel_count: int
    correction_strength: str


def classify_image(
    rgb_image: np.ndarray,
    wound_bbox: tuple[int, int, int, int] | None = None,
    min_skin_pixels: int = 100,
) -> ITAResult:
    """Run the full ITA classification stage on a single RGB image.

    1. Build an HSV skin mask excluding the wound bounding box.
    2. Convert to LAB and average L*, b* over the masked skin pixels.
    3. Compute ITA and classify into a Fitzpatrick type.
    """
    mask = skin_mask_hsv(rgb_image, wound_bbox=wound_bbox)
    skin_pixel_count = int(mask.sum())
    if skin_pixel_count < min_skin_pixels:
        raise ValueError(
            f"Only {skin_pixel_count} skin pixels found (need >= {min_skin_pixels}). "
            "Check wound_bbox and lighting; the skin mask may be too strict for this image."
        )

    lab = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB).astype(np.float64)
    # OpenCV packs LAB as L in [0,255] (scale of 100/255), a,b in [0,255] offset by 128
    L = lab[..., 0] * (100.0 / 255.0)
    b = lab[..., 2] - 128.0

    mean_L = float(L[mask].mean())
    mean_b = float(b[mask].mean())

    ita = float(compute_ita(mean_L, mean_b))
    fst = classify_fitzpatrick(ita)

    return ITAResult(
        ita_angle=ita,
        fitzpatrick_type=fst,
        mean_L=mean_L,
        mean_b=mean_b,
        skin_pixel_count=skin_pixel_count,
        correction_strength=CORRECTION_STRENGTH[fst],
    )
