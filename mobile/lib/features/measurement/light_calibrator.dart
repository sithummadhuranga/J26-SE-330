import 'dart:math' as math;
import 'dart:typed_data';

import 'measurement_pipeline.dart';

/// The light colour calibration for the phone: exposure and white balance from the grey patch of the reference sticker,
/// then the skin type (ITA) read on the corrected photo. It follows the Python code of the colour component
/// (research/IT23343702, marker.py and ita.py) and is checked against its golden vectors, see
/// test/measurement/light_calibrator_test.dart.
///
/// The sticker picture must be SQUARE (the contract is 500 x 500 at 10 pixels per mm). The Python code reads any
/// height x width, but the app only ever hands over the square crop, so a picture of another shape is ignored with a note.
///
/// STATUS: the calibration is RELATIVE. The reference colours are stand-ins until reference photos of the printed sticker
/// fill them, so every result says so in its label and the skin class is only as good as the printed grey is close to L* 50.
const kLightCalibrationVersion = 'light-1';

// Reject rules of the sticker and the skin mask. Judgement, not fitted: there are no real sticker photos yet.
const double kMaxGreyLStd = 5.0;
const double kMaxClippedFraction = 0.05;
const double kGainMin = 0.25;
const double kGainMax = 4.0;
const int kMinPatchPixels = 64;
const double kMaxGreyAbError = 20.0;
const double kMaxSkinDeltaE = 15.0;

/// Lower brightness limit (HSV V, percent) of the skin mask. The research guide says 35, which drops very dark skin.
const double kSkinVMin = 20.0;
const int kMinSkinPixels = 100;

class PatchBox {
  const PatchBox(this.cx, this.cy, this.size);

  /// Centre and side as fractions of the straightened sticker picture.
  final double cx, cy, size;
}

class StickerLayout {
  const StickerLayout(this.patches, this.sampleFraction, this.calibrationMode);

  final Map<String, PatchBox> patches;

  /// The central share of a patch that is read.
  final double sampleFraction;
  final String calibrationMode;
}

/// research/IT23294066/area-measurement/marker_layout.json (50 mm sticker, 10 mm patches). A test compares this with the file.
const kStickerLayout = StickerLayout(
  {'grey': PatchBox(0.25, 0.5, 0.2), 'skin_a': PatchBox(0.5, 0.5, 0.2), 'skin_b': PatchBox(0.75, 0.5, 0.2)},
  0.6,
  'relative',
);

/// Reference colours (CIE L*a*b*) of the patches. `null` means not specified: only the other values are compared.
class StickerReference {
  const StickerReference(this.source, this.lab);

  factory StickerReference.fromJson(Map<String, dynamic> json) {
    final lab = <String, List<double?>>{
      for (final e in (json['lab'] as Map<String, dynamic>).entries)
        e.key: [for (final v in e.value as List<dynamic>) (v as num?)?.toDouble()],
    };
    final grey = lab['grey'];
    if (grey == null || grey[1] == null || grey[2] == null) {
      throw const FormatException('the reference needs a full L*a*b* for the grey patch');
    }
    return StickerReference(json['source'] as String, lab);
  }

  /// 'stand-in', 'reference-photos' or 'measured'.
  final String source;
  final Map<String, List<double?>> lab;
}

const kStandInReference = StickerReference('stand-in', {
  'grey': <double?>[50.0, 0.0, 0.0],
  'skin_a': <double?>[38.0, 14.0, 14.0],
  'skin_b': <double?>[25.0, null, null],
});

/// Why a sticker's colour patches could not be used, as a fixed word the app can turn into a sentence (the list agreed with the
/// colour component; the text in [MarkerCheck.reason] stays as it was).
enum MarkerCode {
  greyClipped('grey_clipped'),
  greyNotUniform('grey_not_uniform'),
  greyNotNeutral('grey_not_neutral'),
  greyGainOutOfRange('grey_gain_out_of_range'),
  skinPatchOff('skin_patch_off'),
  cropInvalid('crop_invalid');

  const MarkerCode(this.code);

  final String code;
}

class MarkerCheck {
  const MarkerCheck({
    required this.used,
    this.reason = '',
    this.code,
    this.gains,
    this.skinDeltaE = const {},
    required this.referenceSource,
    required this.calibrationMode,
  });

