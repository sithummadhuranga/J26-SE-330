import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/assessment/media_store.dart';

WoundOutline _outline() {
  final mask = Uint8List(640 * 480);
  for (var y = 100; y < 120; y++) {
    for (var x = 200; x < 250; x++) {
      mask[y * 640 + x] = 1;
    }
  }
  return WoundOutline(mask: mask, scale: 0.5, padLeft: 0, padTop: 0, originalWidth: 1280, originalHeight: 960);
}

void main() {
  late Directory dir;

  setUp(() => dir = Directory.systemTemp.createTempSync('media_test_'));
  tearDown(() => dir.deleteSync(recursive: true));

  test('a saved photo and outline come back unchanged from the phone\'s own folder', () async {
    final store = FileMediaStore(dir);
    final photo = Uint8List.fromList([1, 2, 3, 4]);
    await store.save('a-1', photo, _outline());
    final back = await store.load('a-1');
    expect(back, isNotNull);
    expect(back!.photo, photo);
    final o = back.outline!;
    expect((o.scale, o.padLeft, o.padTop, o.originalWidth, o.originalHeight), (0.5, 0, 0, 1280, 960));
    expect(o.mask, _outline().mask);
  });

  test('a photo without an outline is stored too, and an unknown id gives null', () async {
    final store = FileMediaStore(dir);
    await store.save('b-2', Uint8List.fromList([9]), null);
    final back = await store.load('b-2');
    expect(back!.photo, [9]);
    expect(back.outline, isNull);
    expect(await store.load('nope'), isNull);
  });

  test('ids that could climb out of the folder are refused', () async {
    final store = FileMediaStore(dir);
    expect(() => store.save('../evil', Uint8List(1), null), throwsArgumentError);
    expect(() => store.load('a/b'), throwsArgumentError);
  });

  test('the memory store keeps things for the tests and the prototype', () async {
    final store = MemoryMediaStore();
    await store.save('c', Uint8List.fromList([5]), _outline());
    expect((await store.load('c'))!.photo, [5]);
    expect(await store.load('d'), isNull);
  });

  group('the outline', () {
    test('its boundary pixels are mapped to fractions of the original photo', () {
      final pts = _outline().boundaryFractions();
      // 50 x 20 block: the boundary is the pixels with an empty neighbour = the 136 around the edge
      expect(pts.length, 2 * 50 + 2 * 18);
      final xs = pts.map((p) => p.$1), ys = pts.map((p) => p.$2);
      // canvas x 200..249 at scale 0.5 -> photo x 400..499 of 1280
      expect(xs.reduce((a, b) => a < b ? a : b), closeTo(400.5 / 1280 * 1.0 - 0.5 / 1280 + 0.0, 0.002));
      expect(xs.reduce((a, b) => a > b ? a : b), closeTo(499.5 / 1280 + 1 / 1280, 0.003));
      expect(ys.reduce((a, b) => a < b ? a : b), closeTo(200 / 960, 0.003));
      for (final p in pts) {
        expect(p.$1, inInclusiveRange(0, 1));
        expect(p.$2, inInclusiveRange(0, 1));
      }
    });

    test('padding of a letterboxed portrait photo is taken off before the fractions are made', () {
      final mask = Uint8List(640 * 480);
      for (var y = 100; y < 110; y++) {
        for (var x = 300; x < 310; x++) {
          mask[y * 640 + x] = 1;
        }
      }
      // photo 900 x 1200 shrunk by 0.4 to 360 x 480 and centred: padLeft = (640 - 360) / 2 = 140
      final o = WoundOutline(mask: mask, scale: 0.4, padLeft: 140, padTop: 0, originalWidth: 900, originalHeight: 1200);
      final pts = o.boundaryFractions();
      final minX = pts.map((p) => p.$1).reduce((a, b) => a < b ? a : b);
      expect(minX, closeTo((300 - 140) / 0.4 / 900, 0.004));
    });

    test('an empty mask has no boundary', () {
      expect(WoundOutline(mask: Uint8List(640 * 480), scale: 1, padLeft: 0, padTop: 0, originalWidth: 640, originalHeight: 480).boundaryFractions(), isEmpty);
    });
  });
}
