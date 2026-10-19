import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/colour_regions.dart';

/// The first group of tests uses tiny made-up images. The last one compares with the Python reference using golden files
/// from research/IT23294066/colour-regions/colour_regions.py --write (they come from DFUC2022 photos, which we may not
/// share, so set WOUND_GOLDEN_DIR to run it; otherwise it is skipped).
void main() {
  // [colours] lists RGB values; pixel i gets colours[i * colours.length ~/ n]. All pixels are inside the mask.
  (Uint8List, Uint8List) image(List<List<int>> colours, int n) {
    final px = Uint8List(n * 3);
    for (var i = 0; i < n; i++) {
      final c = colours[i * colours.length ~/ n];
      px.setRange(3 * i, 3 * i + 3, c);
    }
    return (px, Uint8List.fromList(List.filled(n, 1)));
  }

  test('an empty mask has no regions', () {
    expect(colourRegions(Uint8List(3 * 4), Uint8List(4), channels: 3), isEmpty);
  });

  test('two colours in a 3 to 1 split give two regions of 75 and 25 percent', () {
    final (px, mask) = image([
      [200, 20, 20],
      [200, 20, 20],
      [200, 20, 20],
      [20, 20, 200],
    ], 400);
    final r = colourRegions(px, mask, channels: 3);
    expect(r.map((e) => e.cluster), [1, 2]);
    expect(r.map((e) => e.percent), [75.0, 25.0]);
    expect(r[0].centre, [200.0, 20.0, 20.0]);
    expect(r[1].centre, [20.0, 20.0, 200.0]);
  });

  test('three colours give three regions, largest first, adding up to 100', () {
    final (px, mask) = image([
      [10, 10, 10],
      [10, 10, 10],
      [10, 10, 10],
      [10, 10, 10],
      [120, 120, 120],
      [120, 120, 120],
      [240, 240, 240],
    ], 700);
    final r = colourRegions(px, mask, channels: 3);
    expect(r.map((e) => e.percent), [57.1, 28.6, 14.3]);
    expect(r.fold<double>(0, (a, e) => a + e.percent), closeTo(100.0, 1e-9));
  });

  test('pixels outside the mask are ignored and RGBA input works', () {
    final px = Uint8List.fromList([255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);
    final r = colourRegions(px, Uint8List.fromList([1, 0, 0]));
    expect(r.length, 1);
    expect(r[0].percent, 100.0);
    expect(r[0].centre, [255.0, 0.0, 0.0]);
  });

  test('the same input always gives the same answer', () {
    final px = Uint8List.fromList([for (var i = 0; i < 3 * 5000; i++) (i * 7919 + i ~/ 3 * 31) % 256]);
    final mask = Uint8List.fromList(List.filled(5000, 1));
    final a = colourRegions(px, mask, channels: 3), b = colourRegions(px, mask, channels: 3);
    expect([for (final e in a) e.percent], [for (final e in b) e.percent]);
    expect(a.fold<double>(0, (s, e) => s + e.percent), closeTo(100.0, 1e-9));
  });

  final dir = Platform.environment['WOUND_GOLDEN_DIR'];
  test('matches the Python reference on golden wounds', () {
    final manifest = jsonDecode(File('$dir/manifest.json').readAsStringSync()) as Map<String, dynamic>;
    var checked = 0;
    for (final img in (manifest['images'] as List).cast<Map<String, dynamic>>()) {
      final id = img['id'] as String;
      final golden = jsonDecode(File('$dir/$id.regions.json').readAsStringSync()) as List;
      final got = colourRegions(File('$dir/$id.rgb.u8').readAsBytesSync(), File('$dir/$id.union.u8').readAsBytesSync(), channels: 3);
      expect(got.length, golden.length, reason: 'number of regions for $id');
      for (var i = 0; i < got.length; i++) {
        final want = golden[i] as Map<String, dynamic>;
        expect(got[i].cluster, want['cluster']);
        expect(got[i].percent, closeTo((want['percent'] as num).toDouble(), 0.101), reason: 'percent $i of $id');
        final c = (want['centre'] as List).cast<num>();
        for (var ch = 0; ch < 3; ch++) {
          expect(got[i].centre[ch], closeTo(c[ch].toDouble(), 0.06), reason: 'centre $i of $id');
        }
      }
      checked++;
    }
    // ignore: avoid_print
    print('$checked golden wounds checked');
  }, skip: dir == null ? 'set WOUND_GOLDEN_DIR to run' : false);
}
