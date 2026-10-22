import 'dart:math' as math;
import 'dart:typed_data';

/// The capture gate: looks for the reference sticker in a photo and, when it cannot use one, says why with a fixed code.
///
/// This is the Dart counterpart of `diagnose_stickers` in research/IT23294066/area-measurement/sticker_finder.py, which is the
/// reference (the tests compare the two on photos made by make_gate_fixtures.py). Same steps: find dark frames with a hole,
/// turn each into four corners, straighten it, check the dark orientation square and the patch layout. It has no sub-pixel edge
/// fit, because the gate only needs to know whether a sticker is usable; the corners are good to a few pixels, not enough for
/// measuring an area.

/// Why a photo has no usable sticker. The words are shared with the Python finder and with Color Matric's decision table.
enum FinderCode {
  noCandidate('no_candidate'),
  cutByEdge('cut_by_edge'),
  weakFrameContrast('weak_frame_contrast'),
  weakOrientation('weak_orientation'),
  layoutMismatch('layout_mismatch'),
  mirrored('mirrored');

  const FinderCode(this.code);

  final String code;
}

/// One sticker the gate accepted. Corners are in photo pixels, in the order they are printed (top-left of the print first).
class GateSticker {
  const GateSticker(this.corners, this.pxPerMm, this.orientationContrast, this.frameContrast);

  final List<(double, double)> corners;

  /// Roughly the pixels per mm along the sides. Good enough for a quality check, not for measuring.
  final double pxPerMm;
  final double orientationContrast;
  final double frameContrast;

  (double, double) get centre =>
      (corners.fold(0.0, (a, c) => a + c.$1) / 4, corners.fold(0.0, (a, c) => a + c.$2) / 4);
}

class GateScan {
  const GateScan(this.stickers, this.code, this.bestFrameContrast, this.bestOrientationContrast, this.candidates);

  final List<GateSticker> stickers;

  /// Null when at least one sticker was accepted.
  final FinderCode? code;
  final double bestFrameContrast;
  final double bestOrientationContrast;
  final int candidates;

  bool get found => stickers.isNotEmpty;
}

const _sideMm = 50.0;
const _frameMm = 5.0;
const _canon = 100; // the straightened picture is 100 x 100 pixels, 2 per mm
// marker_layout.json: orientation square, the three patches (centre x, centre y, size) and the white reference.
const _sqLo = 5.0, _sqHi = 11.0;
const _patches = [(12.5, 25.0, 10.0), (25.0, 25.0, 10.0), (37.5, 25.0, 10.0)];
const _whiteX = (15.0, 35.0), _whiteY = (33.0, 42.0);

/// Grey values the way OpenCV makes them (fixed-point 0.299 R + 0.587 G + 0.114 B).
Uint8List toGray(Uint8List rgba, int w, int h) {
  final out = Uint8List(w * h);
  for (var i = 0, j = 0; i < out.length; i++, j += 4) {
    out[i] = (rgba[j] * 4899 + rgba[j + 1] * 9617 + rgba[j + 2] * 1868 + 8192) >> 14;
  }
  return out;
}

