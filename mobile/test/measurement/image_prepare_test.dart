import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/image_prepare.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

/// Most tests use made-up images. The last one compares with the Python reference on large photos made from a DFUC2022
/// image (research use only, so the files stay outside the repo): set WOUND_GOLDEN_DIR to the golden folder to run it.
void main() {
  Uint8List solid(int w, int h, List<int> rgb) {
    final px = Uint8List(w * h * 4);
    for (var p = 0; p < w * h; p++) {
      px.setRange(4 * p, 4 * p + 4, [...rgb, 255]);
    }
    return px;
  }

  List<int> at(PreparedImage img, int x, int y) => img.rgba.sublist(4 * (y * kInputW + x), 4 * (y * kInputW + x) + 3);

  test('a 640x480 photo is copied unchanged', () {
    final px = Uint8List.fromList([for (var i = 0; i < 640 * 480 * 4; i++) (i * 13) % 256]);
    for (var p = 0; p < 640 * 480; p++) {
      px[4 * p + 3] = 255;
    }
    final img = prepareImage(px, 640, 480);
    expect(img.scale, 1.0);
    expect([img.padLeft, img.padTop], [0, 0]);
    expect(img.rgba, px);
  });

  test('a large 4:3 photo fills the canvas and keeps its colour', () {
    final img = prepareImage(solid(4000, 3000, [200, 100, 50]), 4000, 3000);
    expect(img.scale, closeTo(0.16, 1e-12));
    expect([img.padLeft, img.padTop], [0, 0]);
    expect(at(img, 0, 0), [200, 100, 50]);
    expect(at(img, 639, 479), [200, 100, 50]);
  });

  test('a portrait photo gets grey borders left and right and keeps its proportions', () {
    final img = prepareImage(solid(3000, 4000, [10, 200, 10]), 3000, 4000);
    expect(img.scale, closeTo(0.12, 1e-12));
    expect([img.padLeft, img.padTop], [140, 0]);
    expect(at(img, 10, 240), [114, 114, 114]);
    expect(at(img, 320, 240), [10, 200, 10]);
    expect(at(img, 630, 240), [114, 114, 114]);
    final (ox, oy) = img.toOriginal(140 + 180, 240);
    expect([ox, oy], [1500.0, 2000.0]);
  });

  test('shrinking averages instead of picking pixels', () {
    // 1280x960 photo made of black and white columns, 1 pixel each: every canvas pixel covers 2 columns, so it is grey.
    final px = Uint8List(1280 * 960 * 4);
    for (var y = 0; y < 960; y++) {
      for (var x = 0; x < 1280; x++) {
        final v = x.isEven ? 0 : 254;
        px.setRange(4 * (y * 1280 + x), 4 * (y * 1280 + x) + 4, [v, v, v, 255]);
      }
    }
    final img = prepareImage(px, 1280, 960);
    expect(at(img, 100, 100), [127, 127, 127]);
  });

  test('a small photo is enlarged without changing its colour', () {
    final img = prepareImage(solid(320, 240, [30, 60, 90]), 320, 240);
    expect(img.scale, 2.0);
    expect(at(img, 5, 5), [30, 60, 90]);
    expect(at(img, 634, 474), [30, 60, 90]);
  });

  test('a wrong buffer size is refused', () {
    expect(() => prepareImage(Uint8List(10), 640, 480), throwsArgumentError);
  });

  final dir = Platform.environment['WOUND_GOLDEN_DIR'];
  test('matches the Python reference on large photos', () {
    final meta = jsonDecode(File('$dir/resize_manifest.json').readAsStringSync()) as Map<String, dynamic>;
    for (final name in meta.keys) {
      final m = meta[name] as Map<String, dynamic>;
      final w = m['width'] as int, h = m['height'] as int;
      final rgb = File('$dir/resize_$name.big.rgb.u8').readAsBytesSync();
      final rgba = Uint8List(w * h * 4);
      for (var p = 0; p < w * h; p++) {
        rgba[4 * p] = rgb[3 * p];
        rgba[4 * p + 1] = rgb[3 * p + 1];
        rgba[4 * p + 2] = rgb[3 * p + 2];
        rgba[4 * p + 3] = 255;
      }
      final img = prepareImage(rgba, w, h);
      expect(img.scale, closeTo((m['scale'] as num).toDouble(), 1e-12), reason: '$name scale');
      expect([img.padLeft, img.padTop], [m['pad_left'], m['pad_top']], reason: '$name padding');
      final want = File('$dir/resize_$name.canvas.rgb.u8').readAsBytesSync();
      var maxDiff = 0, sum = 0;
      for (var p = 0; p < kInputW * kInputH; p++) {
        for (var c = 0; c < 3; c++) {
          final d = (img.rgba[4 * p + c] - want[3 * p + c]).abs();
          sum += d;
          if (d > maxDiff) maxDiff = d;
        }
      }
      // ignore: avoid_print
      print('$name: largest colour difference $maxDiff, mean ${(sum / (3 * kInputW * kInputH)).toStringAsFixed(4)}');
      expect(maxDiff, lessThanOrEqualTo(1), reason: '$name differs from OpenCV by more than rounding');
    }
  }, skip: dir == null ? 'set WOUND_GOLDEN_DIR to run' : false);
}
