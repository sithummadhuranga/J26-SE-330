"""Plain Python version of running the exported LiteRT model. The Dart code in mobile/ follows it step by step.

The exported model has no detection filtering inside it. It takes a float32 RGB image (NCHW, values 0..1) and returns:
    pred  (1, 37, 6300)       rows 0-3: box centre x, y, width, height (0..1 of the input size); row 4: ulcer score;
                              rows 5-36: the 32 mask coefficients
    proto (1, 32, H/4, W/4)   mask prototypes (logits)
Here they become masks: score filter, greedy non-max suppression, coefficients x prototypes, bilinear upsampling, crop to
the box. All of it happens in the model's input image (letterboxed to 480x640), so wound and marker pixels share one scale
and the area can be computed without going back to the full-size photo.

Needs numpy, opencv and ai-edge-litert (--compare-ultralytics also needs ultralytics):
    python litert_reference.py --model m.tflite --images <dir> --limit 50 --compare-ultralytics --dfuc-train <DFUC root>
    python litert_reference.py --model m.tflite --images <dir> --ids a,b,c --golden-out <dir>   (test files for the Dart code)
"""
import argparse
import json
import os

import cv2
import numpy as np

INPUT_H, INPUT_W = 480, 640
CONF, IOU, MAX_DET = 0.25, 0.7, 300


def letterbox(rgb, size=(INPUT_H, INPUT_W), pad_value=114):
    """Fit an RGB image into size (h, w) keeping its aspect ratio, as Ultralytics' LetterBox does with centre padding.
    Shrinking uses area averaging (Ultralytics uses bilinear, which aliases on 12-megapixel phone photos); enlarging is
    bilinear. Photos that are already 640x480 are not resized, so nothing changes for the DFUC images.
    Returns the canvas, the scale used, and the (left, top) padding."""
    h0, w0 = rgb.shape[:2]
    r = min(size[0] / h0, size[1] / w0)
    new_w, new_h = round(w0 * r), round(h0 * r)
    dw, dh = (size[1] - new_w) / 2, (size[0] - new_h) / 2
    if (w0, h0) != (new_w, new_h):
        rgb = cv2.resize(rgb, (new_w, new_h), interpolation=cv2.INTER_AREA if r < 1 else cv2.INTER_LINEAR)
    top, bottom = round(dh - 0.1), round(dh + 0.1)
    left, right = round(dw - 0.1), round(dw + 0.1)
    canvas = cv2.copyMakeBorder(rgb, top, bottom, left, right, cv2.BORDER_CONSTANT, value=(pad_value,) * 3)
    return canvas, r, (left, top)


def to_input(canvas):
    return np.ascontiguousarray((canvas.astype(np.float32) / 255.0).transpose(2, 0, 1)[None])


def iou_one_to_many(box, others):
    x1 = np.maximum(box[0], others[:, 0])
    y1 = np.maximum(box[1], others[:, 1])
    x2 = np.minimum(box[2], others[:, 2])
    y2 = np.minimum(box[3], others[:, 3])
    inter = np.clip(x2 - x1, 0, None) * np.clip(y2 - y1, 0, None)
    area = (box[2] - box[0]) * (box[3] - box[1])
    areas = (others[:, 2] - others[:, 0]) * (others[:, 3] - others[:, 1])
    return inter / (area + areas - inter + 1e-9)


def decode(pred, conf=CONF, iou=IOU, max_det=MAX_DET, size=(INPUT_H, INPUT_W)):
    """Score filter + greedy NMS. Returns boxes (n,4) xyxy in input pixels, scores (n,), coefficients (n,32)."""
    p = pred[0]
    score = p[4]
    keep = np.where(score >= conf)[0]
    if keep.size == 0:
        return np.zeros((0, 4), np.float32), np.zeros(0, np.float32), np.zeros((0, 32), np.float32)
    h, w = size
    cx, cy, bw, bh = p[0, keep] * w, p[1, keep] * h, p[2, keep] * w, p[3, keep] * h
    boxes = np.stack([cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2], 1)
    s, coef = score[keep], p[5:37, keep].T
    order = np.argsort(-s, kind="stable")
    chosen = []
    while order.size and len(chosen) < max_det:
        i = order[0]
        chosen.append(i)
        order = order[1:]
        if order.size:
            order = order[iou_one_to_many(boxes[i], boxes[order]) <= iou]
    chosen = np.array(chosen)
    return boxes[chosen].astype(np.float32), s[chosen].astype(np.float32), coef[chosen].astype(np.float32)


def instance_masks(proto, coef, boxes, size=(INPUT_H, INPUT_W)):
    """(n, H, W) uint8 masks in input space: coefficients @ prototypes, bilinear upsample, > 0, crop to the box."""
    h, w = size
    n = coef.shape[0]
    if n == 0:
        return np.zeros((0, h, w), np.uint8)
    c, mh, mw = proto.shape[1:]
    logits = (coef @ proto[0].reshape(c, -1)).reshape(n, mh, mw)
    xs, ys = np.arange(w, dtype=np.float32)[None, :], np.arange(h, dtype=np.float32)[:, None]
    out = np.zeros((n, h, w), np.uint8)
    for k in range(n):
        up = cv2.resize(logits[k], (w, h), interpolation=cv2.INTER_LINEAR)
        x1, y1, x2, y2 = boxes[k]
        out[k] = ((up > 0) & (xs >= x1) & (xs < x2) & (ys >= y1) & (ys < y2)).astype(np.uint8)
    return out