/// Shrinks a grey picture so its long side is at most [maxDim] (area average, like OpenCV's INTER_AREA). Returns the picture,
/// its size and the factor (1.0 when nothing was done).
(Uint8List, int, int, double) shrinkGray(Uint8List src, int w, int h, int maxDim) {
  final long = math.max(w, h);
  if (long <= maxDim) return (src, w, h, 1.0);
  final f = maxDim / long;
  final dw = math.max(1, (w * f).round()), dh = math.max(1, (h * f).round());
  final tmp = Float32List(dw * h);
  final sx = w / dw, sy = h / dh;
  for (var dx = 0; dx < dw; dx++) {
    final x0 = dx * sx, x1 = (dx + 1) * sx;
    final i0 = x0.floor(), i1 = math.min(w, x1.ceil());
    for (var y = 0; y < h; y++) {
      var sum = 0.0;
      for (var ix = i0; ix < i1; ix++) {
        sum += src[y * w + ix] * (math.min(ix + 1.0, x1) - math.max(ix.toDouble(), x0));
      }
      tmp[y * dw + dx] = sum / sx;
    }
  }
  final out = Uint8List(dw * dh);
  for (var dy = 0; dy < dh; dy++) {
    final y0 = dy * sy, y1 = (dy + 1) * sy;
    final j0 = y0.floor(), j1 = math.min(h, y1.ceil());
    for (var x = 0; x < dw; x++) {
      var sum = 0.0;
      for (var iy = j0; iy < j1; iy++) {
        sum += tmp[iy * dw + x] * (math.min(iy + 1.0, y1) - math.max(iy.toDouble(), y0));
      }
      out[dy * dw + x] = (sum / sy + 0.5).floor();
    }
  }
  return (out, dw, dh, dw / w);
}

/// Looks for stickers in an RGBA photo and, when none is usable, says why. [maxDim] is the working size of the long side.
GateScan diagnoseStickers(
  Uint8List rgba,
  int width,
  int height, {
  int maxDim = 1200,
  double minOrient = 0.5,
  double minFrame = 0.5,
}) {
  final (small, w, h, scale) = shrinkGray(toGray(rgba, width, height), width, height, maxDim);
  final scan = _Scan(small, w, h);
  final strict = scan.run(minOrient, minFrame, lenient: false);
  var notes = strict.notes;
  if (strict.stickers.isEmpty && notes.isEmpty) notes = scan.run(minOrient, minFrame, lenient: true).notes;
  double clamp01(double v) => math.min(math.max(v, 0.0), 1.0);
  final bestFc = clamp01(notes.fold(0.0, (a, n) => math.max(a, n.fc)));
  final bestOc = clamp01(notes.fold(0.0, (a, n) => math.max(a, n.oc)));
  GateSticker toPhoto(_Hit s) {
    (double, double) back((double, double) p) => ((p.$1 + 0.5) / scale - 0.5, (p.$2 + 0.5) / scale - 0.5);
    final c = [for (final p in s.corners) back(p)];
    var side = 0.0;
    for (var i = 0; i < 4; i++) {
      side += _len(c[i], c[(i + 1) % 4]);
    }
    return GateSticker(c, side / 4 / _sideMm, s.oc, s.fc);
  }

  final stickers = [for (final s in strict.stickers) toPhoto(s)]..sort((a, b) => b.pxPerMm.compareTo(a.pxPerMm));
  if (stickers.isNotEmpty) return GateScan(stickers, null, bestFc, bestOc, notes.length);
  final rejected = notes.where((n) => n.code != null).toList();
  FinderCode code;
  if (rejected.isNotEmpty) {
    rejected.sort((a, b) => b.area.compareTo(a.area));
    code = rejected.first.code!;
  } else if (scan.cutByEdge(strict.minSide)) {
    code = FinderCode.cutByEdge;
  } else {
    code = FinderCode.noCandidate;
  }
  return GateScan(const [], code, bestFc, bestOc, notes.length);
}

/// The sticker straightened to a [size] x [size] RGB picture (10 pixels per mm for 500), the printed top left at the top left, cut
/// from the full photo with the corners of [s]. This is what the colour check reads. Corners are good to a few pixels, which is
/// enough to read the middle of each patch.
Uint8List straightenSticker(Uint8List rgba, int width, int height, GateSticker s, {int size = 500}) {
  final to = [for (final c in s.corners) c];
  final dst = [(0.0, 0.0), (size.toDouble(), 0.0), (size.toDouble(), size.toDouble()), (0.0, size.toDouble())];
  final hm = _homography(dst, to);
  final out = Uint8List(size * size * 3);
  for (var j = 0; j < size; j++) {
    for (var i = 0; i < size; i++) {
      final d = hm[6] * i + hm[7] * j + hm[8];
      final x = math.min(math.max((hm[0] * i + hm[1] * j + hm[2]) / d, 0.0), width - 1.0);
      final y = math.min(math.max((hm[3] * i + hm[4] * j + hm[5]) / d, 0.0), height - 1.0);
      final x0 = math.min(x.floor(), width - 2), y0 = math.min(y.floor(), height - 2);
      final ax = x - x0, ay = y - y0;
      final p00 = (y0 * width + x0) * 4, p10 = p00 + 4, p01 = p00 + width * 4, p11 = p01 + 4;
      final o = (j * size + i) * 3;
      for (var c = 0; c < 3; c++) {
        final v = (rgba[p00 + c] * (1 - ax) + rgba[p10 + c] * ax) * (1 - ay) + (rgba[p01 + c] * (1 - ax) + rgba[p11 + c] * ax) * ay;
        out[o + c] = (v + 0.5).floor();
      }
    }
  }
  return out;
}