  final bool used;

  /// Why the sticker was rejected.
  final String reason;

  /// The same reason as a fixed word; null when the sticker was used.
  final MarkerCode? code;

  /// Per-channel linear gains (R, G, B).
  final List<double>? gains;

  /// CIE76 error of the skin patches after the correction.
  final Map<String, double> skinDeltaE;
  final String referenceSource;
  final String calibrationMode;

  /// What a class read after this correction rests on, in plain words.
  String get label => '$calibrationMode calibration, $referenceSource reference colours';
}

class SkinType {
  const SkinType({this.fitzpatrick, this.ita, this.meanL, this.meanB, required this.skinPixels});

  /// 'I' to 'VI', or null when there were too few skin pixels.
  final String? fitzpatrick;
  final double? ita, meanL, meanB;
  final int skinPixels;

  /// 'too_few_skin_pixels' when no skin type could be read, else null (the word Color Matric named for the gate).
  SkinCode? get code => fitzpatrick == null ? SkinCode.tooFewSkinPixels : null;
}

enum SkinCode {
  tooFewSkinPixels('too_few_skin_pixels');

  const SkinCode(this.code);

  final String code;
}

// ---------------------------------------------------------------------------------------------------------------------
// Colour maths

double srgbToLinear(double x) => x <= 0.04045 ? x / 12.92 : math.pow((x + 0.055) / 1.055, 2.4).toDouble();

double linearToSrgb(double x) => x <= 0.0031308 ? x * 12.92 : 1.055 * math.pow(x, 1 / 2.4).toDouble() - 0.055;

final Float64List _levels = Float64List.fromList([for (var i = 0; i < 256; i++) srgbToLinear(i / 255.0)]);

/// Round half to even, like numpy's rint.
double rint(double x) {
  final f = x.floorToDouble();
  final d = x - f;
  if (d < 0.5) return f;
  if (d > 0.5) return f + 1;
  return f % 2 == 0 ? f : f + 1;
}

/// The 256-entry table that multiplies linear light by [gain]. Rounded once per entry, as the Python code does.
Uint8List gainTable(double gain) {
  final lut = Uint8List(256);
  for (var i = 0; i < 256; i++) {
    final v = rint(linearToSrgb((_levels[i] * gain).clamp(0.0, 1.0)) * 255.0).clamp(0.0, 255.0);
    lut[i] = v.toInt();
  }
  return lut;
}

/// Multiplies linear-light R, G, B of an RGBA picture by [gains]; alpha is kept. Returns a new picture.
Uint8List applyGains(Uint8List rgba, List<double> gains) {
  final lr = gainTable(gains[0]), lg = gainTable(gains[1]), lb = gainTable(gains[2]);
  final out = Uint8List(rgba.length);
  for (var i = 0; i + 3 < rgba.length; i += 4) {
    out[i] = lr[rgba[i]];
    out[i + 1] = lg[rgba[i + 1]];
    out[i + 2] = lb[rgba[i + 2]];
    out[i + 3] = rgba[i + 3];
  }
  return out;
}

const double _xn = 0.950456, _zn = 1.088754; // D65 white point, as OpenCV uses it

double _f(double t) => t > 0.008856 ? math.pow(t, 1 / 3).toDouble() : 7.787 * t + 16 / 116;

/// CIE L*, a*, b* of an 8-bit sRGB colour, quantised to 8 bits the way OpenCV does it (L* in steps of 100/255,
/// a* and b* in whole units). The Python code reads its Lab values from that 8-bit conversion.
List<double> _lab8(int r, int g, int b) {
  final lr = _levels[r], lg = _levels[g], lb = _levels[b];
  final x = (0.412453 * lr + 0.357580 * lg + 0.180423 * lb) / _xn;
  final y = 0.212671 * lr + 0.715160 * lg + 0.072169 * lb;
  final z = (0.019334 * lr + 0.119193 * lg + 0.950227 * lb) / _zn;
  final fy = _f(y);
  final l = y > 0.008856 ? 116 * fy - 16 : 903.3 * y;
  final l8 = (l * 255 / 100 + 0.5).floor().clamp(0, 255);
  final a8 = (500 * (_f(x) - fy) + 128 + 0.5).floor().clamp(0, 255);
  final b8 = (200 * (fy - _f(z)) + 128 + 0.5).floor().clamp(0, 255);
  return [l8 * (100 / 255), a8 - 128.0, b8 - 128.0];
}

