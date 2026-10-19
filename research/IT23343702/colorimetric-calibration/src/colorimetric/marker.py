"""Reference sticker: grey-patch exposure / white-balance correction (before pipeline Step 3).

The caller finds the sticker in the full-resolution photo, straightens it and passes the flat
crop (the whole sticker including the black frame). Colour is the only thing taken from it,
so the crop's size does not have to match the calibrated image's.

The grey patch fixes what raw-pixel ITA cannot: skin L* depends on how the camera exposed the
frame (DFUC: median skin L* 37 against 53 for the whole photo). Per-channel gains, applied in
linear light, make the grey read its reference colour, which fixes exposure and white balance
in one go. The two skin patches are not used for the fit yet; they are a check: after the
correction their error against the reference must be small, or the sticker is rejected.

Layout: `sticker/marker_layout.json` is a copy of research/IT23294066/area-measurement/marker_layout.json
(CV's file is the source of truth; tests/test_marker.py compares the two when both exist).
Reference colours: `sticker/marker_reference.json`. Its `source` says where they come from.

STATUS: calibration is RELATIVE. No spectrophotometer exists, so the reference colours are
stand-ins ("stand-in") until reference photos of the printed sticker fill the JSON
("reference-photos"). ITA bins assume absolute L*, so a class read after this correction is
only as good as the printed grey is close to L* 50. MEASURED stays False until real Lab
values of the printed patches exist. Outputs carry `reference_source` and `calibration_mode`
so nothing downstream calls this "accurate colour".
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np

MEASURED = False  # set True only together with a marker_reference.json whose source is "measured"

_DATA = Path(__file__).parent / "sticker"
LAYOUT_PATH = _DATA / "marker_layout.json"
REFERENCE_PATH = _DATA / "marker_reference.json"
REFERENCE_SOURCES = ("stand-in", "reference-photos", "measured")

# Reject rules. There are no real sticker photos yet, so these are judgement, not fitted.
MAX_GREY_L_STD = 5.0        # L* spread inside the grey patch; a flat matte patch is noise only
MAX_CLIPPED_FRACTION = 0.05  # share of grey pixels with any channel >= 250 or <= 5
GAIN_RANGE = (0.25, 4.0)    # per-channel linear gain, about +-2 stops
MIN_PATCH_PIXELS = 64
# Grey must look like a grey before the correction: distance of its (a*, b*) from the
# reference grey's. A bare camera under a very warm lamp (R/B gain ratio 2.5) reads C* = 18.5
# on an L* 50 grey; beyond 20 it is a stained patch or a wrong patch, not a colour cast.
MAX_GREY_AB_ERROR = 20.0
# After the correction the skin patches must be close to their reference (CIE76; L* only when
# the reference has no a*/b*). Only enforced for references that come from photos or a
# spectrophotometer: a stand-in is a nominal aim, and a printed patch can sit far from it.
MAX_SKIN_DELTA_E = 15.0


@dataclass(frozen=True)
class StickerLayout:
    """Patch geometry as fractions of the straightened crop, read from marker_layout.json."""

    patches: dict[str, tuple[float, float, float]]  # name -> (centre x, centre y, size)
    sample_fraction: float                         # central share of a patch that is read
    calibration_mode: str                          # "relative" | "absolute"

    @property
    def names(self) -> tuple[str, ...]:
        return tuple(self.patches)

    @property
    def skin_names(self) -> tuple[str, ...]:
        return tuple(n for n in self.patches if n.startswith("skin"))


@dataclass(frozen=True)
class MarkerReference:
    source: str                                    # one of REFERENCE_SOURCES
    lab: dict[str, tuple[float, float | None, float | None]]


def load_layout(path: str | Path | None = None) -> StickerLayout:
    d = json.loads(Path(path or LAYOUT_PATH).read_text())
    side = float(d["side_mm"])
    patches = {p["name"]: (p["centre"][0] / side, p["centre"][1] / side, p["size"] / side) for p in d["patches"]}
    if "grey" not in patches:
        raise ValueError("marker layout needs a patch named 'grey'")
    return StickerLayout(patches, float(d["use_central_fraction_of_patch"]), d["calibration_mode"])


def load_reference(path: str | Path | None = None) -> MarkerReference:
    d = json.loads(Path(path or REFERENCE_PATH).read_text())
    if d["source"] not in REFERENCE_SOURCES:
        raise ValueError(f"reference source must be one of {REFERENCE_SOURCES}, got {d['source']!r}")
    if d["source"] == "measured" and not MEASURED:
        raise ValueError("source 'measured' needs marker.MEASURED = True, set deliberately with real Lab values")
    lab = {k: tuple(v) for k, v in d["lab"].items()}
    if "grey" not in lab or lab["grey"][1] is None or lab["grey"][2] is None:
        raise ValueError("reference needs a full L*a*b* for the grey patch")
    return MarkerReference(d["source"], lab)


@dataclass
class MarkerResult:
    used: bool
    reason: str = ""                       # why the marker was rejected, if it was
    gains: np.ndarray | None = None        # per-channel linear gains (R, G, B)
    skin_delta_e: dict[str, float] = field(default_factory=dict)  # CIE76 after the correction
    reference_source: str = ""             # "stand-in" | "reference-photos" | "measured"
    calibration_mode: str = ""             # "relative" | "absolute"

    @property
    def label(self) -> str:
        """Plain-words description of what a class read after this correction rests on."""
        return f"{self.calibration_mode} calibration, {self.reference_source} reference colours"


def _srgb_to_linear(x: np.ndarray) -> np.ndarray:
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def _linear_to_srgb(x: np.ndarray) -> np.ndarray:
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def _lab_to_linear_rgb(lab: np.ndarray) -> np.ndarray:
    px = np.asarray(lab, dtype=np.float32).reshape(1, 1, 3)
    return _srgb_to_linear(np.clip(cv2.cvtColor(px, cv2.COLOR_LAB2RGB)[0, 0].astype(np.float64), 0, 1))


def _rgb_median_to_lab(pixels: np.ndarray) -> np.ndarray:
    rgb = np.median(pixels, axis=0).reshape(1, 1, 3).astype(np.uint8)
    lab = cv2.cvtColor(rgb, cv2.COLOR_RGB2LAB).astype(np.float64)[0, 0]
    return np.array([lab[0] * (100.0 / 255.0), lab[1] - 128.0, lab[2] - 128.0])


def _patch_pixels(crop: np.ndarray, name: str, layout: StickerLayout) -> np.ndarray:
    h, w = crop.shape[:2]
    cx, cy, size = layout.patches[name]
    half_w, half_h = size * layout.sample_fraction * w / 2, size * layout.sample_fraction * h / 2
    x0, x1 = int(round(cx * w - half_w)), int(round(cx * w + half_w))
    y0, y1 = int(round(cy * h - half_h)), int(round(cy * h + half_h))
    return crop[max(y0, 0):y1, max(x0, 0):x1].reshape(-1, 3)


def apply_gains(rgb: np.ndarray, gains: np.ndarray) -> np.ndarray:
    """Multiply linear-light RGB by per-channel gains; uint8 in, uint8 out (clipped)."""
    levels = _srgb_to_linear(np.arange(256) / 255.0)
    out = np.empty_like(rgb)
    for c in range(3):
        lut = np.clip(np.rint(_linear_to_srgb(np.clip(levels * gains[c], 0, 1)) * 255.0), 0, 255)
        out[..., c] = lut.astype(np.uint8)[rgb[..., c]]
    return out


def estimate_gains(
    crop: np.ndarray,
    layout: StickerLayout | None = None,
    reference: MarkerReference | None = None,
) -> MarkerResult:
    """Read a straightened sticker crop and return the correction gains, or a rejection.

    `used` is False (with `reason`) when the sticker looks unreliable; the caller then falls
    back to the skin-pixel behaviour.
    """
    if crop.dtype != np.uint8 or crop.ndim != 3 or crop.shape[2] != 3:
        raise ValueError("marker crop must be a uint8 RGB image of shape (H, W, 3)")
    layout = layout or load_layout()
    reference = reference or load_reference()
    meta = dict(reference_source=reference.source, calibration_mode=layout.calibration_mode)

    def reject(reason: str) -> MarkerResult:
        return MarkerResult(False, reason, **meta)

    grey = _patch_pixels(crop, "grey", layout)
    if len(grey) < MIN_PATCH_PIXELS:
        return reject(f"grey patch has only {len(grey)} pixels")
    clipped = float(np.mean((grey.max(axis=1) >= 250) | (grey.min(axis=1) <= 5)))
    if clipped > MAX_CLIPPED_FRACTION:
        return reject(f"grey patch clipped ({clipped:.0%} of pixels)")
    L = cv2.cvtColor(grey.reshape(-1, 1, 3), cv2.COLOR_RGB2LAB)[..., 0].astype(np.float64) * (100.0 / 255.0)
    if L.std() > MAX_GREY_L_STD:
        return reject(f"grey patch not uniform (L* std {L.std():.1f})")

    grey_lab = _rgb_median_to_lab(grey)
    ref_grey = np.array(reference.lab["grey"], dtype=np.float64)
    ab_error = float(np.linalg.norm(grey_lab[1:] - ref_grey[1:]))
    if ab_error > MAX_GREY_AB_ERROR:
        return reject(f"grey patch not neutral (a*b* {ab_error:.1f} from the reference, limit {MAX_GREY_AB_ERROR:.0f})")

    measured = _srgb_to_linear(np.median(grey, axis=0) / 255.0)
    gains = _lab_to_linear_rgb(ref_grey) / np.maximum(measured, 1e-6)
    if gains.min() < GAIN_RANGE[0] or gains.max() > GAIN_RANGE[1]:
        return reject(f"grey patch gains out of range ({gains.min():.2f}-{gains.max():.2f})")

    res = MarkerResult(True, gains=gains, **meta)
    corrected = apply_gains(crop, gains)
    for name in layout.skin_names:
        if name not in reference.lab:
            continue
        lab, ref = _rgb_median_to_lab(_patch_pixels(corrected, name, layout)), reference.lab[name]
        used = [i for i, v in enumerate(ref) if v is not None]
        res.skin_delta_e[name] = float(np.linalg.norm(lab[used] - np.array([ref[i] for i in used], dtype=np.float64)))
    if reference.source != "stand-in":
        worst = max(res.skin_delta_e.items(), key=lambda kv: kv[1], default=None)
        if worst is not None and worst[1] > MAX_SKIN_DELTA_E:
            return reject(f"{worst[0]} patch off by dE {worst[1]:.1f} after correction (limit {MAX_SKIN_DELTA_E:.0f})")
    return res


def correct_with_marker(
    rgb: np.ndarray,
    crop: np.ndarray,
    layout: StickerLayout | None = None,
    reference: MarkerReference | None = None,
) -> tuple[np.ndarray, MarkerResult]:
    """Apply the grey-patch correction to `rgb`; returns (image, result).

    If the marker is rejected the image is returned unchanged and `result.used` is False.
    """
    res = estimate_gains(crop, layout, reference)
    if not res.used:
        return rgb, res
    return apply_gains(rgb, res.gains), res
