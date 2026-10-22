"""Reference-session photos -> the sticker's reference colours (marker_reference.json, source "reference-photos").

Protocol (reply 7): a sticker and an 18% grey card lie side by side in one plane, shot under several lights. For one photo
the card tells how a known neutral looks to the camera, so a patch colour is read relative to the card:

    relative linear RGB = patch / card (per channel) * 0.18        -> XYZ (sRGB, D65) -> L*a*b*

Exposure and white balance cancel in the ratio. Assumptions, stated so they are not forgotten: the card is neutral with
reflectance 0.18 (L* 49.5; its real tolerance is unknown), the camera records linear light through the sRGB curve (a phone's
local tone mapping would break this), and the light is the same on the card and the sticker.

Inputs per photo are two flat crops: the straightened sticker (as the sticker finder gives it) and a patch of the card.
Rows are grouped by phone; phones are never averaged together. Nothing here has been run on real photos.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from .marker import (
    MAX_CLIPPED_FRACTION,
    MAX_GREY_L_STD,
    MIN_PATCH_PIXELS,
    StickerLayout,
    _patch_pixels,
    _srgb_to_linear,
    load_layout,
)

CARD_REFLECTANCE = 0.18  # nominal 18% grey card, L* 49.5
KEEP_GREY_MAX_L_SPREAD = 1.0   # from reply 7, 5.3: printed grey is kept only if its L* spread is at most 1 ...
KEEP_GREY_MAX_AB = 5.0         # ... and its a*b* distance from neutral at most 5 (judgement; follows from the 1 L* need)

_RGB_TO_XYZ = np.array([[0.4124564, 0.3575761, 0.1804375],
                        [0.2126729, 0.7151522, 0.0721750],
                        [0.0193339, 0.1191920, 0.9503041]])
_WHITE = np.array([0.95047, 1.0, 1.08883])


def linear_rgb_to_lab(rgb_lin: np.ndarray) -> np.ndarray:
    """Float CIE L*a*b* (D65) of a linear sRGB triple, no 8-bit rounding."""
    t = (_RGB_TO_XYZ @ np.asarray(rgb_lin, dtype=np.float64)) / _WHITE
    f = np.where(t > (6 / 29) ** 3, np.cbrt(t), t / (3 * (6 / 29) ** 2) + 4 / 29)
    return np.array([116 * f[1] - 16, 500 * (f[0] - f[1]), 200 * (f[1] - f[2])])


def _clipped(pixels: np.ndarray) -> float:
    return float(np.mean((pixels.max(axis=1) >= 250) | (pixels.min(axis=1) <= 5)))


def _median_linear(pixels: np.ndarray) -> np.ndarray:
    return _srgb_to_linear(np.median(pixels, axis=0) / 255.0)


def patch_labs_from_card(sticker: np.ndarray, card: np.ndarray, layout: StickerLayout | None = None) -> dict[str, np.ndarray]:
    """L*a*b* of every sticker patch relative to the card, for one photo. Raises ValueError if the photo cannot be used."""
    for name, img in (("sticker", sticker), ("card", card)):
        if img.dtype != np.uint8 or img.ndim != 3 or img.shape[2] != 3:
            raise ValueError(f"{name} crop must be a uint8 RGB image of shape (H, W, 3)")
    layout = layout or load_layout()
    card_px = card.reshape(-1, 3)
    if len(card_px) < MIN_PATCH_PIXELS:
        raise ValueError(f"card crop has only {len(card_px)} pixels")
    if _clipped(card_px) > MAX_CLIPPED_FRACTION:
        raise ValueError(f"card clipped ({_clipped(card_px):.0%} of pixels)")
    card_lin = _median_linear(card_px)
    out = {}
    for name in layout.names:
        px = _patch_pixels(sticker, name, layout)
        if len(px) < MIN_PATCH_PIXELS:
            raise ValueError(f"{name} patch has only {len(px)} pixels")
        if _clipped(px) > MAX_CLIPPED_FRACTION:
            raise ValueError(f"{name} patch clipped ({_clipped(px):.0%} of pixels)")
        out[name] = linear_rgb_to_lab(_median_linear(px) / np.maximum(card_lin, 1e-6) * CARD_REFLECTANCE)
    return out


@dataclass
class ReferenceRow:
    phone: str
    sticker_id: str
    light: str
    shot: str
    labs: dict[str, np.ndarray]


@dataclass
class PhoneReference:
    phone: str
    n_rows: int
    lab: dict[str, list[float]]            # median over rows: name -> [L*, a*, b*]
    spread: dict[str, dict[str, float]]    # name -> {"L_std", "a_std", "b_std"} over rows
    grey_ab_from_neutral: float
    keep_printed_grey: bool                # the decision rule of reply 7, 5.3
    reason: str

    def reference_json(self) -> dict:
        """Content for a marker_reference file; load with marker.load_reference."""
        return {"version": 1, "source": "reference-photos", "phone": self.phone, "n_rows": self.n_rows,
                "lab": {k: [round(v, 2) for v in vals] for k, vals in self.lab.items()},
                "spread": {k: {m: round(x, 3) for m, x in d.items()} for k, d in self.spread.items()},
                "grey_decision": {"keep_printed_grey": self.keep_printed_grey, "reason": self.reason}}


@dataclass
class ReferenceReport:
    phones: dict[str, PhoneReference] = field(default_factory=dict)
    dropped: list[dict] = field(default_factory=list)  # rows that could not be used, with the reason


def build_reference(rows: list[ReferenceRow]) -> dict[str, PhoneReference]:
    """Median over stickers, lights and shots, per phone, with the spread and the grey decision."""
    out: dict[str, PhoneReference] = {}
    for phone in sorted({r.phone for r in rows}):
        mine = [r for r in rows if r.phone == phone]
        names = list(mine[0].labs)
        lab, spread = {}, {}
        for n in names:
            a = np.array([r.labs[n] for r in mine])
            lab[n] = np.median(a, axis=0).tolist()
            sd = a.std(axis=0, ddof=1) if len(a) > 1 else np.full(3, np.nan)
            spread[n] = {"L_std": float(sd[0]), "a_std": float(sd[1]), "b_std": float(sd[2])}
        ab = float(np.hypot(lab["grey"][1], lab["grey"][2]))
        l_std = spread["grey"]["L_std"]
        if len(mine) < 2:
            keep, why = False, "fewer than 2 usable photos: spread unknown, so the printed grey cannot be accepted"
        elif l_std <= KEEP_GREY_MAX_L_SPREAD and ab <= KEEP_GREY_MAX_AB:
            keep, why = True, f"grey L* spread {l_std:.2f} <= {KEEP_GREY_MAX_L_SPREAD:g} and a*b* {ab:.2f} <= {KEEP_GREY_MAX_AB:g}"
        else:
            keep, why = False, f"grey L* spread {l_std:.2f} (limit {KEEP_GREY_MAX_L_SPREAD:g}) or a*b* {ab:.2f} (limit {KEEP_GREY_MAX_AB:g}) too large: cut the grey from the card"
        out[phone] = PhoneReference(phone, len(mine), lab, spread, ab, keep, why)
    return out
