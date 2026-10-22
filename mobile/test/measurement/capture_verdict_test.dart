import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_messages.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/sticker_gate.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';

GateScan _scan(FinderCode? code, {int stickers = 0}) => GateScan(
      [for (var i = 0; i < stickers; i++) const GateSticker([(0, 0), (10, 0), (10, 10), (0, 10)], 1, 0.9, 0.9)],
      code,
      0.5,
      0.5,
      1,
    );

MarkerCheck _marker({MarkerCode? code}) => MarkerCheck(
      used: code == null,
      reason: code?.code ?? '',
      code: code,
      referenceSource: 'stand-in',
      calibrationMode: 'relative',
    );

void main() {
  group('the decision table (CV draft of the table agreed with Color Matric; his vectors replace it)', () {
    test('every finder code means retake', () {
      for (final c in FinderCode.values) {
        final v = judgeCapture(scan: _scan(c));
        expect((v.verdict, v.code), (Verdict.retake, c.code), reason: c.code);
      }
    });

    test('a readable sticker with good patches and skin is ok', () {
      final v = judgeCapture(scan: _scan(null, stickers: 1), marker: _marker(), skin: const SkinType(fitzpatrick: 'III', skinPixels: 9000));
      expect((v.verdict, v.code), (Verdict.ok, null));
    });

    test('colour problems only warn, because the scale still comes from the frame', () {
      for (final c in [MarkerCode.greyClipped, MarkerCode.greyNotUniform, MarkerCode.greyNotNeutral, MarkerCode.greyGainOutOfRange, MarkerCode.skinPatchOff]) {
        final v = judgeCapture(scan: _scan(null, stickers: 1), marker: _marker(code: c));
        expect((v.verdict, v.code), (Verdict.warn, c.code), reason: c.code);
      }
    });

    test('a sticker that cannot be read means retake', () {
      final v = judgeCapture(scan: _scan(null, stickers: 1), marker: _marker(code: MarkerCode.cropInvalid));
      expect((v.verdict, v.code), (Verdict.retake, 'crop_invalid'));
    });

    test('too few skin pixels only warns', () {
      final v = judgeCapture(scan: _scan(null, stickers: 1), marker: _marker(), skin: const SkinType(skinPixels: 10));
      expect((v.verdict, v.code), (Verdict.warn, 'too_few_skin_pixels'));
    });

    test('the worst result wins, and a retake from the finder is not softened by anything else', () {
      final v = judgeCapture(scan: _scan(FinderCode.mirrored), marker: _marker(code: MarkerCode.greyClipped), skin: const SkinType(skinPixels: 0));
      expect((v.verdict, v.code), (Verdict.retake, 'mirrored'));
      final w = judgeCapture(scan: _scan(null, stickers: 1), marker: _marker(code: MarkerCode.greyClipped), skin: const SkinType(skinPixels: 0));
      expect((w.verdict, w.code), (Verdict.warn, 'grey_clipped'), reason: 'ties go to the first in the list: finder, marker, skin');
    });

    test('a sticker that was found but not checked (no marker result) still counts as ok on the finder side', () {
      final v = judgeCapture(scan: _scan(null, stickers: 1));
      expect(v.verdict, Verdict.ok);
    });
  });

  group('the sentences', () {
    test('there is one for every code, short, and without the technical words', () {
      final codes = [for (final c in FinderCode.values) c.code, for (final c in MarkerCode.values) c.code, for (final c in SkinCode.values) c.code];
      for (final code in codes) {
        final text = captureMessage(code);
        expect(text.split(RegExp(r'\s+')).length, lessThanOrEqualTo(16), reason: code);
        expect(text.toLowerCase(), isNot(matches(RegExp(r'\b(contrast|orientation|homography|frame|layout|gains?|ita|l\*|clipped|neutral)\b'))), reason: code);
        expect(text.endsWith('.'), isTrue, reason: code);
      }
      expect(captureMessage(null), isNotEmpty);
    });

    test('an unknown code gets a safe sentence instead of a crash', () {
      expect(captureMessage('something_new'), contains('Retake'));
    });
  });
}