/// Linear RGB of a CIE L*a*b* colour (D65), clipped to [0, 1].
List<double> _labToLinearRgb(double l, double a, double b) {
  final fy = (l + 16) / 116, fx = a / 500 + fy, fz = fy - b / 200;
  double inv(double t) => t > 0.206893 ? t * t * t : (t - 16 / 116) / 7.787;
  final x = inv(fx) * _xn, y = inv(fy), z = inv(fz) * _zn;
  return [
    (3.240479 * x - 1.537150 * y - 0.498535 * z).clamp(0.0, 1.0),
    (-0.969256 * x + 1.875991 * y + 0.041556 * z).clamp(0.0, 1.0),
    (0.055648 * x - 0.204043 * y + 1.057311 * z).clamp(0.0, 1.0),
  ];
}

// ---------------------------------------------------------------------------------------------------------------------
// The sticker

int _round(double x) => rint(x).toInt();

/// The sampled pixels of a patch (interleaved R, G, B).
Uint8List _patchPixels(Uint8List rgb, int w, int h, PatchBox p, double sampleFraction) {
  final halfW = p.size * sampleFraction * w / 2, halfH = p.size * sampleFraction * h / 2;
  final x0 = math.max(_round(p.cx * w - halfW), 0), x1 = math.min(_round(p.cx * w + halfW), w);
  final y0 = math.max(_round(p.cy * h - halfH), 0), y1 = math.min(_round(p.cy * h + halfH), h);
  if (x1 <= x0 || y1 <= y0) return Uint8List(0);
  final out = Uint8List((x1 - x0) * (y1 - y0) * 3);
  var k = 0;
  for (var y = y0; y < y1; y++) {
    for (var x = x0; x < x1; x++) {
      final i = 3 * (y * w + x);
      out[k++] = rgb[i];
      out[k++] = rgb[i + 1];
      out[k++] = rgb[i + 2];
    }
  }
  return out;
}

/// Per-channel median; the mean of the two middle values when the count is even.
List<double> _median(Uint8List px) {
  final n = px.length ~/ 3;
  final result = <double>[];
  for (var c = 0; c < 3; c++) {
    final v = Uint8List(n);
    for (var i = 0; i < n; i++) {
      v[i] = px[3 * i + c];
    }
    v.sort();
    result.add(n.isOdd ? v[n ~/ 2].toDouble() : (v[n ~/ 2 - 1] + v[n ~/ 2]) / 2);
  }
  return result;
}

/// Lab of the median colour. The median is cut to whole 8-bit levels first, as the Python code does.
List<double> _medianLab(Uint8List px) {
  final m = _median(px);
  return _lab8(m[0].toInt(), m[1].toInt(), m[2].toInt());
}

