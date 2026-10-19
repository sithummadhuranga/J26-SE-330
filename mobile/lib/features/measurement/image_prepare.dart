import 'dart:math' as math;
import 'dart:typed_data';

import 'yolo_seg_postprocess.dart';

/// A photo fitted into the model's 480x640 input image, with what is needed to map points back to the photo.
class PreparedImage {
  const PreparedImage({
    required this.rgba,
    required this.scale,
    required this.padLeft,
    required this.padTop,
    required this.originalWidth,
    required this.originalHeight,
  });

  /// The 640x480 canvas, RGBA, row by row. Unused border pixels are grey (114).
  final Uint8List rgba;

  /// Canvas pixels per photo pixel. It is the same in x and y, so shapes and areas keep their proportions.
  final double scale;

  /// Where the photo starts on the canvas.
  final int padLeft, padTop;
  final int originalWidth, originalHeight;

  /// A point on the canvas as a point in the original photo.
  (double, double) toOriginal(double x, double y) => ((x - padLeft) / scale, (y - padTop) / scale);
}

/// Fits an RGBA photo of any size into the 640x480 model input, keeping its shape (borders are added where it does not
/// fill the canvas). Shrinking averages the pixels each canvas pixel covers, which avoids the jagged edges that plain
/// bilinear gives on 12-megapixel phone photos; enlarging is bilinear. The padding rules follow Ultralytics' letterbox,
/// and research/IT23294066/on-device-export/litert_reference.py does the same thing in Python.
PreparedImage prepareImage(Uint8List rgba, int width, int height, {int padValue = 114}) {
  if (rgba.length != width * height * 4) {
    throw ArgumentError('expected ${width * height * 4} RGBA bytes for $width x $height, got ${rgba.length}');
  }
  final r = math.min(kInputH / height, kInputW / width);
  final newW = _pyRound(width * r), newH = _pyRound(height * r);
  final left = _pyRound((kInputW - newW) / 2 - 0.1), top = _pyRound((kInputH - newH) / 2 - 0.1);

  final canvas = Uint8List(kInputW * kInputH * 4);
  for (var p = 0; p < kInputW * kInputH; p++) {
    canvas[4 * p] = padValue;
    canvas[4 * p + 1] = padValue;
    canvas[4 * p + 2] = padValue;
    canvas[4 * p + 3] = 255;
  }
  final fitted = (newW == width && newH == height) ? rgba : _resize(rgba, width, height, newW, newH);
  for (var y = 0; y < newH; y++) {
    canvas.setRange(4 * ((top + y) * kInputW + left), 4 * ((top + y) * kInputW + left + newW), fitted, 4 * y * newW);
  }
  return PreparedImage(
    rgba: canvas,
    scale: r,
    padLeft: left,
    padTop: top,
    originalWidth: width,
    originalHeight: height,
  );
}

/// Python's round(): halves go to the even number, which is what Ultralytics' letterbox uses.
int _pyRound(double x) {
  final f = x.floorToDouble();
  if (x - f == 0.5) return f % 2 == 0 ? f.toInt() : f.toInt() + 1;
  return x.round();
}

/// For each destination pixel: the first source pixel it uses and the weights of the consecutive source pixels.
class _Axis {
  _Axis(this.start, this.weights);

  final Int32List start;
  final List<Float32List> weights;
}

_Axis _axis(int src, int dst) {
  final start = Int32List(dst);
  final weights = <Float32List>[];
  if (dst == src) {
    for (var d = 0; d < dst; d++) {
      start[d] = d;
      weights.add(Float32List.fromList([1]));
    }
  } else if (dst < src) {
    // Area average: each destination pixel covers src/dst source pixels, edges partly.
    final f = src / dst;
    for (var d = 0; d < dst; d++) {
      final a = d * f, b = (d + 1) * f;
      final first = a.floor(), last = math.min(src - 1, b.ceil() - 1);
      start[d] = first;
      weights.add(Float32List.fromList([
        for (var j = first; j <= last; j++) (math.min(j + 1.0, b) - math.max(j.toDouble(), a)) / f,
      ]));
    }
  } else {
    // Bilinear with half-pixel centres, edges clamped.
    for (var d = 0; d < dst; d++) {
      final s = (d + 0.5) * src / dst - 0.5;
      if (s < 0) {
        start[d] = 0;
        weights.add(Float32List.fromList([1]));
      } else if (s.floor() >= src - 1) {
        start[d] = src - 1;
        weights.add(Float32List.fromList([1]));
      } else {
        final i = s.floor();
        start[d] = i;
        weights.add(Float32List.fromList([1 - (s - i), s - i]));
      }
    }
  }
  return _Axis(start, weights);
}

Uint8List _resize(Uint8List rgba, int w, int h, int newW, int newH) {
  final ax = _axis(w, newW), ay = _axis(h, newH);
  // Horizontal pass: every source row to newW pixels (3 colour channels, float).
  final tmp = Float32List(h * newW * 3);
  for (var y = 0; y < h; y++) {
    final row = y * w * 4;
    for (var x = 0; x < newW; x++) {
      var r = 0.0, g = 0.0, b = 0.0;
      final wt = ax.weights[x];
      final s0 = ax.start[x];
      for (var k = 0; k < wt.length; k++) {
        final o = row + 4 * (s0 + k);
        r += wt[k] * rgba[o];
        g += wt[k] * rgba[o + 1];
        b += wt[k] * rgba[o + 2];
      }
      final t = (y * newW + x) * 3;
      tmp[t] = r;
      tmp[t + 1] = g;
      tmp[t + 2] = b;
    }
  }
  // Vertical pass.
  final out = Uint8List(newW * newH * 4);
  for (var y = 0; y < newH; y++) {
    final wt = ay.weights[y];
    final s0 = ay.start[y];
    for (var x = 0; x < newW; x++) {
      var r = 0.0, g = 0.0, b = 0.0;
      for (var k = 0; k < wt.length; k++) {
        final t = ((s0 + k) * newW + x) * 3;
        r += wt[k] * tmp[t];
        g += wt[k] * tmp[t + 1];
        b += wt[k] * tmp[t + 2];
      }
      final o = (y * newW + x) * 4;
      out[o] = _byte(r);
      out[o + 1] = _byte(g);
      out[o + 2] = _byte(b);
      out[o + 3] = 255;
    }
  }
  return out;
}

int _byte(double v) {
  final n = (v + 0.5).floor();
  return n < 0 ? 0 : (n > 255 ? 255 : n);
}
