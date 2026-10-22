import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_check.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/photo_check_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/sticker_gate.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';

import 'analysis_session_test.dart' show tinyPng;

const _sticker = GateSticker([(1, 0.2), (3.5, 0.4), (3.3, 1.8), (0.8, 1.6)], 1, 0.9, 0.9);

CaptureCheck _check(Verdict v, String? code, {bool sticker = true}) {
  final finder = [for (final c in FinderCode.values) if (c.code == code) c].firstOrNull;
  final marker = [for (final c in MarkerCode.values) if (c.code == code) c].firstOrNull;
  return CaptureCheck(
    GateScan(sticker ? const [_sticker] : const [], finder, 0.5, 0.5, 1),
    sticker ? MarkerCheck(used: marker == null, code: marker, referenceSource: 'stand-in', calibrationMode: 'relative') : null,
    const SkinType(fitzpatrick: 'III', skinPixels: 5000),
    CaptureVerdict(v, code),
  );
}

void main() {
  final png = base64Decode(tinyPng);
  var measured = 0, retaken = 0;

  Future<void> show(WidgetTester tester, CaptureCheck c, {bool anyway = false, double textScale = 1}) async {
    measured = 0;
    retaken = 0;
    tester.view.physicalSize = const Size(800, 2400); // tall, so a list shows everything and nothing is built lazily off screen
    tester.view.devicePixelRatio = 2;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(
      theme: woundTheme(),
      builder: (context, child) => MediaQuery(data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(textScale)), child: child!),
      home: PhotoCheckScreen(
        photoBytes: png,
        photoWidth: 4,
        photoHeight: 2,
        check: c,
        onMeasure: () => measured++,
        onRetake: () => retaken++,
        allowMeasureWithoutSticker: anyway,
      ),
    ));
    await tester.pump();
  }

  testWidgets('ok: the photo is ready, the main button measures, the other chooses another photo', (tester) async {
    await show(tester, _check(Verdict.ok, null));
    expect(find.text('Ready'), findsOneWidget);
    expect(find.text('Sticker found.'), findsOneWidget);
    expect(find.text('The photo looks usable.'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Measure this photo'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Choose another photo'), findsOneWidget);
    expect(find.byKey(const Key('stickerOutline')), findsOneWidget);
    await tester.tap(find.text('Measure this photo'));
    expect(measured, 1);
    await tester.tap(find.text('Choose another photo'));
    expect(retaken, 1);
  });

  testWidgets('warn: what happened is bold, what to do follows, Retake is the main button and measuring anyway is quieter', (tester) async {
    await show(tester, _check(Verdict.warn, 'grey_clipped'));
    expect(find.text('Check'), findsOneWidget);
    final first = tester.widget<Text>(find.text('Too bright or too dark.'));
    expect(first.style?.fontWeight, FontWeight.w700);
    expect(find.text('Colour may be off.'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Retake'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Measure anyway'), findsOneWidget);
    await tester.tap(find.text('Measure anyway'));
    expect(measured, 1);
    await tester.tap(find.widgetWithText(FilledButton, 'Retake'));
    expect(retaken, 1);
  });

  testWidgets('retake: only Retake is offered, and no way to measure without a sticker unless the demo allows it', (tester) async {
    await show(tester, _check(Verdict.retake, 'mirrored'));
    expect(find.text('Retake'), findsNWidgets(2)); // the badge and the button
    expect(find.text('The sticker looks mirrored.'), findsOneWidget);
    expect(find.text('Use the rear camera.'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Retake'), findsOneWidget);
    expect(find.text('Measure anyway'), findsNothing);
    expect(find.text('Measure without a sticker'), findsNothing);
    await tester.tap(find.widgetWithText(FilledButton, 'Retake'));
    expect(retaken, 1);
  });

  testWidgets('retake in the demo may still measure, and says the area will not be in mm', (tester) async {
    await show(tester, _check(Verdict.retake, 'no_candidate', sticker: false), anyway: true);
    expect(find.byKey(const Key('stickerOutline')), findsNothing, reason: 'nothing was found to outline');
    expect(find.text('No sticker found.'), findsOneWidget);
    expect(find.textContaining('no area in mm'), findsOneWidget);
    await tester.tap(find.text('Measure without a sticker'));
    expect(measured, 1);
  });

  testWidgets('the verdict is announced to a screen reader as one sentence', (tester) async {
    final handle = tester.ensureSemantics();
    await show(tester, _check(Verdict.retake, 'weak_frame_contrast'));
    expect(find.bySemanticsLabel(RegExp(r'^Retake\. Glare or shadow on the sticker\. Tilt the phone away from the light\.$')), findsOneWidget);
    handle.dispose();
  });

  testWidgets('buttons are at least 48 logical pixels tall and nothing overflows with large text', (tester) async {
    await show(tester, _check(Verdict.warn, 'grey_not_uniform'), textScale: 1.6);
    for (final label in ['Retake', 'Measure anyway']) {
      final box = tester.getSize(find.widgetWithText(label == 'Retake' ? FilledButton : OutlinedButton, label));
      expect(box.height, greaterThanOrEqualTo(48), reason: label);
    }
    expect(tester.takeException(), isNull);
  });

  testWidgets('how long the check took is shown, so the offline claim is visible', (tester) async {
    await show(tester, _check(Verdict.ok, null));
    expect(find.textContaining('on this phone'), findsOneWidget);
  });
}