/// Reads a straightened sticker picture ([rgb], interleaved R, G, B, [width] x [height]) and returns the correction gains,
/// or a rejection with its reason. A rejected sticker means the caller falls back to the skin pixels alone.
MarkerCheck estimateGains(
  Uint8List rgb,
  int width,
  int height, {
  StickerLayout layout = kStickerLayout,
  StickerReference reference = kStandInReference,
}) {
  if (width <= 0 || height <= 0 || rgb.length != width * height * 3) {
    throw ArgumentError('the sticker picture must be $width x $height RGB bytes');
  }
  MarkerCheck reject(String reason, MarkerCode code) => MarkerCheck(
    used: false,
    reason: reason,
    code: code,
    referenceSource: reference.source,
    calibrationMode: layout.calibrationMode,
  );

  final grey = _patchPixels(rgb, width, height, layout.patches['grey']!, layout.sampleFraction);
  final n = grey.length ~/ 3;
  if (n < kMinPatchPixels) return reject('grey patch has only $n pixels', MarkerCode.cropInvalid);
  var clipped = 0;
  for (var i = 0; i < n; i++) {
    final r = grey[3 * i], g = grey[3 * i + 1], b = grey[3 * i + 2];
    if (math.max<int>(r, math.max<int>(g, b)) >= 250 || math.min<int>(r, math.min<int>(g, b)) <= 5) clipped++;
  }
  if (clipped / n > kMaxClippedFraction) return reject('grey patch clipped (${(100 * clipped / n).round()}% of pixels)', MarkerCode.greyClipped);
  var sum = 0.0, sumSq = 0.0;
  for (var i = 0; i < n; i++) {
    final l = _lab8(grey[3 * i], grey[3 * i + 1], grey[3 * i + 2])[0];
    sum += l;
    sumSq += l * l;
  }
  final mean = sum / n;
  final std = math.sqrt(math.max(sumSq / n - mean * mean, 0));
  if (std > kMaxGreyLStd) return reject('grey patch not uniform (L* std ${std.toStringAsFixed(1)})', MarkerCode.greyNotUniform);

  final greyLab = _medianLab(grey);
  final ref = reference.lab['grey']!;
  final abError = math.sqrt(math.pow(greyLab[1] - ref[1]!, 2) + math.pow(greyLab[2] - ref[2]!, 2));
  if (abError > kMaxGreyAbError) {
    return reject(
      'grey patch not neutral (a*b* ${abError.toStringAsFixed(1)} from the reference, limit ${kMaxGreyAbError.round()})',
      MarkerCode.greyNotNeutral,
    );
  }

  final med = _median(grey);
  final target = _labToLinearRgb(ref[0]!, ref[1]!, ref[2]!);
  final gains = [for (var c = 0; c < 3; c++) target[c] / math.max(srgbToLinear(med[c] / 255.0), 1e-6)];
  final gMin = gains.reduce(math.min), gMax = gains.reduce(math.max);
  if (gMin < kGainMin || gMax > kGainMax) {
    return reject('grey patch gains out of range (${gMin.toStringAsFixed(2)}-${gMax.toStringAsFixed(2)})', MarkerCode.greyGainOutOfRange);
  }

  final luts = [for (final g in gains) gainTable(g)];
  final skinDeltaE = <String, double>{};
  for (final name in layout.patches.keys.where((k) => k.startsWith('skin'))) {
    final want = reference.lab[name];
    if (want == null) continue;
    final px = _patchPixels(rgb, width, height, layout.patches[name]!, layout.sampleFraction);
    if (px.isEmpty) continue;
    for (var i = 0; i < px.length; i++) {
      px[i] = luts[i % 3][px[i]];
    }
    final lab = _medianLab(px);
    var sq = 0.0;
    for (var c = 0; c < 3; c++) {
      final w = want[c];
      if (w != null) sq += (lab[c] - w) * (lab[c] - w);
    }
    skinDeltaE[name] = math.sqrt(sq);
  }
  if (reference.source != 'stand-in' && skinDeltaE.isNotEmpty) {
    final worst = skinDeltaE.entries.reduce((a, b) => a.value >= b.value ? a : b);
    if (worst.value > kMaxSkinDeltaE) {
      return reject(
        '${worst.key} patch off by dE ${worst.value.toStringAsFixed(1)} after correction (limit ${kMaxSkinDeltaE.round()})',
        MarkerCode.skinPatchOff,
      );
    }
  }
  return MarkerCheck(
    used: true,
    gains: gains,
    skinDeltaE: skinDeltaE,
    referenceSource: reference.source,
    calibrationMode: layout.calibrationMode,
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Skin type

// OpenCV's 8-bit RGB to HSV works with these fixed-point tables (hsv_shift 12, hue range 180).
const int _hsvShift = 12;
final Int32List _sDiv = Int32List.fromList([
  0,
  for (var i = 1; i < 256; i++) rint((255 << _hsvShift) / i).toInt(),
]);
final Int32List _hDiv = Int32List.fromList([
  0,
  for (var i = 1; i < 256; i++) rint((180 << _hsvShift) / (6.0 * i)).toInt(),
]);

const List<(String, double, double)> _itaBins = [
  ('I', 55.0, double.infinity),
  ('II', 41.0, 55.0),
  ('III', 28.0, 41.0),
  ('IV', 10.0, 28.0),
  ('V', -30.0, 10.0),
  ('VI', double.negativeInfinity, -30.0),
];

String fitzpatrickOf(double ita) {
  for (final (name, lower, upper) in _itaBins) {
    if (lower < ita && ita <= upper) return name;
  }
  return 'VI';
}

/// Skin type from the mean L* and b* of the skin-coloured pixels of an RGBA picture: hue 0-25 degrees, saturation 15-80 %,
/// brightness at least [vMin] percent, as in the Python code. Fewer than [minSkinPixels] such pixels give no class.
SkinType skinType(Uint8List rgba, int width, int height, {double vMin = kSkinVMin, int minSkinPixels = kMinSkinPixels}) {
  final sOk = [for (var s = 0; s < 256; s++) s / 255.0 * 100.0 >= 15 && s / 255.0 * 100.0 <= 80];
  final vOk = [for (var v = 0; v < 256; v++) v / 255.0 * 100.0 >= vMin && v / 255.0 * 100.0 <= 100];
  var count = 0;
  var sumL = 0.0, sumB = 0.0;
  const half = 1 << (_hsvShift - 1);
  for (var p = 0; p < width * height; p++) {
    final r = rgba[4 * p], g = rgba[4 * p + 1], b = rgba[4 * p + 2];
    final int v = math.max<int>(r, math.max<int>(g, b));
    final int diff = v - math.min<int>(r, math.min<int>(g, b));
    final s = (diff * _sDiv[v] + half) >> _hsvShift;
    if (!sOk[s] || !vOk[v]) continue;
    final vr = v == r ? -1 : 0, vg = v == g ? -1 : 0;
    var h = (vr & (g - b)) + (~vr & ((vg & (b - r + 2 * diff)) + (~vg & (r - g + 4 * diff))));
    h = (h * _hDiv[diff] + half) >> _hsvShift;
    if (h < 0) h += 180;
    if (h * 2 > 25) continue;
    final lab = _lab8(r, g, b);
    sumL += lab[0];
    sumB += lab[2];
    count++;
  }
  if (count < minSkinPixels) return SkinType(skinPixels: count);
  final meanL = sumL / count, meanB = sumB / count;
  final ita = math.atan2(meanL - 50, meanB) * 180 / math.pi;
  return SkinType(fitzpatrick: fitzpatrickOf(ita), ita: ita, meanL: meanL, meanB: meanB, skinPixels: count);
}

// ---------------------------------------------------------------------------------------------------------------------
// The calibrator

/// Grey-patch exposure and white balance, then the skin type on the corrected picture. Without a usable sticker the
/// picture is returned unchanged and the skin type is read from its own pixels, with a note that says so.
class LightCalibrator implements Calibrator {
  const LightCalibrator({this.layout = kStickerLayout, this.reference = kStandInReference, this.vMin = kSkinVMin});

  final StickerLayout layout;
  final StickerReference reference;
  final double vMin;

  @override
  CalibrationResult calibrate(Uint8List rgba, int width, int height, {Uint8List? markerRgb}) {
    final notes = <String>[];
    List<double>? gains;
    String? label;
    if (markerRgb != null) {
      final side = math.sqrt(markerRgb.length / 3).round();
      if (side * side * 3 != markerRgb.length) {
        notes.add('the sticker picture has an unexpected size, using the skin pixels only');
      } else {
        final check = estimateGains(markerRgb, side, side, layout: layout, reference: reference);
        if (check.used) {
          gains = check.gains;
          label = check.label;
        } else {
          notes.add('marker rejected, using skin-pixel fallback: ${check.reason}');
        }
      }
    }
    final image = gains == null ? rgba : applyGains(rgba, gains);
    if (label != null) notes.add('skin type read after $label');
    final skin = skinType(image, width, height, vMin: vMin);
    if (skin.fitzpatrick == null) notes.add('no skin type: only ${skin.skinPixels} skin-coloured pixels were found');
    return CalibrationResult(
      image,
      fitzpatrickClass: skin.fitzpatrick,
      version: gains == null ? '$kLightCalibrationVersion+skin-pixels' : '$kLightCalibrationVersion+grey-patch',
      markerUsed: gains != null,
      markerLabel: label,
      itaAngle: skin.ita,
      notes: notes,
    );
  }
}
