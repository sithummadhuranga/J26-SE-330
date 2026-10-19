"""Write the prediction masks for the DFUC2022 leaderboard: one PNG per image, named by the image id.

The challenge asks for mask images named after the image they belong to, with 0 for background and 1 for ulcer. Each mask is
saved at the size of its photo (some test photos are portrait, 480x640) and holds all detected ulcers merged, like the
validation numbers in eval_seg.py. Check the exact file naming on the challenge's submission page before uploading: the
page text we could read says only "named using the image id".

    python make_submission.py --weights best.pt --images <DFUC2022_test_release> --out <dir> --conf 0.25
    python make_submission.py ... --gt <DFUC2022_train_masks>   score the written files against ground truth (use on val)

Run it on the test photos only after the model and the confidence threshold were chosen on validation. The output is a
prediction of licensed DFUC2022 photos, so keep it outside the git repo.
"""
import argparse
import os
import zipfile

import cv2
import numpy as np
from ultralytics import YOLO


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", required=True)
    ap.add_argument("--images", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--conf", type=float, required=True)
    ap.add_argument("--ext", default=".jpg")
    ap.add_argument("--device", default="cpu", help="cpu keeps the GPU free for training")
    ap.add_argument("--gt", help="folder of ground-truth masks (ID.png) to score the written files against")
    ap.add_argument("--zip", action="store_true", help="also write submission.zip next to the folder")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    ids = sorted(f[: -len(args.ext)] for f in os.listdir(args.images) if f.endswith(args.ext))
    model = YOLO(args.weights)
    empty = 0
    for start in range(0, len(ids), 50):  # chunks of 50: one predict() call on ~400 images returned nothing (see eval_seg.py)
        part = ids[start:start + 50]
        results = model.predict([os.path.join(args.images, i + args.ext) for i in part], imgsz=640, conf=args.conf,
                                retina_masks=True, device=args.device, verbose=False)
        assert len(results) == len(part)
        for i, r in zip(part, results):
            h, w = r.orig_shape
            mask = np.zeros((h, w), np.uint8)
            if r.masks is not None:
                mask = (r.masks.data.cpu().numpy() > 0.5).any(axis=0).astype(np.uint8)
            assert mask.shape == (h, w)
            empty += int(mask.sum() == 0)
            cv2.imwrite(os.path.join(args.out, i + ".png"), mask)

    # Read every file back and check what was written, not what was meant to be written.
    problems, areas = [], []
    for i in ids:
        m = cv2.imread(os.path.join(args.out, i + ".png"), cv2.IMREAD_UNCHANGED)
        h, w = cv2.imread(os.path.join(args.images, i + args.ext)).shape[:2]
        if m is None or m.ndim != 2 or m.dtype != np.uint8 or m.shape != (h, w) or not set(np.unique(m)) <= {0, 1}:
            problems.append(i)
        else:
            areas.append(m.mean())
    print(f"{len(ids)} masks written to {args.out}; unreadable or wrong shape/values: {len(problems)}; "
          f"images with no detection: {empty}; mean wound share of the image {100 * np.mean(areas):.2f}%")
    if problems:
        print("PROBLEMS:", problems[:10])

    if args.gt:
        dice = []
        for i in ids:
            pred = cv2.imread(os.path.join(args.out, i + ".png"), 0) > 0
            gt = cv2.imread(os.path.join(args.gt, i + ".png"), 0) > 127
            dice.append(2 * (pred & gt).sum() / (pred.sum() + gt.sum()))
        print(f"mean Dice of the written files against ground truth: {np.mean(dice):.4f} over {len(dice)} images")
    if args.zip:
        z = os.path.join(os.path.dirname(os.path.abspath(args.out)), "submission.zip")
        with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED) as zf:
            for i in ids:
                zf.write(os.path.join(args.out, i + ".png"), i + ".png")
        print("wrote", z, f"({os.path.getsize(z) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