double _len((double, double) a, (double, double) b) => math.sqrt(math.pow(a.$1 - b.$1, 2) + math.pow(a.$2 - b.$2, 2));

/// Why one frame-like shape was turned down, in a fixed order (the same as the Python finder). Null when it is accepted.
FinderCode? _rejectCode(double oc, double fc, bool layout, bool transposed, double minOrient, double minFrame) {
  if (fc < minFrame) return FinderCode.weakFrameContrast;
  if (oc < minOrient) return FinderCode.weakOrientation;
  if (!layout) return transposed ? FinderCode.mirrored : FinderCode.layoutMismatch;
  return null;
}

class _Note {
  const _Note(this.area, this.code, this.oc, this.fc);

  final double area;
  final FinderCode? code;
  final double oc, fc;
}

class _Hit {
  const _Hit(this.corners, this.oc, this.fc);

  final List<(double, double)> corners;
  final double oc, fc;
}

class _Result {
  const _Result(this.stickers, this.notes, this.minSide);

  final List<_Hit> stickers;
  final List<_Note> notes;
  final double minSide;
}

class _Scan {
  _Scan(this.gray, this.w, this.h);

  final Uint8List gray;
  final int w, h;

  _Result run(double minOrient, double minFrame, {required bool lenient}) {
    final dark = _darkMask(_blur3(gray, w, h), w, h, lenient ? 0.8 : 0.5, lenient ? 215 : 140);
    final minSide = 0.04 * math.max(w, h);
    final comps = _components(dark, w, h);
    final hits = <_Hit>[];
    final notes = <_Note>[];
    for (final c in comps.where((c) => c.holes > 0 && c.area + c.holeArea >= minSide * minSide)) {
      final quad = _quad(c.boundary, minSide);
      if (quad == null) continue;
      final q = _orderClockwise(quad);
      final blur = _blurAround(q);
      final (oc, fc, k, canon) = _orient(blur, q);
      final layout = _layoutOk(canon);
      var code = _rejectCode(oc, fc, layout, layout ? false : _layoutOk(_transpose(canon)), minOrient, minFrame);
      if (lenient && code == null) code = FinderCode.weakFrameContrast;
      final area = (c.area + c.holeArea).toDouble();
      notes.add(_Note(area, code, oc, fc));
      if (code != null) continue;
      hits.add(_Hit([for (var i = 0; i < 4; i++) q[(i + k) % 4]], oc, fc));
    }
    return _Result(hits, notes, minSide);
  }

