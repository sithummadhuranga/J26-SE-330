"""process_image(): chain the calibration steps into one call (T43).

Order (research guide, adjusted where measurement showed a problem):
    1  raw input processing     intensity stretch + artefact smoothing
    2  illumination correction  multi-scale Retinex on L* (optional white balance)
    3  skin tone (ITA) + gate   read from the RAW input, see `classify_on`
    4  adaptive 3x3 CCM         only if the gate says IV-VI
    5  specular removal         inpaint V>240 & S<30
    6  CLAHE on L* + median     contrast
Step 7 (model normalisation) and the resize are model-side, not part of calibration.

The output has exactly the input height and width (agreed with Member 1).

Why ITA is read from the raw image: Step 1 and MSR shift ITA by about 13-14 degrees
on average and white balance by about 26 degrees (measured on 50 ISIC images), but the
gate cut-offs were fitted on raw photos. `classify_on="processed"` keeps the guide's
literal order for comparison.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field, replace

import cv2
import numpy as np

from .ccm import apply_adaptive_ccm, estimate_adaptive_ccm
from .enhance import clahe_median, remove_specular
from .illumination import gray_world, multi_scale_retinex_L, white_balance
from .ita import GATE_CUTOFF_FITTED, GATE_CUTOFF_STANDARD, classify_image, needs_ccm
from .preprocess import _check_rgb, process_raw_input


@dataclass(frozen=True)
class PipelineConfig:
    stretch: bool = True               # step 1
    illumination: str = "msr"          # step 2: "msr" | "gray_world" | "none"
    use_white_balance: bool = False    # step 2 extra; off by default (see module docstring)
    use_ccm: bool = True               # step 4
    gate_cutoff: float = GATE_CUTOFF_STANDARD
    classify_on: str = "raw"           # "raw" | "processed"
    use_specular: bool = True          # step 5
    use_clahe: bool = True             # step 6
    ccm_k: int = 6
    seed: int = 0                      # k-means seeding, for repeatable output


@dataclass
class ProcessResult:
    image: np.ndarray                  # uint8 RGB, same shape as the input
    ita_angle: float | None            # None if too few skin pixels
    fst: str | None
    ccm_applied: bool
    timings_ms: dict[str, float] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)


# Ablation variants for T62: none > Gray World > Retinex > Retinex+CCM > full.
ABLATION_VARIANTS: dict[str, PipelineConfig] = {
    "none": PipelineConfig(stretch=False, illumination="none", use_ccm=False, use_specular=False, use_clahe=False),
    "gray_world": PipelineConfig(stretch=False, illumination="gray_world", use_ccm=False, use_specular=False, use_clahe=False),
    "retinex": PipelineConfig(illumination="msr", use_ccm=False, use_specular=False, use_clahe=False),
    "retinex_ccm": PipelineConfig(illumination="msr", use_ccm=True, use_specular=False, use_clahe=False),
    "full": PipelineConfig(),
    "full_fitted_gate": PipelineConfig(gate_cutoff=GATE_CUTOFF_FITTED),
}


def process_image(
    rgb: np.ndarray,
    config: PipelineConfig | None = None,
    wound_bbox: tuple[int, int, int, int] | None = None,
) -> ProcessResult:
    """Run the calibration pipeline on one uint8 RGB image; shape in == shape out.

    `wound_bbox` is (x, y, w, h) in the input's own pixel coordinates (unchanged
    geometry means the box stays valid), excluded when sampling skin.
    """
    _check_rgb(rgb)
    cfg = config or PipelineConfig()
    if cfg.illumination not in ("msr", "gray_world", "none"):
        raise ValueError(f"unknown illumination {cfg.illumination!r}")
    if cfg.classify_on not in ("raw", "processed"):
        raise ValueError(f"unknown classify_on {cfg.classify_on!r}")

    timings: dict[str, float] = {}
    notes: list[str] = []

    def timed(name, fn, *args, **kwargs):
        t0 = time.perf_counter()
        out = fn(*args, **kwargs)
        timings[name] = (time.perf_counter() - t0) * 1000.0
        return out

    def classify(img):
        try:
            return classify_image(img, wound_bbox=wound_bbox)
        except ValueError as e:
            notes.append(f"skin tone not estimated: {e}")
            return None

    ita = timed("ita", classify, rgb) if cfg.classify_on == "raw" else None

    img = rgb
    if cfg.stretch:
        img = timed("1_raw_input", process_raw_input, img)
    if cfg.illumination == "msr":
        img = timed("2_msr", multi_scale_retinex_L, img)
    elif cfg.illumination == "gray_world":
        img = timed("2_gray_world", gray_world, img)
    if cfg.use_white_balance:
        img = timed("2_white_balance", white_balance, img)

    if cfg.classify_on == "processed":
        ita = timed("ita", classify, img)

    ccm_applied = False
    if cfg.use_ccm:
        if ita is None:
            notes.append("CCM skipped: no skin-tone estimate")
        elif needs_ccm(ita.ita_angle, cfg.gate_cutoff):
            cv2.setRNGSeed(cfg.seed)
            try:
                M = timed("4_ccm_fit", estimate_adaptive_ccm, img, ita.fitzpatrick_type,
                          wound_bbox=wound_bbox, k=cfg.ccm_k, correct=True)
                img = timed("4_ccm_apply", apply_adaptive_ccm, img, M)
                ccm_applied = True
            except ValueError as e:
                notes.append(f"CCM skipped: {e}")

    if cfg.use_specular:
        img = timed("5_specular", remove_specular, img)
    if cfg.use_clahe:
        img = timed("6_clahe", clahe_median, img)

    timings["total"] = sum(v for k, v in timings.items() if k != "total")
    return ProcessResult(
        image=img,
        ita_angle=None if ita is None else ita.ita_angle,
        fst=None if ita is None else ita.fitzpatrick_type.value,
        ccm_applied=ccm_applied,
        timings_ms=timings,
        notes=notes,
    )


def with_gate(config: PipelineConfig, cutoff: float) -> PipelineConfig:
    """Copy of `config` using a different gate cut-off."""
    return replace(config, gate_cutoff=cutoff)
