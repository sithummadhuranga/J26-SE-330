import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_runner.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/decoded_photo.dart';

const _dir = 'test/measurement/golden/gate';

void main() {
  testWidgets('a photo file goes through decoding, the background check and the verdict', (tester) async {
    await tester.runAsync(() async {
      final good = await checkEncodedPhoto(File('$_dir/real_iphone_tilt.jpg').readAsBytesSync());
      expect((good.width, good.height), (900, 1200));
      expect(good.check.verdict.verdict, isNot(Verdict.retake));
      expect(good.check.scan.found, isTrue);

      final glare = await checkEncodedPhoto(File('$_dir/real_glare_a.jpg').readAsBytesSync());
      expect(glare.check.verdict.verdict, Verdict.retake);
      expect(glare.check.verdict.code, 'weak_frame_contrast');
    });
  });

  testWidgets('bytes that are not a picture are reported, not swallowed', (tester) async {
    await tester.runAsync(() async {
      await expectLater(checkEncodedPhoto(Uint8List.fromList([1, 2, 3, 4])), throwsA(anything));
    });
  });

  testWidgets('decoding gives the pixel size and RGBA bytes', (tester) async {
    await tester.runAsync(() async {
      final p = await decodePhoto(File('$_dir/blank.jpg').readAsBytesSync());
      expect((p.width, p.height), (1200, 900));
      expect(p.rgba.length, 1200 * 900 * 4);
    });
  });
}
