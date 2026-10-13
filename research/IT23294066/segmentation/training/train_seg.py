"""Train YOLO11n-seg on the DFUC2022 split made by data-preparation/ (same script locally and on Colab).

    python train_seg.py --data <yolo_dfuc>/data.yaml --name baseline_raw --epochs 100

Every setting is an argument and is saved next to the weights (args.json), so a run can be repeated. The `proposal`
augmentation preset follows section 5.2 of the proposal: rotation up to 15 degrees, horizontal and vertical flips,
brightness up to 10%. Hue and saturation jitter are off because the colour-region analysis needs true colours.
`default` is Ultralytics' own preset, for comparison.
"""
import argparse
import json
import os

import torch
from ultralytics import YOLO, __version__

AUG = {
    "proposal": dict(degrees=15.0, fliplr=0.5, flipud=0.5, hsv_h=0.0, hsv_s=0.0, hsv_v=0.1),
    "default": {},
}


def pick_device():
    if torch.cuda.is_available():
        return 0
    if torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--model", default="yolo11n-seg.pt")
    ap.add_argument("--name", required=True)
    ap.add_argument("--project", default="runs", help="keep outside the git repository")
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--patience", type=int, default=30)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--fraction", type=float, default=1.0, help="share of the training set to use (smoke tests)")
    ap.add_argument("--aug", choices=AUG, default="proposal")
    ap.add_argument("--device", default=None)
    ap.add_argument("--workers", type=int, default=4)
    args = ap.parse_args()

    device = args.device if args.device is not None else pick_device()
    train_args = dict(
        data=args.data, epochs=args.epochs, imgsz=args.imgsz, batch=args.batch, patience=args.patience,
        seed=args.seed, deterministic=True, fraction=args.fraction, device=device, workers=args.workers,
        project=os.path.abspath(args.project), name=args.name, exist_ok=False, plots=True, **AUG[args.aug],
    )
    model = YOLO(args.model)
    results = model.train(**train_args)

    meta = dict(train_args, model=args.model, aug_preset=args.aug, ultralytics=__version__, torch=torch.__version__)
    with open(os.path.join(str(results.save_dir), "args.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print("saved to", results.save_dir)


if __name__ == "__main__":
    main()