  /// A big dark shape touches the photo edge and has bright paper inside its bounding box: a frame that is cut off. A solid dark
  /// object at the edge (a bag, a chair, a shadow) has no paper inside, so it does not count.
  bool cutByEdge(double minSide) {
    final dark = _darkMask(_blur3(gray, w, h), w, h, 0.5, 140);
    final hist = List<int>.filled(256, 0);
    for (final v in gray) {
      hist[v]++;
    }
    var acc = 0, p95 = 255;
    for (var v = 0; v < 256; v++) {
      acc += hist[v];
      if (acc >= 0.95 * gray.length) {
        p95 = v;
        break;
      }
    }
    final paper = 0.75 * p95;
    for (final c in _components(dark, w, h)) {
      if (c.area < minSide * minSide) continue;
      if (!(c.minX <= 1 || c.minY <= 1 || c.maxX >= w - 2 || c.maxY >= h - 2)) continue;
      var bright = 0;
      final bw = c.maxX - c.minX + 1, bh = c.maxY - c.minY + 1;
      for (var y = c.minY; y <= c.maxY; y++) {
        for (var x = c.minX; x <= c.maxX; x++) {
          if (gray[y * w + x] > paper) bright++;
        }
      }
      if (bright / (bw * bh) >= 0.25) return true;
    }
    return false;
  }

  /// The grey picture, blurred a little more for a bigger sticker, around one candidate (a window, to save time).
  _Window _blurAround(List<(double, double)> q) {
    final sigma = math.max(0.5, 0.6 * _len(q[0], q[1]) / 100);
    final pad = (3 * sigma).ceil() + 2;
    var x0 = q.map((p) => p.$1).reduce(math.min).floor() - pad, x1 = q.map((p) => p.$1).reduce(math.max).ceil() + pad;
    var y0 = q.map((p) => p.$2).reduce(math.min).floor() - pad, y1 = q.map((p) => p.$2).reduce(math.max).ceil() + pad;
    x0 = math.max(0, x0);
    y0 = math.max(0, y0);
    x1 = math.min(w - 1, x1);
    y1 = math.min(h - 1, y1);
    final ww = x1 - x0 + 1, wh = y1 - y0 + 1;
    final r = (3 * sigma).ceil();
    final k = Float32List(2 * r + 1);
    var total = 0.0;
    for (var i = -r; i <= r; i++) {
      k[i + r] = math.exp(-i * i / (2 * sigma * sigma));
      total += k[i + r];
    }
    for (var i = 0; i < k.length; i++) {
      k[i] /= total;
    }
    final tmp = Float32List(ww * wh);
    for (var y = 0; y < wh; y++) {
      for (var x = 0; x < ww; x++) {
        var s = 0.0;
        for (var i = -r; i <= r; i++) {
          final sx = math.min(math.max(x0 + x + i, 0), w - 1);
          s += gray[(y0 + y) * w + sx] * k[i + r];
        }
        tmp[y * ww + x] = s;
      }
    }
    final out = Float32List(ww * wh);
    for (var y = 0; y < wh; y++) {
      for (var x = 0; x < ww; x++) {
        var s = 0.0;
        for (var i = -r; i <= r; i++) {
          final sy = math.min(math.max(y + i, 0), wh - 1);
          s += tmp[sy * ww + x] * k[i + r];
        }
        out[y * ww + x] = s;
      }
    }
    return _Window(out, ww, wh, x0, y0);
  }

  /// Tries the four ways round and returns the orientation contrast, the frame contrast, the turn that makes the dark corner the
  /// top left, and the straightened picture of that turn.
  (double, double, int, Float32List) _orient(_Window blur, List<(double, double)> q) {
    final dst = [(0.0, 0.0), (_canon.toDouble(), 0.0), (_canon.toDouble(), _canon.toDouble()), (0.0, _canon.toDouble())];
    (double, double, int, Float32List)? best;
    for (var k = 0; k < 4; k++) {
      final qk = [for (var i = 0; i < 4; i++) q[(i + k) % 4]];
      final hm = _homography(dst, qk);
      final canon = Float32List(_canon * _canon);
      for (var j = 0; j < _canon; j++) {
        for (var i = 0; i < _canon; i++) {
          final d = hm[6] * i + hm[7] * j + hm[8];
          canon[j * _canon + i] = blur.sample((hm[0] * i + hm[1] * j + hm[2]) / d, (hm[3] * i + hm[4] * j + hm[5]) / d);
        }
      }
      final r = _regions(canon);
      final span = math.max(r.white - r.frame, 1.0);
      final oc = (r.others.reduce(math.min) - r.orient) / span;
      final fc = (r.white - r.frame) / (r.white + 1.0);
      if (best == null || oc > best.$1) best = (oc, fc, k, canon);
    }
    return best!;
  }
}

