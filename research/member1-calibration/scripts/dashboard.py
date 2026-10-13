#!/usr/bin/env python3
"""Local review dashboard for the colorimetric pipeline.

Three tabs:
  1. FST Manual Review — page through the ISIC subset, compare the ITA
     auto-label against your own judgment, save overrides. This is the
     Phase 1 "manual spot-check" task from the research guide.
  2. CCM Preview — pick any image, see the adaptive CCM correction
     (Step 4) applied side-by-side with the original.
  3. Test Suite — run pytest against the project and see results without
     leaving the browser.

Run:
    streamlit run scripts/dashboard.py
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
import pandas as pd
import streamlit as st

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from colorimetric.ccm import apply_adaptive_ccm, estimate_adaptive_ccm  # noqa: E402
from colorimetric.ita import FitzpatrickType, classify_image  # noqa: E402

IMAGES_DIR = ROOT / "data" / "isic_subset"
ITA_RESULTS_CSV = ROOT / "data" / "annotations" / "ita_results.csv"
METADATA_CSV = ROOT / "data" / "annotations" / "isic_subset_metadata.csv"
MANUAL_REVIEW_CSV = ROOT / "data" / "annotations" / "manual_fst_review.csv"

FST_OPTIONS = ["(not reviewed)"] + [t.value for t in FitzpatrickType]

st.set_page_config(page_title="Colorimetric Pipeline Dashboard", layout="wide")


@st.cache_data
def load_data() -> pd.DataFrame:
    ita_df = pd.read_csv(ITA_RESULTS_CSV) if ITA_RESULTS_CSV.exists() else pd.DataFrame()
    meta_df = pd.read_csv(METADATA_CSV) if METADATA_CSV.exists() else pd.DataFrame()

    if ita_df.empty:
        return pd.DataFrame()

    df = ita_df.merge(
        meta_df[["filename", "sex", "age_approx", "anatom_site", "diagnosis"]],
        on="filename",
        how="left",
    )

    if MANUAL_REVIEW_CSV.exists():
        manual_df = pd.read_csv(MANUAL_REVIEW_CSV)
        df = df.merge(manual_df, on="filename", how="left")
    else:
        df["manual_fst"] = np.nan
        df["notes"] = ""

    return df


def save_manual_label(filename: str, manual_fst: str, notes: str) -> None:
    if MANUAL_REVIEW_CSV.exists():
        manual_df = pd.read_csv(MANUAL_REVIEW_CSV)
        manual_df = manual_df[manual_df["filename"] != filename]
    else:
        manual_df = pd.DataFrame(columns=["filename", "manual_fst", "notes"])

    new_row = pd.DataFrame([{"filename": filename, "manual_fst": manual_fst, "notes": notes}])
    manual_df = pd.concat([manual_df, new_row], ignore_index=True)
    manual_df.to_csv(MANUAL_REVIEW_CSV, index=False)
    st.cache_data.clear()


tab1, tab2, tab3 = st.tabs(["FST Manual Review", "CCM Preview", "Test Suite"])

with tab1:
    st.header("FST Manual Review")
    st.caption(
        "Phase 1 spot-check: compare the ITA auto-label against your own judgment. "
        "Watch for a systematic offset (Benčević et al. found ITA needed a -10° "
        "correction against their ground truth) — see docs/literature_review.md."
    )

    df = load_data()
    if df.empty:
        st.warning(
            f"No data found. Run `python scripts/download_isic_subset.py` and "
            f"`python scripts/validate_ita.py --images {IMAGES_DIR} --out {ITA_RESULTS_CSV}` first."
        )
    else:
        col_filter, col_stats = st.columns([2, 3])
        with col_filter:
            fst_filter = st.multiselect(
                "Filter by auto FST", options=sorted(df["fitzpatrick_type"].unique()), default=[]
            )
            only_unreviewed = st.checkbox("Only show unreviewed", value=False)

        filtered = df.copy()
        if fst_filter:
            filtered = filtered[filtered["fitzpatrick_type"].isin(fst_filter)]
        if only_unreviewed:
            filtered = filtered[filtered["manual_fst"].isna()]
        filtered = filtered.reset_index(drop=True)

        with col_stats:
            reviewed_count = df["manual_fst"].notna().sum()
            st.metric("Reviewed", f"{reviewed_count} / {len(df)}")
            agree = df[df["manual_fst"].notna()]
            if len(agree) > 0:
                match_rate = (agree["manual_fst"] == agree["fitzpatrick_type"]).mean()
                st.metric("Auto/manual agreement", f"{match_rate:.0%}")

        if len(filtered) == 0:
            st.info("No images match the current filter.")
        else:
            idx = st.number_input(
                "Image index", min_value=0, max_value=len(filtered) - 1, value=0, step=1
            )
            row = filtered.iloc[int(idx)]

            img_col, info_col = st.columns([1, 1])
            with img_col:
                img_path = IMAGES_DIR / row["filename"]
                if img_path.exists():
                    st.image(str(img_path), caption=row["filename"], width=400)
                else:
                    st.error(f"Image not found: {img_path}")

            with info_col:
                st.subheader(row["filename"])
                st.write(f"**Auto ITA angle:** {row['ita_angle']:.2f}°")
                st.write(f"**Auto FST:** {row['fitzpatrick_type']}")
                st.write(f"**Correction strength:** {row['correction_strength']}")
                st.write(f"**Skin pixels sampled:** {int(row['skin_pixel_count'])}")
                st.write(
                    f"**Metadata:** sex={row.get('sex', '-')}, age={row.get('age_approx', '-')}, "
                    f"site={row.get('anatom_site', '-')}, diagnosis={row.get('diagnosis', '-')}"
                )

                current_manual = row["manual_fst"] if pd.notna(row["manual_fst"]) else "(not reviewed)"
                manual_choice = st.selectbox(
                    "Your FST judgment",
                    options=FST_OPTIONS,
                    index=FST_OPTIONS.index(current_manual) if current_manual in FST_OPTIONS else 0,
                    key=f"fst_{row['filename']}",
                )
                notes = st.text_input(
                    "Notes (optional)",
                    value=row["notes"] if pd.notna(row.get("notes")) else "",
                    key=f"notes_{row['filename']}",
                )

                if st.button("Save review", key=f"save_{row['filename']}"):
                    save_manual_label(
                        row["filename"],
                        manual_choice if manual_choice != "(not reviewed)" else "",
                        notes,
                    )
                    st.success("Saved.")
                    st.rerun()

with tab2:
    st.header("CCM Preview")
    st.caption("Pick an image and preview the Step 4 adaptive color correction.")

    if not IMAGES_DIR.exists() or not any(IMAGES_DIR.iterdir()):
        st.warning(f"No images in {IMAGES_DIR}. Run the download script first.")
    else:
        filenames = sorted(p.name for p in IMAGES_DIR.iterdir() if p.suffix.lower() in (".jpg", ".jpeg", ".png"))
        selected = st.selectbox("Image", filenames)

        bgr = cv2.imread(str(IMAGES_DIR / selected))
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)

        try:
            result = classify_image(rgb)
            st.write(f"Detected FST: **{result.fitzpatrick_type.value}** (ITA={result.ita_angle:.2f}°)")

            force_correction = st.checkbox("Force correction even if FST I-III", value=False)
            fst_for_ccm = result.fitzpatrick_type
            if force_correction and fst_for_ccm in (FitzpatrickType.I, FitzpatrickType.II, FitzpatrickType.III):
                fst_for_ccm = FitzpatrickType.VI  # force the gate open for preview purposes

            M = estimate_adaptive_ccm(rgb, fst_for_ccm)
            col_orig, col_corrected = st.columns(2)
            with col_orig:
                st.image(rgb, caption="Original", width=400)
            with col_corrected:
                if M is None:
                    st.info("FST I-III — no correction applied per pipeline spec.")
                    st.image(rgb, caption="Unchanged", width=400)
                else:
                    corrected = apply_adaptive_ccm(rgb, M)
                    st.image(corrected, caption="CCM-corrected", width=400)
                    st.code(np.array2string(M, precision=3, suppress_small=True), language="text")
        except ValueError as e:
            st.error(f"Could not classify image: {e}")

with tab3:
    st.header("Test Suite")
    st.caption("Runs `pytest -v` against the project.")

    if st.button("Run tests"):
        with st.spinner("Running pytest..."):
            result = subprocess.run(
                [sys.executable, "-m", "pytest", "-v"],
                cwd=str(ROOT),
                capture_output=True,
                text=True,
            )
        if result.returncode == 0:
            st.success("All tests passed.")
        else:
            st.error(f"Tests failed (exit code {result.returncode}).")
        st.code(result.stdout + "\n" + result.stderr, language="text")
