"""Evaluation harness (T23): fairness metrics per skin-tone stratum.

Reused in every phase: baseline (raw RGB), ablation variants and the final
pipeline are all scored with `evaluate_fairness`.

Metrics
    * AUROC per FST group (rank-based, no sklearn dependency)
    * Sensitivity / specificity per group at a fixed threshold
    * AUROC gap, sensitivity gap, specificity gap between groups
    * Equalized-odds gap: max(TPR gap, FPR gap) across groups
    * Colour Consistency Index (CCI): spread of mean skin L* across images

Targets (Fairness Metrics tab): AUROC IV-VI >= 0.88, I-III >= 0.89,
AUROC gap <= 0.01, sensitivity and specificity gaps < 0.03.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import cv2
import numpy as np

from .ita import skin_mask_hsv

FST_LABELS = ("I", "II", "III", "IV", "V", "VI")
LIGHT_GROUP = "I-III"
DARK_GROUP = "IV-VI"

TARGET_AUROC_LIGHT = 0.89
TARGET_AUROC_DARK = 0.88
TARGET_AUROC_GAP = 0.01
TARGET_RATE_GAP = 0.03


def fst_to_group(fst: str) -> str:
    """Map an FST label ('I'..'VI') to the two evaluation strata."""
    if fst not in FST_LABELS:
        raise ValueError(f"Unknown FST label: {fst!r}")
    return LIGHT_GROUP if FST_LABELS.index(fst) < 3 else DARK_GROUP


def auroc(y_true: np.ndarray, y_score: np.ndarray) -> float:
    """Area under the ROC curve via the Mann-Whitney U statistic (ties get half credit).

    Returns NaN when only one class is present.
    """
    y_true = np.asarray(y_true).astype(bool)
    y_score = np.asarray(y_score, dtype=np.float64)
    n_pos = int(y_true.sum())
    n_neg = int((~y_true).sum())
    if n_pos == 0 or n_neg == 0:
        return float("nan")
    ranks = _average_ranks(y_score)
    return float((ranks[y_true].sum() - n_pos * (n_pos + 1) / 2) / (n_pos * n_neg))


def _average_ranks(x: np.ndarray) -> np.ndarray:
    order = np.argsort(x, kind="mergesort")
    sorted_x = x[order]
    ranks = np.empty(len(x), dtype=np.float64)
    i = 0
    while i < len(x):
        j = i
        while j + 1 < len(x) and sorted_x[j + 1] == sorted_x[i]:
            j += 1
        ranks[order[i : j + 1]] = (i + j) / 2.0 + 1.0
        i = j + 1
    return ranks


def sensitivity_specificity(y_true: np.ndarray, y_score: np.ndarray, threshold: float = 0.5) -> tuple[float, float]:
    """(TPR, TNR) at `threshold`; NaN for a rate whose class is absent."""
    y_true = np.asarray(y_true).astype(bool)
    pred = np.asarray(y_score, dtype=np.float64) >= threshold
    pos, neg = y_true.sum(), (~y_true).sum()
    tpr = float((pred & y_true).sum() / pos) if pos else float("nan")
    tnr = float((~pred & ~y_true).sum() / neg) if neg else float("nan")
    return tpr, tnr


def _spread(values: list[float]) -> float:
    v = [x for x in values if not np.isnan(x)]
    return float(max(v) - min(v)) if len(v) >= 2 else float("nan")


@dataclass
class GroupMetrics:
    n: int
    n_positive: int
    auroc: float
    sensitivity: float
    specificity: float


@dataclass
class FairnessReport:
    groups: dict[str, GroupMetrics]
    auroc_gap: float
    sensitivity_gap: float
    specificity_gap: float
    equalized_odds_gap: float
    threshold: float
    meets_targets: bool | None = None
    notes: list[str] = field(default_factory=list)

    def as_row(self) -> dict[str, float | str | None]:
        """Flat dict matching the columns of the Fairness Metrics tab."""
        light, dark = self.groups.get(LIGHT_GROUP), self.groups.get(DARK_GROUP)
        return {
            "AUROC FST I-III": None if light is None else round(light.auroc, 4),
            "AUROC FST IV-VI": None if dark is None else round(dark.auroc, 4),
            "AUROC gap": round(self.auroc_gap, 4),
            "Sensitivity gap": round(self.sensitivity_gap, 4),
            "Specificity gap": round(self.specificity_gap, 4),
            "Meets targets?": {True: "Yes", False: "No", None: ""}[self.meets_targets],
        }


def evaluate_fairness(
    y_true: np.ndarray,
    y_score: np.ndarray,
    groups: np.ndarray,
    threshold: float = 0.5,
) -> FairnessReport:
    """Per-group AUROC, sensitivity, specificity and the gaps between groups.

    `groups` holds one stratum label per sample (e.g. 'I-III' / 'IV-VI', or the
    six FST labels). Gaps are max minus min across groups.
    """
    y_true = np.asarray(y_true)
    y_score = np.asarray(y_score, dtype=np.float64)
    groups = np.asarray(groups)
    if not (len(y_true) == len(y_score) == len(groups)):
        raise ValueError("y_true, y_score and groups must have the same length")

    per_group: dict[str, GroupMetrics] = {}
    for g in sorted(set(groups.tolist())):
        m = groups == g
        sens, spec = sensitivity_specificity(y_true[m], y_score[m], threshold)
        per_group[g] = GroupMetrics(
            n=int(m.sum()),
            n_positive=int(y_true[m].astype(bool).sum()),
            auroc=auroc(y_true[m], y_score[m]),
            sensitivity=sens,
            specificity=spec,
        )

    auroc_gap = _spread([g.auroc for g in per_group.values()])
    sens_gap = _spread([g.sensitivity for g in per_group.values()])
    spec_gap = _spread([g.specificity for g in per_group.values()])
    fpr_gap = spec_gap  # FPR = 1 - specificity, so its spread equals the specificity spread
    eo_gap = float(np.nanmax([sens_gap, fpr_gap])) if not (np.isnan(sens_gap) and np.isnan(fpr_gap)) else float("nan")

    report = FairnessReport(per_group, auroc_gap, sens_gap, spec_gap, eo_gap, threshold)
    light, dark = per_group.get(LIGHT_GROUP), per_group.get(DARK_GROUP)
    if light and dark and not any(np.isnan([light.auroc, dark.auroc, auroc_gap, sens_gap, spec_gap])):
        report.meets_targets = bool(
            dark.auroc >= TARGET_AUROC_DARK
            and light.auroc >= TARGET_AUROC_LIGHT
            and auroc_gap <= TARGET_AUROC_GAP
            and sens_gap < TARGET_RATE_GAP
            and spec_gap < TARGET_RATE_GAP
        )
    return report


def mean_skin_L(rgb_image: np.ndarray, min_pixels: int = 100) -> float:
    """Mean CIELAB L* (0-100) over the HSV skin mask; NaN if too few skin pixels."""
    mask = skin_mask_hsv(rgb_image)
    if mask.sum() < min_pixels:
        return float("nan")
    L = cv2.cvtColor(rgb_image, cv2.COLOR_RGB2LAB)[..., 0].astype(np.float64) * (100.0 / 255.0)
    return float(L[mask].mean())


def colour_consistency(mean_L_per_image: np.ndarray) -> float:
    """Colour Consistency Index: std dev of per-image mean skin L* (lower = more consistent).

    Definition assumed from the tracker ("L std dev reduction"); confirm with the
    supervisor before quoting it in the report.
    """
    v = np.asarray(mean_L_per_image, dtype=np.float64)
    v = v[~np.isnan(v)]
    return float(v.std(ddof=0)) if len(v) else float("nan")


def cci_reduction(before: np.ndarray, after: np.ndarray) -> float:
    """Fractional reduction in CCI after correction (0.25 = 25% less spread)."""
    b, a = colour_consistency(before), colour_consistency(after)
    return float(1.0 - a / b) if b and not np.isnan(b) else float("nan")
