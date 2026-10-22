import 'dart:math' as math;
import 'dart:typed_data';

import 'yolo_seg_postprocess.dart';

/// How pixels of the 640x480 canvas convert to millimetres on the plane the wound lies in. A reference marker supplies
/// it; which marker is used decides which kind of scale can be built, but the area calculation does not depend on that.
abstract class PlaneScale {
  /// Area in mm² of the set pixels of [mask] (row by row, [width] pixels per row, values 0 or 1).
  double areaMm2(Uint8List mask, {int width = kInputW});
}

/// The same scale everywhere in the picture. Right when the phone is held square to the wound; a tilt of 5 degrees costs
/// about 3% of area and 20 degrees about 14% (see research/IT23294066/area-measurement/geometry_simulation.py).
class UniformScale implements PlaneScale {
  const UniformScale(this.mmPerPixel);

  final double mmPerPixel;

  /// From a square marker of side [sideMm] whose four corners were found at [corners] (in order around the square).
  /// The scale is the side length divided by the average length of the four edges in pixels.
  factory UniformScale.fromSquare(List<(double, double)> corners, double sideMm) {
    _checkSquare(corners, sideMm);
    var sum = 0.0;
    for (var i = 0; i < 4; i++) {
      final a = corners[i], b = corners[(i + 1) % 4];
      sum += math.sqrt(math.pow(a.$1 - b.$1, 2) + math.pow(a.$2 - b.$2, 2));
    }
    return UniformScale(sideMm / (sum / 4));
  }

  @override
  double areaMm2(Uint8List mask, {int width = kInputW}) {
    var n = 0;
    for (final v in mask) {
      n += v;
    }
    return n * mmPerPixel * mmPerPixel;
  }
}

/// A perspective map from canvas pixels to millimetres on the wound's plane, found from the four corners of a square
/// marker. It corrects for camera tilt, but amplifies corner errors when the marker is small, so it needs a marker of
/// roughly 5 cm or corners found on the full-size photo.
class HomographyScale implements PlaneScale {
  /// [h] is a 3x3 matrix, row by row: (x, y) in pixels maps to ((h0 x + h1 y + h2) / w, (h3 x + h4 y + h5) / w) with
  /// w = h6 x + h7 y + h8.
  HomographyScale(this.h) : assert(h.length == 9);

  final List<double> h;

  /// From the pixel positions of a square marker's corners, in order around the square, and its side in mm.
  factory HomographyScale.fromSquare(List<(double, double)> corners, double sideMm) {
    _checkSquare(corners, sideMm);
    final plane = [(0.0, 0.0), (sideMm, 0.0), (sideMm, sideMm), (0.0, sideMm)];
    // Eight equations for the eight unknowns h0..h7 (h8 is 1).
    final a = List.generate(8, (_) => List<double>.filled(9, 0));
    for (var i = 0; i < 4; i++) {
      final (x, y) = corners[i];
      final (u, v) = plane[i];
      a[2 * i] = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
      a[2 * i + 1] = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
    }
    for (var c = 0; c < 8; c++) {
      var p = c;
      for (var r = c + 1; r < 8; r++) {
        if (a[r][c].abs() > a[p][c].abs()) p = r;
      }
      if (a[p][c].abs() < 1e-12) throw ArgumentError('the four corners do not form a usable square');
      final t = a[c];
      a[c] = a[p];
      a[p] = t;
      for (var r = 0; r < 8; r++) {
        if (r == c) continue;
        final f = a[r][c] / a[c][c];
        for (var k = c; k < 9; k++) {
          a[r][k] -= f * a[c][k];
        }
      }
    }
    return HomographyScale([for (var i = 0; i < 8; i++) a[i][8] / a[i][i], 1.0]);
  }

  /// Millimetres on the plane for a pixel position.
  (double, double) toPlane(double x, double y) {
    final w = h[6] * x + h[7] * y + h[8];
    return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
  }

  /// Each pixel is worth det(H) / w³ mm², the local stretch of the perspective map at its centre.
  @override
  double areaMm2(Uint8List mask, {int width = kInputW}) {
    final det = h[0] * (h[4] * h[8] - h[5] * h[7]) - h[1] * (h[3] * h[8] - h[5] * h[6]) + h[2] * (h[3] * h[7] - h[4] * h[6]);
    var area = 0.0;
    for (var p = 0; p < mask.length; p++) {
      if (mask[p] == 0) continue;
      final x = p % width + 0.5, y = p ~/ width + 0.5;
      final w = h[6] * x + h[7] * y + h[8];
      area += (det / (w * w * w)).abs();
    }
    return area;
  }
}

void _checkSquare(List<(double, double)> corners, double sideMm) {
  if (corners.length != 4) throw ArgumentError('a square has four corners, got ${corners.length}');
  if (!(sideMm > 0)) throw ArgumentError('the marker side must be positive, got $sideMm');
}
