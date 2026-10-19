"""Light/dark gate policies for T55, by repeated stratified 5-fold CV on image-level features.

Policies (dark = FST IV-VI, the class the CCM is for):
  ita28     ITA of the skin mask < 28 deg  (current gate, standard cut-off)
  ita_fit   ITA < cut-off fitted on the training folds (maximises balanced accuracy)
  model     logistic regression on skin + exposure context features, P(dark) > 0.5
  agree     dark only if ita28 and model both say dark; light only if both say light;
            otherwise ABSTAIN (retake / skin type None)
  model@t   model with dark threshold t (recall-leaning operating points)
  band      model, but abstain when P(dark) is within +-w of 0.5
For each: accuracy on answered images, dark recall, light specificity, coverage (share answered).
Uses the feature cache written by fst_context.py.

Usage: .venv/bin/python scripts/fst_gate_eval.py --data ~/Documents/Color-pipeline/data/fitzpatrick17k
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import RepeatedStratifiedKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

sys.path.insert(0, str(Path(__file__).resolve().parent))
from fst_context import NAMES, load_features  # noqa: E402

ITA = NAMES.index("skin_ita")


def fit_cut(ita: np.ndarray, dark: np.ndarray) -> float:
    cands = np.arange(-10, 50, 1.0)
    bal = [((ita < c) & dark).sum() / dark.sum() / 2 + ((ita >= c) & ~dark).sum() / (~dark).sum() / 2 for c in cands]
    return float(cands[int(np.argmax(bal))])


def stats(ans: np.ndarray, call_dark: np.ndarray, dark: np.ndarray):
    n = ans.sum()
    acc = (call_dark[ans] == dark[ans]).mean()
    rec = (call_dark & dark & ans).sum() / (dark & ans).sum()
    spec = (~call_dark & ~dark & ans).sum() / (~dark & ans).sum()
    return acc, rec, spec, n / len(ans)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", type=Path, required=True)
    d = ap.parse_args().data.expanduser()
    X, y = load_features(d, d / "context_features.npz")
    dark_all = y >= 3
    res: dict[str, list] = {}
    cuts = []
    for tr, te in RepeatedStratifiedKFold(n_splits=5, n_repeats=5, random_state=0).split(X, y):
        m = make_pipeline(StandardScaler(), LogisticRegression(max_iter=3000, C=0.5)).fit(X[tr], dark_all[tr])
        p = m.predict_proba(X[te])[:, 1]
        dk, ita = dark_all[te], X[te][:, ITA]
        cut = fit_cut(X[tr][:, ITA], dark_all[tr]); cuts.append(cut)
        yes = np.ones(len(te), bool)
        pol = {"ita28": (yes, ita < 28), f"ita_fit": (yes, ita < cut), "model": (yes, p > .5)}
        a, b = ita < 28, p > .5
        pol["agree"] = (a == b, a)
        for t in (0.25, 0.35):
            pol[f"model@{t}"] = (yes, p > t)
        for w in (0.1, 0.2):
            pol[f"band ±{w}"] = (abs(p - .5) >= w, b)
        for k, (ans, call) in pol.items():
            res.setdefault(k, []).append(stats(ans, call, dk))
    print(f"{len(y)} images ({dark_all.sum()} dark); fitted ITA cut-off per fold: mean {np.mean(cuts):.1f}, range {min(cuts):.0f}..{max(cuts):.0f}\n")
    print(f"{'policy':10s} {'accuracy':>9s} {'dark recall':>12s} {'light spec':>11s} {'coverage':>9s}")
    for k, v in res.items():
        a = np.nanmean(np.array(v), axis=0)
        print(f"{k:10s} {a[0]:9.3f} {a[1]:12.3f} {a[2]:11.3f} {a[3]:9.3f}")


if __name__ == "__main__":
    main()