def segment(interpreter, rgb, conf=CONF):
    canvas, r, pad = letterbox(rgb)
    ins, outs = interpreter.get_input_details(), interpreter.get_output_details()
    interpreter.set_tensor(ins[0]["index"], to_input(canvas))
    interpreter.invoke()
    t = [interpreter.get_tensor(o["index"]) for o in outs]
    pred, proto = (t[0], t[1]) if t[0].ndim == 3 else (t[1], t[0])
    boxes, scores, coef = decode(pred, conf)
    masks = instance_masks(proto, coef, boxes)
    return dict(canvas=canvas, ratio=r, pad=pad, pred=pred, proto=proto, boxes=boxes, scores=scores, masks=masks,
                union=masks.any(axis=0).astype(np.uint8) if len(masks) else np.zeros(canvas.shape[:2], np.uint8))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--images", required=True)
    ap.add_argument("--limit", type=int, default=50)
    ap.add_argument("--ids", help="comma-separated image ids, overrides --limit")
    ap.add_argument("--golden-out")
    ap.add_argument("--compare-ultralytics", action="store_true")
    ap.add_argument("--dfuc-train")
    args = ap.parse_args()

    from ai_edge_litert.interpreter import Interpreter
    it = Interpreter(model_path=args.model)
    it.allocate_tensors()
    ids = sorted(f[:-4] for f in os.listdir(args.images) if f.endswith(".jpg"))[: args.limit]
    if args.ids:
        ids = args.ids.split(",")
    ul = None
    if args.compare_ultralytics:
        from ultralytics import YOLO
        ul = YOLO(args.model)
    if args.golden_out:
        os.makedirs(args.golden_out, exist_ok=True)

    agree, d_ref, d_ul, manifest = [], [], [], []
    for i in ids:
        bgr = cv2.imread(os.path.join(args.images, i + ".jpg"))
        res = segment(it, cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
        rec = dict(id=i, n_det=int(len(res["scores"])))
        if args.dfuc_train:
            gt = cv2.imread(os.path.join(args.dfuc_train, "DFUC2022_train_masks", i + ".png"), 0) > 127
            u = res["union"].astype(bool)
            d_ref.append(2 * (u & gt).sum() / max(u.sum() + gt.sum(), 1))
        if ul is not None:
            r = ul.predict(os.path.join(args.images, i + ".jpg"), imgsz=640, conf=CONF, retina_masks=True,
                           device="cpu", verbose=False)[0]
            um = (r.masks.data.cpu().numpy() > 0.5).any(axis=0) if r.masks is not None else np.zeros_like(gt)
            inter, tot = (res["union"].astype(bool) & um).sum(), (res["union"].astype(bool) | um).sum()
            agree.append(1.0 if tot == 0 else inter / tot)  # both pipelines found nothing = full agreement
            rec["n_det_ultralytics"] = 0 if r.boxes is None else int(len(r.boxes))
            if args.dfuc_train:
                d_ul.append(2 * (um & gt).sum() / max(um.sum() + gt.sum(), 1))
        if args.golden_out:
            res["pred"].astype("<f4").tofile(os.path.join(args.golden_out, i + ".pred.f32"))
            res["proto"].astype("<f4").tofile(os.path.join(args.golden_out, i + ".proto.f32"))
            cv2.imwrite(os.path.join(args.golden_out, i + ".union.png"), res["union"] * 255)
            res["union"].astype(np.uint8).tofile(os.path.join(args.golden_out, i + ".union.u8"))  # row-major 0/1 bytes
            res["canvas"].astype(np.uint8).tofile(os.path.join(args.golden_out, i + ".rgb.u8"))  # RGB, row-major HWC, as decoded here
            cv2.imwrite(os.path.join(args.golden_out, i + ".jpg"), bgr)
            rec.update(boxes=res["boxes"].round(3).tolist(), scores=res["scores"].round(5).tolist(),
                       union_pixels=int(res["union"].sum()))
            manifest.append(rec)
    if args.golden_out:
        json.dump(dict(model=os.path.basename(args.model), conf=CONF, iou=IOU, input_hw=[INPUT_H, INPUT_W],
                       pred_shape=list(res["pred"].shape), proto_shape=list(res["proto"].shape), images=manifest),
                  open(os.path.join(args.golden_out, "manifest.json"), "w"), indent=1)
    print(f"{len(ids)} images")
    if agree:
        a = np.array(agree)
        print(f"reference vs Ultralytics union mask IoU: mean {a.mean():.5f}, min {a.min():.5f}, "
              f"images below 0.99: {(a < 0.99).sum()}")
    if d_ref:
        print(f"mean Dice vs ground truth: reference {np.mean(d_ref):.4f}" +
              (f", Ultralytics {np.mean(d_ul):.4f}" if d_ul else ""))


if __name__ == "__main__":
    main()
