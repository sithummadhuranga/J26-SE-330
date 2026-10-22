"""Capture quality gate: is this photo fit for area and colour? ok | warn | retake, with a stable code and a plain sentence.

A pure decision table. It reads three things the capture step already knows:
  finder code   why the sticker finder found nothing (None when a sticker was accepted); see sticker_finder.REJECT_CODES
  marker code   why the grey-patch correction was refused (marker.MARKER_CODES; "" when it was used, None when not evaluated)
  skin pixels   how many skin-coloured pixels the skin type was read from (None when not evaluated, e.g. a photo of the sheet)
The worst result wins; ties go by TABLE_ORDER. `warn` still allows the area (its scale comes from the sticker frame, not from the
colours); `retake` means no usable sticker. Logic uses the codes; the English sentences are defaults for logs and the Python tools,
the app has its own strings (and translations).

The table and its sentences were proposed by CV (message 11) and are checked by tests/golden/quality_expected.json, which the
Dart side is checked against too. Thresholds elsewhere come from our own photos; nothing here is validated on patients.
"""

from __future__ import annotations

from dataclasses import dataclass, field

MIN_SKIN_PIXELS = 100  # the same minimum the skin type needs (ita.classify_image)

# code -> level, in table order (finder, marker, skin).
_TABLE = (
    ("no_candidate", "retake", "No sticker found. Put the sticker flat beside the wound."),
    ("cut_by_edge", "retake", "The sticker is cut off. Move back so all four corners show."),
    ("weak_frame_contrast", "retake", "Glare or shadow on the sticker. Tilt the phone away from the light."),
    ("weak_orientation", "retake", "The sticker corner is not clear. Use a clean sticker."),
    ("layout_mismatch", "retake", "Sticker not recognised. Use the sticker from the kit."),
    ("mirrored", "retake", "The sticker looks mirrored. Use the rear camera."),
    ("crop_invalid", "retake", "The sticker could not be read. Take the photo again."),
    ("grey_clipped", "warn", "Too bright or too dark. Colour may be off."),
    ("grey_not_uniform", "warn", "Uneven light on the sticker. Colour may be off."),
    ("grey_not_neutral", "warn", "Strong coloured light. Colour may be off."),
    ("grey_gain_out_of_range", "warn", "The light is too extreme for colour correction."),
    ("skin_patch_off", "warn", "A sticker patch looks off. Colour may be off."),
    ("too_few_skin_pixels", "warn", "Skin tone unclear. Include more skin in the photo."),
)
TABLE_ORDER = tuple(code for code, _, _ in _TABLE)
LEVEL = {code: level for code, level, _ in _TABLE}
MESSAGE = {code: text for code, _, text in _TABLE}
FINDER_CODES = TABLE_ORDER[:6]
MARKER_CODES = TABLE_ORDER[6:12]


@dataclass(frozen=True)
class Finding:
    code: str
    level: str
    message: str


@dataclass(frozen=True)
class Verdict:
    level: str                     # "ok" | "warn" | "retake"
    code: str | None               # the primary finding's code, None when ok
    message: str                   # its English sentence, "" when ok
    findings: tuple[Finding, ...] = field(default_factory=tuple)  # all of them: retake before warn, then table order

    def to_dict(self) -> dict:
        return {"level": self.level, "code": self.code, "message": self.message,
                "findings": [{"code": f.code, "level": f.level, "message": f.message} for f in self.findings]}


def decide(finder_code: str | None, marker_code: str | None, skin_pixels: int | None) -> Verdict:
    """The verdict for one photo. Unknown input raises ValueError: a photo is never passed because a code was mistyped."""
    codes: list[str] = []
    if finder_code is not None:
        if finder_code not in FINDER_CODES:
            raise ValueError(f"unknown finder code {finder_code!r}")
        codes.append(finder_code)
    if marker_code:
        if marker_code not in MARKER_CODES:
            raise ValueError(f"unknown marker code {marker_code!r}")
        codes.append(marker_code)
    if skin_pixels is not None:
        if skin_pixels < 0:
            raise ValueError("skin_pixels cannot be negative")
        if skin_pixels < MIN_SKIN_PIXELS:
            codes.append("too_few_skin_pixels")
    codes.sort(key=lambda c: (LEVEL[c] != "retake", TABLE_ORDER.index(c)))
    findings = tuple(Finding(c, LEVEL[c], MESSAGE[c]) for c in codes)
    if not findings:
        return Verdict("ok", None, "")
    return Verdict(findings[0].level, findings[0].code, findings[0].message, findings)


def assess(diagnosis, marker_result, skin_pixels: int | None) -> Verdict:
    """decide() from the finder's Diagnosis (anything with a `.code`) and a marker.MarkerResult (or None when no sticker was read)."""
    return decide(diagnosis.code, None if marker_result is None else marker_result.code, skin_pixels)