class _Window {
  _Window(this.v, this.w, this.h, this.x0, this.y0);

  final Float32List v;
  final int w, h, x0, y0;

  /// Bilinear value at a position in photo pixels (pixel centres at whole numbers); outside the window it repeats the edge.
  double sample(double x, double y) {
    final fx = math.min(math.max(x - x0, 0.0), w - 1.0), fy = math.min(math.max(y - y0, 0.0), h - 1.0);
    final ix = math.min(fx.floor(), w - 2 < 0 ? 0 : w - 2), iy = math.min(fy.floor(), h - 2 < 0 ? 0 : h - 2);
    final ax = fx - ix, ay = fy - iy;
    final ix1 = math.min(ix + 1, w - 1), iy1 = math.min(iy + 1, h - 1);
    return (v[iy * w + ix] * (1 - ax) + v[iy * w + ix1] * ax) * (1 - ay) + (v[iy1 * w + ix] * (1 - ax) + v[iy1 * w + ix1] * ax) * ay;
  }
}

class _Regions {
  const _Regions(this.orient, this.others, this.frame, this.white);

  final double orient;
  final List<double> others;
  final double frame, white;
}

double _mean(Float32List c, double x0, double x1, double y0, double y1) {
  const s = _canon / _sideMm;
  final xa = (x0 * s).toInt(), xb = (x1 * s).toInt(), ya = (y0 * s).toInt(), yb = (y1 * s).toInt();
  var sum = 0.0;
  var n = 0;
  for (var y = ya; y < yb; y++) {
    for (var x = xa; x < xb; x++) {
      sum += c[y * _canon + x];
      n++;
    }
  }
  return n == 0 ? 0 : sum / n;
}

_Regions _regions(Float32List c) {
  const far = _sideMm - _frameMm;
  const sq = _sqHi - _sqLo;
  final orient = _mean(c, _sqLo, _sqHi, _sqLo, _sqHi);
  final others = [
    _mean(c, far - sq, far, _sqLo, _sqHi),
    _mean(c, far - sq, far, far - sq, far),
    _mean(c, _sqLo, _sqHi, far - sq, far),
  ];
  final frame = (_mean(c, 0.5, _sideMm - 0.5, 0.5, _frameMm - 1) +
          _mean(c, 0.5, _sideMm - 0.5, _sideMm - _frameMm + 1, _sideMm - 0.5) +
          _mean(c, 0.5, _frameMm - 1, 0.5, _sideMm - 0.5) +
          _mean(c, _sideMm - _frameMm + 1, _sideMm - 0.5, 0.5, _sideMm - 0.5)) /
      4;
  return _Regions(orient, others, frame, _mean(c, _whiteX.$1, _whiteX.$2, _whiteY.$1, _whiteY.$2));
}

/// The mid-tone patches must lie where the layout puts them, and the cells where a mirrored sticker would have them (x and y
/// swapped) must be bare white.
bool _layoutOk(Float32List c) {
  const s = _canon / _sideMm;
  double cell(double cx, double cy, double size) {
    final half = 0.3 * size;
    return _mean(c, cx - half, cx + half, cy - half, cy + half);
  }

  final white = _regions(c).white + 1.0;
  final patch = [for (final p in _patches) cell(p.$1, p.$2, p.$3)];
  final swapped = [for (final p in _patches) if (p.$1 != p.$2) cell(p.$2, p.$1, p.$3)];
  return patch.reduce(math.max) / white < 0.9 && swapped.reduce(math.min) / white > 0.8 && s > 0;
}

Float32List _transpose(Float32List c) {
  final t = Float32List(c.length);
  for (var y = 0; y < _canon; y++) {
    for (var x = 0; x < _canon; x++) {
      t[x * _canon + y] = c[y * _canon + x];
    }
  }
  return t;
}

