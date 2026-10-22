import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/sticker_gate.dart';

/// Photos and answers made by research/IT23294066/on-device-export/make_gate_fixtures.py from the Python finder.
const _dir = 'test/measurement/golden/gate';

Future<(Uint8List, int, int)> _decode(String path) async {
  final codec = await ui.instantiateImageCodec(File(path).readAsBytesSync());
  final image = (await codec.getNextFrame()).image;
  final data = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
  return (data!.buffer.asUint8List(), image.width, image.height);
}

Uint8List _rgba(int w, int h, void Function(int x, int y, List<int> px) paint) {
  final out = Uint8List(w * h * 4);
  final px = [0, 0, 0];
  for (var y = 0; y < h; y++) {
    for (var x = 0; x < w; x++) {
      px[0] = px[1] = px[2] = 255;
      paint(x, y, px);
      final i = (y * w + x) * 4;
      out[i] = px[0];
      out[i + 1] = px[1];
      out[i + 2] = px[2];
      out[i + 3] = 255;
    }
  }
  return out;
}

void main() {
  group('gray and shrink', () {
    test('gray uses the luma weights OpenCV uses, rounded', () {
      final g = toGray(Uint8List.fromList([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 200, 100, 50, 255]), 4, 1);
      expect(g, [76, 150, 29, 124]);
    });

    test('shrinking by 2 averages each 2x2 block, and a photo within the limit is not resized', () {
      final src = Uint8List.fromList([0, 100, 200, 40, 100, 200, 40, 0, 10, 20, 30, 40, 50, 60, 70, 80]);
      final (small, w, h, scale) = shrinkGray(src, 4, 4, 2);
      expect((w, h, scale), (2, 2, 0.5));
      expect(small, [100, 70, 35, 55]);
      final (same, w2, h2, s2) = shrinkGray(src, 4, 4, 8);
      expect((w2, h2, s2), (4, 4, 1.0));
      expect(same, src);
    });
  });

  group('finder codes', () {
    test('are the list agreed with Color Matric, as the words the Python finder uses', () {
      expect([for (final c in FinderCode.values) c.code],
          ['no_candidate', 'cut_by_edge', 'weak_frame_contrast', 'weak_orientation', 'layout_mismatch', 'mirrored']);
    });
  });

  group('synthetic photos', () {
    test('a blank photo has no candidate', () {
      final scan = diagnoseStickers(_rgba(400, 300, (x, y, p) => p[0] = p[1] = p[2] = 140), 400, 300);
      expect((scan.found, scan.code, scan.candidates), (false, FinderCode.noCandidate, 0));
    });
  });

  group('the straightened sticker picture', () {
    testWidgets('has the patches where the layout puts them, whatever the turn and tilt of the photo', (tester) async {
      await tester.runAsync(() async {
        for (final name in ['sim_one_ok', 'sim_six_tilt25', 'real_iphone_tilt']) {
          final (rgba, w, h) = await _decode('$_dir/$name.jpg');
          final scan = diagnoseStickers(rgba, w, h);
          expect(scan.found, isTrue, reason: name);
          final crop = straightenSticker(rgba, w, h, scan.stickers.first);
          expect(crop.length, 500 * 500 * 3, reason: name);
          double mean(double cx, double cy) {
            var sum = 0.0;
            for (var y = (cy - 3) * 10; y < (cy + 3) * 10; y++) {
              for (var x = (cx - 3) * 10; x < (cx + 3) * 10; x++) {
                final i = (y.toInt() * 500 + x.toInt()) * 3;
                sum += (crop[i] + crop[i + 1] + crop[i + 2]) / 3;
              }
            }
            return sum / 36 / 100;
          }

          expect(mean(8, 8), lessThan(80), reason: '$name: the dark orientation square is at the top left');
          expect(mean(42, 8), greaterThan(180), reason: '$name: the other inner corners are white');
          expect(mean(25, 12.5), greaterThan(180), reason: '$name: white above the middle patch');
          expect(mean(12.5, 25), inInclusiveRange(60, 190), reason: '$name: the grey patch is mid tone');
          expect(mean(37.5, 25), lessThan(150), reason: '$name: the darkest skin patch');
        }
      });
    });
  });

  group('the Python finder is the reference', () {
    final meta = jsonDecode(File('$_dir/gate_expected.json').readAsStringSync()) as Map<String, dynamic>;
    final cases = (meta['cases'] as Map<String, dynamic>).cast<String, Map<String, dynamic>>();

    for (final e in cases.entries) {
      testWidgets('${e.key}: same verdict and the same stickers', (tester) async {
        await tester.runAsync(() async {
          final (rgba, w, h) = await _decode('$_dir/${e.key}.jpg');
          expect((w, h), (e.value['width'], e.value['height']));
          final watch = Stopwatch()..start();
          final scan = diagnoseStickers(rgba, w, h, maxDim: meta['max_dim'] as int);
          watch.stop();
          // ignore: avoid_print
          print('${e.key}: ${scan.code?.code ?? 'found ${scan.stickers.length}'}  ${watch.elapsedMilliseconds} ms');
          expect(scan.code?.code, e.value['code'], reason: 'the code');
          expect(scan.stickers.length, e.value['found'], reason: 'how many stickers');
          final want = (e.value['stickers'] as List).cast<Map<String, dynamic>>();
          for (final s in want) {
            final wc = (s['corners'] as List).map((c) => ((c as List)[0] as num).toDouble()).toList();
            final wy = (s['corners'] as List).map((c) => ((c as List)[1] as num).toDouble()).toList();
            final cx = wc.reduce((a, b) => a + b) / 4, cy = wy.reduce((a, b) => a + b) / 4;
            final near = scan.stickers.reduce((a, b) => _dist(a.centre, (cx, cy)) < _dist(b.centre, (cx, cy)) ? a : b);
            expect(_dist(near.centre, (cx, cy)), lessThan(8), reason: 'sticker centre');
            // corners are refined to the half-light edge like the Python finder, so they agree to a pixel or two
            final err = [for (var i = 0; i < 4; i++) _dist(near.corners[i], (wc[i], wy[i]))].reduce(math.max);
            expect(err, lessThan(3.0), reason: 'corners, largest miss $err px');
            expect(near.pxPerMm / (s['px_per_mm'] as num).toDouble(), closeTo(1.0, 0.025), reason: 'scale (px per mm) within 2.5% (these photos are small: one pixel of a corner is 0.7%)');
          }
          for (final s in scan.stickers) {
            expect(s.frameContrast, greaterThan(0.5));
            expect(s.orientationContrast, greaterThan(0.5));
          }
          expect(scan.bestFrameContrast, inInclusiveRange(0, 1));
          expect(scan.bestOrientationContrast, inInclusiveRange(0, 1));
        });
      });
    }
  });
}

double _dist((double, double) a, (double, double) b) => math.sqrt(math.pow(a.$1 - b.$1, 2) + math.pow(a.$2 - b.$2, 2));
