"""CalibrationPipeline: the module's public API (proposal Table 3, SO1, FR9, FR11, NFR7).

    pipeline = CalibrationPipeline()                         # all seven stages on
    result = pipeline.process(rgb, wound_bbox=(x, y, w, h))
    result.calibrated_image    # uint8 RGB, same height/width as the input
    result.fitzpatrick_class   # "I".."VI" or None
    result.stage_timing        # ms per stage
    result.model_tensor        # float32 (512, 512, 3), letterboxed + normalised (stage 7)
    result.to_json()           # analytics fields for the wound analytics JSON (FR8)

Turning stages off (ablation without code changes):
    CalibrationPipeline(enabled_stages=ALL_STAGES - {PipelineStage.CCM})

Differences from the proposal's Table 3, both agreed with Member 1 (docs/interface_spec.md):
the calibrated image keeps the input's height and width, and the 512x512 tensor is
letterboxed (uniform scale, black borders) rather than stretched, with `letterbox_info`
to map masks back. Names are snake_case here; the Dart/C++ port uses the camelCase ones.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field, replace
from enum import Enum

import numpy as np

from .pipeline import PipelineConfig, process_image
from .preprocess import LetterboxInfo, final_normalise, letterbox


class PipelineStage(str, Enum):
    INPUT_NORMALISATION = "1_input_normalisation"
    ILLUMINATION = "2_illumination"
    SKIN_TONE = "3_skin_tone"
    CCM = "4_ccm"
    HIGHLIGHT_REMOVAL = "5_highlight_removal"
    CONTRAST = "6_contrast"
    FINAL_NORMALISATION = "7_final_normalisation"


ALL_STAGES: frozenset[PipelineStage] = frozenset(PipelineStage)


def config_from_stages(enabled: set[PipelineStage] | frozenset[PipelineStage], base: PipelineConfig | None = None) -> PipelineConfig:
    """PipelineConfig with stages 1-6 switched by `enabled`; other settings come from `base`.

    Stage 2 uses `base.illumination` when on ("msr" by default) and "none" when off.
    The CCM needs a skin-tone estimate, so it only runs if SKIN_TONE is also on.
    Stage 7 is handled by CalibrationPipeline, not process_image.
    """
    base = base or PipelineConfig()
    S = PipelineStage
    return replace(
        base,
        stretch=S.INPUT_NORMALISATION in enabled,
        illumination=(base.illumination if base.illumination != "none" else "msr") if S.ILLUMINATION in enabled else "none",
        use_white_balance=base.use_white_balance and S.ILLUMINATION in enabled,
        classify_skin=S.SKIN_TONE in enabled,
        use_ccm=S.CCM in enabled,
        use_specular=S.HIGHLIGHT_REMOVAL in enabled,
        use_clahe=S.CONTRAST in enabled,
    )


def stages_of(config: PipelineConfig) -> frozenset[PipelineStage]:
    """Stages 1-6 that `config` has switched on (the reverse of `config_from_stages`)."""
    S = PipelineStage
    on = {
        S.INPUT_NORMALISATION: config.stretch,
        S.ILLUMINATION: config.illumination != "none",
        S.SKIN_TONE: config.classify_skin,
        S.CCM: config.use_ccm,
        S.HIGHLIGHT_REMOVAL: config.use_specular,
        S.CONTRAST: config.use_clahe,
    }
    return frozenset(s for s, v in on.items() if v)


@dataclass
class CalibrationResult:
    calibrated_image: np.ndarray            # uint8 RGB, same shape as the input
    fitzpatrick_class: str | None           # "I".."VI"; None if skin tone was not estimated
    ita_angle: float | None
    ccm_applied: bool
    stage_timing: dict[str, float]          # ms; keys as in ProcessResult plus "7_final", "total"
    model_tensor: np.ndarray | None = None  # float32 (size, size, 3) when stage 7 is on
    letterbox_info: LetterboxInfo | None = None
    notes: list[str] = field(default_factory=list)

    def to_json(self) -> dict:
        """Fields for the wound analytics JSON (FR8); no image data (NFR5)."""
        return {
            "fitzpatrick_class": self.fitzpatrick_class,
            "ita_angle": None if self.ita_angle is None else round(self.ita_angle, 2),
            "ccm_applied": self.ccm_applied,
            "stage_timing_ms": {k: round(v, 2) for k, v in self.stage_timing.items()},
            "notes": list(self.notes),
        }


class CalibrationPipeline:
    """Configured calibration module. Defaults to all seven stages on."""

    def __init__(
        self,
        config: PipelineConfig | None = None,
        enabled_stages: set[PipelineStage] | frozenset[PipelineStage] | None = None,
        model_size: int = 512,
        normalise_mode: str = "imagenet",
    ):
        if normalise_mode not in ("imagenet", "zscore"):
            raise ValueError(f"unknown normalise_mode {normalise_mode!r} (use 'imagenet' or 'zscore')")
        if enabled_stages is None:
            enabled_stages = ALL_STAGES if config is None else stages_of(config) | {PipelineStage.FINAL_NORMALISATION}
        unknown = set(enabled_stages) - ALL_STAGES
        if unknown:
            raise ValueError(f"unknown stages {unknown}")
        self.enabled_stages = frozenset(enabled_stages)
        self.config = config_from_stages(self.enabled_stages, config)
        self.model_size = model_size
        self.normalise_mode = normalise_mode

    def process(self, image: np.ndarray, wound_bbox: tuple[int, int, int, int] | None = None) -> CalibrationResult:
        """Run the enabled stages on one uint8 RGB image.

        `wound_bbox` is (x, y, w, h) in the input's pixel coordinates (FR1); it is kept out
        of the skin sample used for ITA and the CCM.
        """
        res = process_image(image, self.config, wound_bbox=wound_bbox)
        timing = {k: v for k, v in res.timings_ms.items() if k != "total"}
        tensor, info = None, None
        if PipelineStage.FINAL_NORMALISATION in self.enabled_stages:
            t0 = time.perf_counter()
            boxed, info = letterbox(res.image, self.model_size)
            tensor = final_normalise(boxed, self.normalise_mode)
            timing["7_final"] = (time.perf_counter() - t0) * 1000.0
        timing["total"] = sum(timing.values())
        return CalibrationResult(
            calibrated_image=res.image,
            fitzpatrick_class=res.fst,
            ita_angle=res.ita_angle,
            ccm_applied=res.ccm_applied,
            stage_timing=timing,
            model_tensor=tensor,
            letterbox_info=info,
            notes=list(res.notes),
        )