/// 3 x 3 smoothing (the 1-2-1 kernel), edges repeated.
Uint8List _blur3(Uint8List g, int w, int h) {
  final tmp = Float32List(w * h);
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      final l = g[y * w + math.max(x - 1, 0)], c = g[y * w + x], r = g[y * w + math.min(x + 1, w - 1)];
      tmp[y * w + x] = (l + 2 * c + r) / 4;
    }
  }
  final out = Uint8List(w * h);
  for (var y = 0; y < h; y++) {
    final up = math.max(y - 1, 0) * w, mid = y * w, dn = math.min(y + 1, h - 1) * w;
    for (var x = 0; x < w; x++) {
      out[mid + x] = ((tmp[up + x] + 2 * tmp[mid + x] + tmp[dn + x]) / 4 + 0.5).floor();
    }
  }
  return out;
}

/// Pixels darker than [factor] times the average around them (a window of about an eighth of the photo) and darker than [absMax],
/// closed with a 3 x 3 block so hairline gaps in a frame do not split it.
Uint8List _darkMask(Uint8List g, int w, int h, double factor, int absMax) {
  final k = math.max(15, (math.min(w, h) ~/ 8) | 1);
  final r = k ~/ 2;
  final iw = w + 1;
  final integral = Int64List(iw * (h + 1));
  for (var y = 0; y < h; y++) {
    var row = 0;
    for (var x = 0; x < w; x++) {
      row += g[y * w + x];
      integral[(y + 1) * iw + x + 1] = integral[y * iw + x + 1] + row;
    }
  }
  final dark = Uint8List(w * h);
  for (var y = 0; y < h; y++) {
    final y0 = math.max(0, y - r), y1 = math.min(h, y + r + 1);
    for (var x = 0; x < w; x++) {
      final x0 = math.max(0, x - r), x1 = math.min(w, x + r + 1);
      final sum = integral[y1 * iw + x1] - integral[y0 * iw + x1] - integral[y1 * iw + x0] + integral[y0 * iw + x0];
      final mean = sum / ((x1 - x0) * (y1 - y0));
      final v = g[y * w + x];
      if (v < factor * mean && v < absMax) dark[y * w + x] = 1;
    }
  }
  return _erode(_dilate(dark, w, h), w, h);
}

Uint8List _dilate(Uint8List m, int w, int h) => _morph(m, w, h, true);

Uint8List _erode(Uint8List m, int w, int h) => _morph(m, w, h, false);

Uint8List _morph(Uint8List m, int w, int h, bool grow) {
  final tmp = Uint8List(w * h), out = Uint8List(w * h);
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      var v = grow ? 0 : 1;
      for (var dx = -1; dx <= 1; dx++) {
        final xx = x + dx;
        if (xx < 0 || xx >= w) continue;
        final p = m[y * w + xx];
        if (grow ? p > v : p < v) v = p;
      }
      tmp[y * w + x] = v;
    }
  }
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      var v = grow ? 0 : 1;
      for (var dy = -1; dy <= 1; dy++) {
        final yy = y + dy;
        if (yy < 0 || yy >= h) continue;
        final p = tmp[yy * w + x];
        if (grow ? p > v : p < v) v = p;
      }
      out[y * w + x] = v;
    }
  }
  return out;
}

class _Comp {
  int area = 0, holes = 0, holeArea = 0;
  int minX = 1 << 30, minY = 1 << 30, maxX = -1, maxY = -1;
  final boundary = <(int, int)>[];
}

