import 'dart:typed_data';

import '../light_calibrator.dart';
import 'capture_verdict.dart';
import 'sticker_gate.dart';

/// Everything the capture check found in one photo.
class CaptureCheck {
  const CaptureCheck(this.scan, this.marker, this.skin, this.verdict, {this.stickerCrop});

  final GateScan scan;

  /// The straightened sticker (500 x 500 RGB) the colour check read; null when no sticker was found. The analysis reuses it.
  final Uint8List? stickerCrop;

  /// The sticker's colour patches, read from the straightened sticker; null when no sticker was found.
  final MarkerCheck? marker;
  final SkinType? skin;
  final CaptureVerdict verdict;
}

/// Looks at one decoded photo (RGBA) the way the nurse needs it right after taking it: is there a usable sticker, are its colour
/// patches readable, is there enough skin. Runs in well under a second on a laptop; call it off the main isolate on a phone.
CaptureCheck checkCapture(Uint8List rgba, int width, int height) {
  final scan = diagnoseStickers(rgba, width, height);
  MarkerCheck? marker;
  Uint8List? crop;
  if (scan.found) {
    crop = straightenSticker(rgba, width, height, scan.stickers.first);
    marker = estimateGains(crop, 500, 500);
  }
  final (small, w, h) = shrinkRgba(rgba, width, height, 640);
  final skin = skinType(small, w, h);
  return CaptureCheck(scan, marker, skin, judgeCapture(scan: scan, marker: marker, skin: skin), stickerCrop: crop);
}

/// Shrinks an RGBA photo by whole-number block averages so its long side is at most [maxDim]; the same photo when it already is.
(Uint8List, int, int) shrinkRgba(Uint8List rgba, int width, int height, int maxDim) {
  final long = width > height ? width : height;
  if (long <= maxDim) return (rgba, width, height);
  final f = (long / maxDim).ceil();
  final w = width ~/ f, h = height ~/ f;
  final out = Uint8List(w * h * 4);
  final n = f * f;
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      var r = 0, g = 0, b = 0;
      for (var dy = 0; dy < f; dy++) {
        for (var dx = 0; dx < f; dx++) {
          final i = ((y * f + dy) * width + x * f + dx) * 4;
          r += rgba[i];
          g += rgba[i + 1];
          b += rgba[i + 2];
        }
      }
      final o = (y * w + x) * 4;
      out[o] = (r / n + 0.5).floor();
      out[o + 1] = (g / n + 0.5).floor();
      out[o + 2] = (b / n + 0.5).floor();
      out[o + 3] = 255;
    }
  }
  return (out, w, h);
}
