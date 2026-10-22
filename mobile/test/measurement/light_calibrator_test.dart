import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';

/// Compares the Dart light calibration with the Python code of the colour component on its golden vectors
/// (research/IT23343702/colorimetric-calibration/tests/golden, synthetic pictures only). The tests are skipped when that
/// folder is not in the checkout.
const _goldenDir = '../research/IT23343702/colorimetric-calibration/tests/golden';

/// The code each rejected golden crop must carry (Color Matric's message 10 list).
const _markerCodeOf = {
  'r01_grey_clipped': 'grey_clipped',
  'r02_grey_uneven': 'grey_not_uniform',
  'r03_too_small': 'crop_invalid',
  'r04_grey_not_neutral': 'grey_not_neutral',
  'r05_gain_out_of_range': 'grey_gain_out_of_range',
  'r06_skin_patch_off': 'skin_patch_off',
};
const _layoutFile = '../research/IT23294066/area-measurement/marker_layout.json';

class Png {
  Png(this.rgba, this.w, this.h);

  final Uint8List rgba;
  final int w, h;

  Uint8List get rgb => Uint8List.fromList([
    for (var i = 0; i < w * h; i++) ...[rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]],
  ]);
}

Future<Png> readPng(String path) async {
  final codec = await ui.instantiateImageCodec(await File(path).readAsBytes());
  final image = (await codec.getNextFrame()).image;
  final data = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
  return Png(data!.buffer.asUint8List(), image.width, image.height);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final haveGolden = Directory(_goldenDir).existsSync();
  final skip = haveGolden ? null : 'the colour component golden vectors are not in this checkout';
  late final Map<String, dynamic> expected;
  if (haveGolden) expected = jsonDecode(File('$_goldenDir/expected.json').readAsStringSync()) as Map<String, dynamic>;

  test('the bundled layout is the one in marker_layout.json', () {
    final d = jsonDecode(File(_layoutFile).readAsStringSync()) as Map<String, dynamic>;
    final side = (d['side_mm'] as num).toDouble();
    expect(d['use_central_fraction_of_patch'], kStickerLayout.sampleFraction);
    expect(d['calibration_mode'], kStickerLayout.calibrationMode);
    final patches = d['patches'] as List<dynamic>;
    expect(patches.map((p) => p['name']).toList(), kStickerLayout.patches.keys.toList());
    for (final p in patches) {
      final box = kStickerLayout.patches[p['name']]!;
      expect(box.cx, (p['centre'][0] as num) / side);
      expect(box.cy, (p['centre'][1] as num) / side);
      expect(box.size, (p['size'] as num) / side);
    }
  });

  test('the gain tables match the Python tables on all 256 entries, for every channel and gain set', () {
    final cases = (jsonDecode(File('test/measurement/golden/lut_full.json').readAsStringSync())
        as Map<String, dynamic>)['cases'] as List<dynamic>;
    expect(cases.length, greaterThan(5));
    for (final c in cases) {
      final gains = (c['gains'] as List<dynamic>).map((g) => (g as num).toDouble()).toList();
      for (var ch = 0; ch < 3; ch++) {
        expect(gainTable(gains[ch]).toList(), (c['lut'][ch] as List<dynamic>).cast<int>(), reason: 'gains $gains channel $ch');
      }
    }
  });

  test('rounding is half to even', () {
    expect([0.5, 1.5, 2.5, 3.5, -0.5, 2.4, 2.6].map(rint).toList(), [0.0, 2.0, 2.0, 4.0, -0.0, 2.0, 3.0]);
  });

  test('sticker pictures with a good grey patch give the Python gains', () async {
    final entries = (expected['grey_gains'] as Map<String, dynamic>).entries.where((e) => e.value['used'] == true);
    expect(entries.length, greaterThanOrEqualTo(6)); // c01..c05 and the 400 px sticker c06
    for (final e in entries) {
      final crop = await readPng('$_goldenDir/crops/${e.key}.png');
      final r = estimateGains(crop.rgb, crop.w, crop.h);
      expect(r.used, isTrue, reason: '${e.key}: ${r.reason}');
      final want = (e.value['gains'] as List<dynamic>).map((g) => (g as num).toDouble()).toList();
      for (var c = 0; c < 3; c++) {
        expect(r.gains![c], closeTo(want[c], (e.value['gains_tolerance'] as num).toDouble()), reason: e.key);
      }
      expect(r.label, e.value['label']);
      final dE = (e.value['skin_delta_e'] as Map<String, dynamic>);
      for (final k in dE.keys) {
        // The error is read from an 8-bit Lab, and the Dart rounding can differ from OpenCV's by one level in a*, b* (and
        // 0.4 in L*), which is up to about 1.5 in dE (c06 skin_a: Python 1.41, Dart 0.04). It is only reported, and for a
        // stand-in reference it never rejects.
        expect(r.skinDeltaE[k]!, closeTo((dE[k] as num).toDouble(), 1.5), reason: '${e.key} $k');
      }
    }
  }, skip: skip);

  test('each rejected sticker is rejected for the stated reason', () async {
    final entries = (expected['grey_gains'] as Map<String, dynamic>).entries.where((e) => e.value['used'] == false);
    expect(entries.length, 6);
    final photoRef = StickerReference.fromJson(
      jsonDecode(File('$_goldenDir/reference_photos_example.json').readAsStringSync()) as Map<String, dynamic>,
    );
    for (final e in entries) {
      final crop = await readPng('$_goldenDir/crops/${e.key}.png');
      final needsPhotoRef = (e.value['reference'] as String).startsWith('reference_photos');
      final r = estimateGains(crop.rgb, crop.w, crop.h, reference: needsPhotoRef ? photoRef : kStandInReference);
      expect(r.used, isFalse, reason: e.key);
      expect(r.reason, contains(e.value['reason_contains'] as String), reason: '${e.key}: ${r.reason}');
      expect(r.code?.code, _markerCodeOf[e.key], reason: '${e.key}: the reason code');
    }
  }, skip: skip);

  test('an accepted sticker has no reason code, and the codes are the agreed list', () async {
    final crop = await readPng('$_goldenDir/crops/${(expected['grey_gains'] as Map<String, dynamic>).entries.firstWhere((e) => e.value['used'] == true).key}.png');
    expect(estimateGains(crop.rgb, crop.w, crop.h).code, isNull);
    expect([for (final c in MarkerCode.values) c.code], [
      'grey_clipped', 'grey_not_uniform', 'grey_not_neutral', 'grey_gain_out_of_range', 'skin_patch_off', 'crop_invalid',
    ]);
  }, skip: skip);

  test('too few skin pixels carries the code Color Matric named', () {
    final tiny = Uint8List(4 * 4 * 4)..fillRange(0, 64, 255);
    final r = skinType(tiny, 4, 4);
    expect(r.fitzpatrick, isNull);
    expect(r.code?.code, 'too_few_skin_pixels');
  });

  test('the skin type matches the Python class and angle on the golden photos', () async {
    final skin = expected['skin_type'] as Map<String, dynamic>;
    for (final e in skin.entries) {
      final photo = await readPng('$_goldenDir/photos/${e.key}.png');
      final r = skinType(photo.rgba, photo.w, photo.h);
      if (e.value['error'] != null) {
        expect(r.fitzpatrick, isNull, reason: e.key);
        continue;
      }
      // Close to a class boundary a port within 0.5 degrees may give the other class, so compare the angle only.
      final boundary = e.value['boundary_case'] != null;
      if (!boundary) expect(r.fitzpatrick, e.value['fitzpatrick'], reason: e.key);
      expect(r.ita!, closeTo((e.value['ita_opencv8'] as num).toDouble(), (e.value['tolerance']['ita_deg'] as num).toDouble()), reason: e.key);
      expect(r.skinPixels, closeTo((e.value['skin_pixel_count'] as num).toDouble(), (e.value['tolerance']['pixel_count'] as num).toDouble()),
          reason: e.key);
      if (!boundary) expect(r.ita! <= 28.0, e.value['gate_dark_at_28'], reason: e.key);
    }
  }, skip: skip);

  test('the old brightness limit of 35 drops the very dark photo, the new default keeps it', () async {
    final photo = await readPng('$_goldenDir/photos/p05_very_dark_V_under_35.png');
    expect(skinType(photo.rgba, photo.w, photo.h).fitzpatrick, 'VI');
    expect(skinType(photo.rgba, photo.w, photo.h, vMin: 35.0).fitzpatrick, isNull);
  }, skip: skip);

  test('applying gains gives the Python picture byte for byte', () async {
    final src = await readPng('$_goldenDir/photos/p02_medium_skin.png');
    for (final e in (expected['apply_gains'] as Map<String, dynamic>).entries) {
      final want = await readPng('$_goldenDir/${e.value['output']}');
      final gains = (e.value['gains'] as List<dynamic>).map((g) => (g as num).toDouble()).toList();
      final got = applyGains(src.rgba, gains);
      expect(got.length, want.rgba.length);
      var different = 0;
      for (var i = 0; i < got.length; i++) {
        if (got[i] != want.rgba[i]) different++;
      }
      expect(different, 0, reason: '${e.key}: $different bytes differ');
    }
  }, skip: skip);

  group('LightCalibrator', () {
    late Png photo, goodCrop, clippedCrop;
    setUpAll(() async {
      if (!haveGolden) return;
      photo = await readPng('$_goldenDir/photos/p02_medium_skin.png');
      goodCrop = await readPng('$_goldenDir/crops/c03_warm_light.png');
      clippedCrop = await readPng('$_goldenDir/crops/r01_grey_clipped.png');
    });

    test('with a good sticker it corrects the photo, reads the skin type and says what it rests on', () {
      final r = const LightCalibrator().calibrate(photo.rgba, photo.w, photo.h, markerRgb: goodCrop.rgb);
      final gains = estimateGains(goodCrop.rgb, goodCrop.w, goodCrop.h).gains!;
      expect(r.markerUsed, isTrue);
      expect(r.markerLabel, 'relative calibration, stand-in reference colours');
      expect(r.rgba, applyGains(photo.rgba, gains));
      expect(r.version, 'light-1+grey-patch');
      expect(r.fitzpatrickClass, isNotNull);
      expect(r.itaAngle, isNotNull);
      expect(r.notes, contains('skin type read after relative calibration, stand-in reference colours'));
    }, skip: skip);

    test('a 400 x 400 sticker picture works with a 640 x 480 photo, and the photo keeps its size', () async {
      final small = await readPng('$_goldenDir/crops/c06_dim_400px.png');
      expect([photo.w, photo.h], [640, 480]);
      expect([small.w, small.h], [400, 400]);
      final r = const LightCalibrator().calibrate(photo.rgba, photo.w, photo.h, markerRgb: small.rgb);
      expect(r.markerUsed, isTrue);
      expect(r.rgba.length, 640 * 480 * 4); // what the model gets is still 640 x 480
    }, skip: skip);

    test('a rejected sticker leaves the photo unchanged and says why', () {
      final r = const LightCalibrator().calibrate(photo.rgba, photo.w, photo.h, markerRgb: clippedCrop.rgb);
      expect(r.markerUsed, isFalse);
      expect(r.rgba, photo.rgba);
      expect(r.version, 'light-1+skin-pixels');
      expect(r.notes.single, startsWith('marker rejected, using skin-pixel fallback: grey patch clipped'));
    }, skip: skip);

    test('without a sticker it reads the skin type from the photo itself', () {
      final r = const LightCalibrator().calibrate(photo.rgba, photo.w, photo.h);
      final direct = skinType(photo.rgba, photo.w, photo.h);
      expect(r.markerUsed, isFalse);
      expect(r.markerLabel, isNull);
      expect(r.fitzpatrickClass, direct.fitzpatrick);
      expect(r.notes, isEmpty);
    }, skip: skip);

    test('a sticker picture of an unexpected size is ignored with a note', () {
      final r = const LightCalibrator().calibrate(photo.rgba, photo.w, photo.h, markerRgb: Uint8List(1000));
      expect(r.markerUsed, isFalse);
      expect(r.notes.single, contains('unexpected size'));
    }, skip: skip);

    test('a photo with no skin gives no class and a note', () async {
      final shadow = await readPng('$_goldenDir/photos/p06_black_shadow_only.png');
      final r = const LightCalibrator().calibrate(shadow.rgba, shadow.w, shadow.h);
      expect(r.fitzpatrickClass, isNull);
      expect(r.notes.single, startsWith('no skin type'));
    }, skip: skip);
  });
}