/// Connected dark shapes (8-connected), each with the number and size of the holes inside it (light regions that do not reach the
/// edge of the photo, 4-connected) and the pixels on its boundary.
List<_Comp> _components(Uint8List m, int w, int h) {
  final fg = _label(m, w, h, 1, true);
  final bg = _label(m, w, h, 0, false);
  final comps = List<_Comp>.generate(fg.count + 1, (_) => _Comp());
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      final l = fg.labels[y * w + x];
      if (l == 0) continue;
      final c = comps[l];
      c.area++;
      if (x < c.minX) c.minX = x;
      if (x > c.maxX) c.maxX = x;
      if (y < c.minY) c.minY = y;
      if (y > c.maxY) c.maxY = y;
    }
  }
  // Which light regions are holes, and which dark shape surrounds each: the dark pixel just above the first pixel of the hole.
  final touches = List<bool>.filled(bg.count + 1, false);
  final first = List<int>.filled(bg.count + 1, -1);
  final size = List<int>.filled(bg.count + 1, 0);
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      final l = bg.labels[y * w + x];
      if (l == 0) continue;
      size[l]++;
      if (first[l] < 0) first[l] = y * w + x;
      if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touches[l] = true;
    }
  }
  for (var l = 1; l <= bg.count; l++) {
    if (touches[l] || first[l] < w) continue;
    final owner = fg.labels[first[l] - w];
    if (owner == 0) continue;
    comps[owner].holes++;
    comps[owner].holeArea += size[l];
  }
  final wanted = [for (final c in comps) c.holes > 0 || c.area > 0];
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      final l = fg.labels[y * w + x];
      if (l == 0 || !wanted[l] || comps[l].holes == 0) continue;
      final edge = x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
          fg.labels[y * w + x - 1] != l || fg.labels[y * w + x + 1] != l || fg.labels[(y - 1) * w + x] != l || fg.labels[(y + 1) * w + x] != l;
      if (edge) comps[l].boundary.add((x, y));
    }
  }
  return comps.sublist(1);
}

class _Labels {
  _Labels(this.labels, this.count);

  final Int32List labels;
  final int count;
}

_Labels _label(Uint8List m, int w, int h, int value, bool eight) {
  final labels = Int32List(w * h);
  final stack = Int32List(w * h);
  var count = 0;
  for (var start = 0; start < w * h; start++) {
    if (m[start] != value || labels[start] != 0) continue;
    count++;
    var top = 0;
    stack[top++] = start;
    labels[start] = count;
    while (top > 0) {
      final p = stack[--top];
      final x = p % w, y = p ~/ w;
      for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
          if ((dx == 0 && dy == 0) || (!eight && dx != 0 && dy != 0)) continue;
          final xx = x + dx, yy = y + dy;
          if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
          final q = yy * w + xx;
          if (m[q] == value && labels[q] == 0) {
            labels[q] = count;
            stack[top++] = q;
          }
        }
      }
    }
  }
  return _Labels(labels, count);
}

/// Four corners of a dark frame: the corners of its smallest turned bounding rectangle, each moved to the nearest boundary point of
/// the shape's hull. Null when it does not look like a square-ish four-sided shape.
List<(double, double)>? _quad(List<(int, int)> boundary, double minSide) {
  final hull = _hull(boundary);
  if (hull.length < 4) return null;
  var bestArea = double.infinity;
  late List<(double, double)> rect;
  for (var i = 0; i < hull.length; i++) {
    final a = hull[i], b = hull[(i + 1) % hull.length];
    final dx = (b.$1 - a.$1).toDouble(), dy = (b.$2 - a.$2).toDouble();
    final len = math.sqrt(dx * dx + dy * dy);
    if (len == 0) continue;
    final ux = dx / len, uy = dy / len, vx = -uy, vy = ux;
    var minU = double.infinity, maxU = -double.infinity, minV = double.infinity, maxV = -double.infinity;
    for (final p in hull) {
      final u = p.$1 * ux + p.$2 * uy, v = p.$1 * vx + p.$2 * vy;
      if (u < minU) minU = u;
      if (u > maxU) maxU = u;
      if (v < minV) minV = v;
      if (v > maxV) maxV = v;
    }
    final area = (maxU - minU) * (maxV - minV);
    if (area < bestArea) {
      bestArea = area;
      (double, double) at(double u, double v) => (u * ux + v * vx, u * uy + v * vy);
      rect = [at(minU, minV), at(maxU, minV), at(maxU, maxV), at(minU, maxV)];
    }
  }
  if (bestArea == double.infinity) return null;
  final picked = <int>{};
  final out = <(double, double)>[];
  for (final c in rect) {
    var best = 0;
    var bestD = double.infinity;
    for (var i = 0; i < hull.length; i++) {
      final d = math.pow(hull[i].$1 - c.$1, 2) + math.pow(hull[i].$2 - c.$2, 2);
      if (d < bestD) {
        bestD = d.toDouble();
        best = i;
      }
    }
    if (!picked.add(best)) return null;
    out.add((hull[best].$1.toDouble(), hull[best].$2.toDouble()));
  }
  final sides = [for (var i = 0; i < 4; i++) _len(out[i], out[(i + 1) % 4])];
  final lo = sides.reduce(math.min), hi = sides.reduce(math.max);
  if (lo < minSide || hi > 4 * lo) return null;
  return out;
}

