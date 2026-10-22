import 'dart:math' as math;
import 'dart:typed_data';

/// Turns the raw output of the exported YOLO11n-seg model into ulcer masks: score filter, non-max suppression and mask
/// assembly. It does the same steps as research/IT23294066/on-device-export/litert_reference.py.
///
/// Everything stays in the 480x640 input image, so wound pixels and marker pixels share one scale.

const int kInputH = 480;
const int kInputW = 640;
const int kAnchors = 6300;
const int kMaskDim = 32;
const int kProtoH = 120;
const int kProtoW = 160;

/// One detected ulcer: its box (in input pixels), score and 32 mask coefficients.
class SegDetection {
  const SegDetection(this.x1, this.y1, this.x2, this.y2, this.score, this.coefficients);

  final double x1, y1, x2, y2, score;
  final Float32List coefficients;
}

/// [pred] is the first model output, shape (1, 37, anchors), stored channel by channel: rows 0-3 are the box centre x, y,
/// width and height (0..1), row 4 the score, rows 5-36 the mask coefficients.
List<SegDetection> decodeDetections(
  Float32List pred, {
  int anchors = kAnchors,
  int inputH = kInputH,
  int inputW = kInputW,
  double conf = 0.25,
  double iou = 0.7,
  int maxDet = 300,
}) {
  final candidates = <int>[
    for (var a = 0; a < anchors; a++)
      if (pred[4 * anchors + a] >= conf) a,
  ];
  if (candidates.isEmpty) return const [];

  // Best score first; ties keep their original order.
  candidates.sort((a, b) {
    final c = pred[4 * anchors + b].compareTo(pred[4 * anchors + a]);
    return c != 0 ? c : a.compareTo(b);
  });

  double bx1(int a) => (pred[a] - pred[2 * anchors + a] / 2) * inputW;
  double by1(int a) => (pred[anchors + a] - pred[3 * anchors + a] / 2) * inputH;
  double bx2(int a) => (pred[a] + pred[2 * anchors + a] / 2) * inputW;
  double by2(int a) => (pred[anchors + a] + pred[3 * anchors + a] / 2) * inputH;

  final kept = <int>[];
  final suppressed = <int>{};
  for (final i in candidates) {
    if (kept.length >= maxDet) break;
    if (suppressed.contains(i)) continue;
    kept.add(i);
    final ax1 = bx1(i), ay1 = by1(i), ax2 = bx2(i), ay2 = by2(i);
    final areaA = (ax2 - ax1) * (ay2 - ay1);
    for (final j in candidates) {
      if (j == i || suppressed.contains(j) || kept.contains(j)) continue;
      final ix = math.max(0.0, math.min(ax2, bx2(j)) - math.max(ax1, bx1(j)));
      final iy = math.max(0.0, math.min(ay2, by2(j)) - math.max(ay1, by1(j)));
      final inter = ix * iy;
      final areaB = (bx2(j) - bx1(j)) * (by2(j) - by1(j));
      if (inter / (areaA + areaB - inter + 1e-9) > iou) suppressed.add(j);
    }
  }

  return [
    for (final a in kept)
      SegDetection(
        bx1(a),
        by1(a),
        bx2(a),
        by2(a),
        pred[4 * anchors + a],
        Float32List.fromList([for (var k = 0; k < kMaskDim; k++) pred[(5 + k) * anchors + a]]),
      ),
  ];
}

/// All detections merged into one 0/1 mask (row-major, h x w). Each mask is coefficients x prototypes, upsampled,
/// thresholded at 0 and cropped to its box.
///
/// [proto] is the second model output, shape (1, 32, protoH, protoW).
Uint8List unionMask(
  Float32List proto,
  List<SegDetection> detections, {
  int h = kInputH,
  int w = kInputW,
  int protoH = kProtoH,
  int protoW = kProtoW,
}) {
  final out = Uint8List(h * w);
  if (detections.isEmpty) return out;
  final plane = protoH * protoW;

  // Bilinear taps (half-pixel centres, edges clamped), the same as cv2.resize with INTER_LINEAR.
  List<int> tap0(int dst, int src) => [for (var d = 0; d < dst; d++) _tap(d, dst, src).$1];
  List<int> tap1(int dst, int src) => [for (var d = 0; d < dst; d++) _tap(d, dst, src).$2];
  List<double> frac(int dst, int src) => [for (var d = 0; d < dst; d++) _tap(d, dst, src).$3];
  final xa = tap0(w, protoW), xb = tap1(w, protoW);
  final xf = frac(w, protoW);
  final ya = tap0(h, protoH), yb = tap1(h, protoH);
  final yf = frac(h, protoH);

  final logits = Float64List(plane);
  for (final d in detections) {
    logits.fillRange(0, plane, 0);
    for (var k = 0; k < kMaskDim; k++) {
      final c = d.coefficients[k];
      final base = k * plane;
      for (var p = 0; p < plane; p++) {
        logits[p] += c * proto[base + p];
      }
    }
    // A pixel x is inside the box when x1 <= x < x2.
    final xStart = math.max(0, d.x1.ceil()), xEnd = math.min(w, d.x2.ceil());
    final yStart = math.max(0, d.y1.ceil()), yEnd = math.min(h, d.y2.ceil());
    for (var y = yStart; y < yEnd; y++) {
      final r0 = ya[y] * protoW, r1 = yb[y] * protoW, fy = yf[y];
      for (var x = xStart; x < xEnd; x++) {
        final fx = xf[x];
        final top = logits[r0 + xa[x]] * (1 - fx) + logits[r0 + xb[x]] * fx;
        final bottom = logits[r1 + xa[x]] * (1 - fx) + logits[r1 + xb[x]] * fx;
        if (top * (1 - fy) + bottom * fy > 0) out[y * w + x] = 1;
      }
    }
  }
  return out;
}

/// Source pixels and weight for destination pixel [d] when resizing src to dst (half-pixel centres).
(int, int, double) _tap(int d, int dst, int src) {
  final s = (d + 0.5) * src / dst - 0.5;
  if (s < 0) return (0, 0, 0.0);
  final i = s.floor();
  if (i >= src - 1) return (src - 1, src - 1, 0.0);
  return (i, i + 1, s - i);
}

/// Number of set pixels in a 0/1 mask.
int maskArea(Uint8List mask) {
  var n = 0;
  for (final v in mask) {
    n += v;
  }
  return n;
}
