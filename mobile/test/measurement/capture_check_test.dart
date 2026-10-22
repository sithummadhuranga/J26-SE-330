import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_check.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';

const _dir = 'test/measurement/golden/gate';

Future<(Uint8List, int, int)> _decode(String path) async {
  final codec = await ui.instantiateImageCodec(File(path).readAsBytesSync());
  final image = (await codec.getNextFrame()).image;
  final data = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
  return (data!.buffer.asUint8List(), image.width, image.height);
}

void main() {
  final meta = (jsonDecode(File('$_dir/gate_expected.json').readAsStringSync()) as Map<String, dynamic>)['cases'] as Map<String, dynamic>;

  test('a block average shrinks a big photo and leaves a small one alone', () {
    final big = Uint8List(8 * 4 * 4);
    for (var i = 0; i < 8 * 4; i++) {
      big[i * 4] = i % 8 < 4 ? 200 : 100;
      big[i * 4 + 3] = 255;
    }
    final (small, w, h) = shrinkRgba(big, 8, 4, 4);
    expect((w, h), (4, 2));
    expect([small[0], small[4], small[8], small[12]], [200, 200, 100, 100]);
    final (same, w2, h2) = shrinkRgba(big, 8, 4, 640);
    expect((w2, h2, identical(same, big)), (8, 4, true));
  });

  group('the whole check on the fixture photos', () {
    for (final e in meta.entries) {
      testWidgets(e.key, (tester) async {
        await tester.runAsync(() async {
          final (rgba, w, h) = await _decode('$_dir/${e.key}.jpg');
          final watch = Stopwatch()..start();
          final c = checkCapture(rgba, w, h);
          watch.stop();
          // ignore: avoid_print
          print('${e.key}: ${c.verdict.verdict.name} ${c.verdict.code ?? '-'}  marker=${c.marker?.code?.code ?? (c.marker == null ? 'n/a' : 'used')}  skin=${c.skin?.skinPixels}  ${watch.elapsedMilliseconds} ms');
          if (e.value['code'] != null) {
            expect(c.verdict.verdict, Verdict.retake);
            expect(c.verdict.code, e.value['code']);
            expect(c.marker, isNull, reason: 'no sticker, nothing to read');
          } else {
            expect(c.verdict.verdict, isNot(Verdict.retake), reason: 'a sticker was found');
            expect(c.marker, isNotNull);
            expect(c.scan.stickers, isNotEmpty);
          }
        });
      });
    }
  });
}