/// Convex hull of integer points (monotone chain).
List<(int, int)> _hull(List<(int, int)> pts) {
  final p = {...pts}.toList()..sort((a, b) => a.$1 != b.$1 ? a.$1.compareTo(b.$1) : a.$2.compareTo(b.$2));
  if (p.length < 3) return p;
  int cross((int, int) o, (int, int) a, (int, int) b) => (a.$1 - o.$1) * (b.$2 - o.$2) - (a.$2 - o.$2) * (b.$1 - o.$1);
  final lower = <(int, int)>[];
  for (final q in p) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower.last, q) <= 0) {
      lower.removeLast();
    }
    lower.add(q);
  }
  final upper = <(int, int)>[];
  for (final q in p.reversed) {
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper.last, q) <= 0) {
      upper.removeLast();
    }
    upper.add(q);
  }
  lower.removeLast();
  upper.removeLast();
  return [...lower, ...upper];
}

/// Clockwise on the screen, starting at the corner nearest the top left.
List<(double, double)> _orderClockwise(List<(double, double)> q) {
  final cx = q.fold(0.0, (a, p) => a + p.$1) / 4, cy = q.fold(0.0, (a, p) => a + p.$2) / 4;
  final s = [...q]..sort((a, b) => math.atan2(a.$2 - cy, a.$1 - cx).compareTo(math.atan2(b.$2 - cy, b.$1 - cx)));
  var start = 0;
  for (var i = 1; i < 4; i++) {
    if (s[i].$1 + s[i].$2 < s[start].$1 + s[start].$2) start = i;
  }
  return [for (var i = 0; i < 4; i++) s[(start + i) % 4]];
}

/// The 3 x 3 matrix (row by row, last entry 1) that maps each of the four [from] points to the matching [to] point.
List<double> _homography(List<(double, double)> from, List<(double, double)> to) {
  final a = List.generate(8, (_) => List<double>.filled(9, 0));
  for (var i = 0; i < 4; i++) {
    final (x, y) = from[i];
    final (u, v) = to[i];
    a[2 * i] = [x, y, 1, 0, 0, 0, -u * x, -u * y, u];
    a[2 * i + 1] = [0, 0, 0, x, y, 1, -v * x, -v * y, v];
  }
  for (var c = 0; c < 8; c++) {
    var p = c;
    for (var r = c + 1; r < 8; r++) {
      if (a[r][c].abs() > a[p][c].abs()) p = r;
    }
    final t = a[c];
    a[c] = a[p];
    a[p] = t;
    final d = a[c][c];
    if (d.abs() < 1e-12) return [1, 0, 0, 0, 1, 0, 0, 0, 1];
    for (var r = 0; r < 8; r++) {
      if (r == c) continue;
      final f = a[r][c] / d;
      for (var k = c; k < 9; k++) {
        a[r][k] -= f * a[c][k];
      }
    }
  }
  return [for (var i = 0; i < 8; i++) a[i][8] / a[i][i], 1.0];
}
