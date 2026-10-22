"""Write the expected outputs the C++ port is checked against (native/tests/parity_test.cpp).

Python is the reference: every case here is made by the Python modules on the synthetic golden photos
(tests/golden/photos, no licensed data). The files go to a build folder, not into git.

Usage: .venv/bin/python scripts/make_native_fixtures.py native/build/fixtures [device_dir]

With device_dir the manifest names inputs as <device_dir>/photos/<name>.png and expected files as
<device_dir>/<file>, for running the test on a phone or emulator (build_android.sh copies the files).

manifest.tsv, one case per line, tab separated:
    kind  name  input.png  expected  key=value ...
kinds: stage_* (one function on the photo), ita (skin tone numbers), tensor (stage 7, float32 raw
file), pipeline (a whole ablation variant; stage 7 off).
"""
from __future__ import annotations

import sys
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))
from colorimetric.api import PipelineStage as S, stages_of  # noqa: E402
from colorimetric.enhance import clahe_median, remove_specular  # noqa: E402
from colorimetric.illumination import gray_world, multi_scale_retinex_L, single_scale_retinex_L  # noqa: E402
from colorimetric.ita import classify_image  # noqa: E402
from colorimetric.pipeline import ABLATION_VARIANTS, process_image  # noqa: E402
from colorimetric.preprocess import final_normalise, letterbox, process_raw_input  # noqa: E402

PHOTOS = ROOT / "tests" / "golden" / "photos"
VARIANTS = ["none", "gray_world", "ssr", "retinex", "retinex_ccm", "full", "full_ccm_v1", "full_ccm_unstratified"]
ILLUM = {"none": 0, "msr": 1, "ssr": 2, "gray_world": 3}
STAGE_BITS = [S.INPUT_NORMALISATION, S.ILLUMINATION, S.SKIN_TONE, S.CCM, S.HIGHLIGHT_REMOVAL, S.CONTRAST]


def write_png(path: Path, rgb: np.ndarray) -> None:
    cv2.imwrite(str(path), cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))


def main(out: Path, device_dir: str | None = None) -> None:
    out.mkdir(parents=True, exist_ok=True)
    rows = []

    def where(p: Path, photo: bool = False) -> str:
        if device_dir is None:
            return str(p)
        return f"{device_dir}/photos/{p.name}" if photo else f"{device_dir}/{p.name}"

    for photo in sorted(PHOTOS.glob("*.png")):
        name = photo.stem
        rgb = cv2.cvtColor(cv2.imread(str(photo)), cv2.COLOR_BGR2RGB)
        inp = where(photo, photo=True)

        for kind, fn in [("stage_raw_input", process_raw_input), ("stage_msr", multi_scale_retinex_L),
                         ("stage_ssr", single_scale_retinex_L), ("stage_gray_world", gray_world),
                         ("stage_specular", remove_specular), ("stage_clahe", clahe_median)]:
            exp = out / f"{kind}.{name}.png"
            write_png(exp, fn(rgb))
            rows.append([kind, name, inp, where(exp)])

        try:
            r = classify_image(rgb)
            rows.append(["ita", name, inp, "-", f"angle={r.ita_angle!r}", f"fst={r.fitzpatrick_type.value}", f"skin={r.skin_pixel_count}"])
        except ValueError:
            rows.append(["ita", name, inp, "-", "angle=none", "fst=none", "skin=0"])

        for mode in ("imagenet", "zscore"):
            exp = out / f"tensor_{mode}.{name}.f32"
            final_normalise(letterbox(rgb, 512)[0], mode).astype("<f4").tofile(exp)
            rows.append(["tensor", name, inp, where(exp), f"mode={mode}", "size=512"])

        for v in VARIANTS:
            cfg = replace(ABLATION_VARIANTS[v], check_agreement=False)
            res = process_image(rgb, cfg)
            exp = out / f"pipeline_{v}.{name}.png"
            write_png(exp, res.image)
            on = stages_of(cfg)
            mask = sum(1 << i for i, s in enumerate(STAGE_BITS) if s in on)
            rows.append(["pipeline", name, inp, where(exp), f"variant={v}", f"stages={mask}", f"illum={ILLUM[cfg.illumination]}",
                         f"fixed={int(cfg.ccm_shift == 'fixed')}", f"strat={int(cfg.ccm_stratified)}",
                         f"lvar={int(cfg.ccm_l_variance)}", f"cutoff={cfg.gate_cutoff}",
                         f"fst={res.fst or 'none'}", f"ccm={int(res.ccm_applied)}"])
    (out / "manifest.tsv").write_text("\n".join("\t".join(r) for r in rows) + "\n")
    print(f"{len(rows)} cases -> {out}")


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / "native" / "build" / "fixtures"), sys.argv[2] if len(sys.argv) > 2 else None)
