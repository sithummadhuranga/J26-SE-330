import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/assessment/local_analysis.dart';
import 'package:melanin_wound_cdss/features/assessment/local_result_screen.dart';
import 'package:melanin_wound_cdss/features/assessment/media_store.dart';
import 'package:melanin_wound_cdss/features/measurement/colour_regions.dart';
import 'package:melanin_wound_cdss/features/measurement/cv_wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';
import 'package:melanin_wound_cdss/features/measurement/measurement_pipeline.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

import 'analysis_session_test.dart' show OneWoundSegmenter, tinyPng;
import 'dart:convert';

const _dir = 'test/measurement/golden/gate';

class _NoWound implements Segmenter {
  @override
  WoundSegmentation segment(Float32List input, {double conf = 0.25}) =>
      WoundSegmentation(detections: const [], mask: Uint8List(kInputW * kInputH), areaPixels: 0, inferenceMs: 1, postprocessMs: 1);
}

CvWoundAnalyzer _analyzer(Segmenter s) => CvWoundAnalyzer(
      pipeline: () async => MeasurementPipeline(segmenter: s, segmentationVersion: 'fake-model-1', calibrator: const LightCalibrator()),
    );

void main() {
  group('a photo without a sticker', () {
    testWidgets('still gets the wound outline, the colour regions and a skin type, and no area at all', (tester) async {
      await tester.runAsync(() async {
        final bytes = File('$_dir/blank.jpg').readAsBytesSync(); // no sticker anywhere in it
        final a = await _analyzer(OneWoundSegmenter()).analyzeWithoutScale(bytes);
        expect(a.outline.mask.where((v) => v != 0).length, 1000);
        expect((a.outline.originalWidth, a.outline.originalHeight), (1200, 900));
        expect(a.colourRegions, isNotEmpty);
        expect(a.colourRegions.fold<double>(0, (s, r) => s + r.percent), closeTo(100, 0.5));
        expect(a.notes.join(' '), isNot(contains('mm')));
      });
    });

    testWidgets('says so when no wound is found, and when the model is missing or the photo cannot be opened', (tester) async {
      await tester.runAsync(() async {
        final bytes = File('$_dir/blank.jpg').readAsBytesSync();
        await expectLater(_analyzer(_NoWound()).analyzeWithoutScale(bytes),
            throwsA(isA<AnalysisFailed>().having((e) => e.message, 'm', startsWith('No wound found'))));
        await expectLater(CvWoundAnalyzer(pipeline: () async => throw const ModelMissing('The model file is not on this phone yet (x).')).analyzeWithoutScale(bytes),
            throwsA(isA<AnalysisFailed>().having((e) => e.message, 'm', contains('not on this phone'))));
        await expectLater(_analyzer(OneWoundSegmenter()).analyzeWithoutScale(Uint8List.fromList([1, 2, 3])),
            throwsA(isA<AnalysisFailed>().having((e) => e.message, 'm', contains('could not be opened'))));
      });
    });

    test('the analyzer is also a LocalAnalyzer, so a screen can ask for it', () {
      expect(_analyzer(OneWoundSegmenter()), isA<LocalAnalyzer>());
    });
  });

  group('the result screen for a photo without a sticker', () {
    final png = Uint8List.fromList(base64Decode(tinyPng));
    final analysis = LocalAnalysis(
      outline: WoundOutline(mask: Uint8List(640 * 480)..[1000] = 1, scale: 0.5, padLeft: 0, padTop: 0, originalWidth: 1280, originalHeight: 960),
      colourRegions: const [ColourRegion(1, 60, [150, 60, 60]), ColourRegion(2, 30, [200, 170, 100]), ColourRegion(3, 10, [120, 120, 110])],
      fitzpatrickClass: 'III',
      notes: const [],
    );

    Future<void> show(WidgetTester tester, {VoidCallback? onDone, LocalAnalysis? a}) async {
      tester.view
        ..physicalSize = const Size(1080, 5200)
        ..devicePixelRatio = 2.6;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
        theme: woundTheme(),
        home: LocalResultScreen(photoBytes: png, analysis: a ?? analysis, onDone: onDone ?? () {}),
      ));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
    }

    testWidgets('the area says it was not measured and why, the rest is shown', (tester) async {
      await show(tester);
      expect(find.text('Wound area'), findsOneWidget);
      expect(find.text('Not measured'), findsOneWidget);
      expect(find.textContaining('needs the sticker'), findsOneWidget);
      expect(find.text('Color distribution'), findsOneWidget);
      expect(find.text('60.0%'), findsOneWidget);
      expect(find.textContaining('Skin tone'), findsOneWidget);
      expect(find.textContaining('III'), findsOneWidget);
      expect(find.byKey(const Key('woundOutline')), findsOneWidget);
    });

    testWidgets('it says the colours are not corrected and that nothing is saved or synced', (tester) async {
      await show(tester);
      expect(find.textContaining('not colour-corrected'), findsOneWidget);
      expect(find.textContaining('not saved'), findsOneWidget);
    });

    testWidgets('an unknown skin type is said plainly, and Done goes back', (tester) async {
      var done = 0;
      await show(tester, onDone: () => done++, a: LocalAnalysis(outline: analysis.outline, colourRegions: analysis.colourRegions, fitzpatrickClass: null, notes: const []));
      expect(find.textContaining('not estimated'), findsOneWidget);
      await tester.tap(find.widgetWithText(FilledButton, 'Done'));
      expect(done, 1);
    });
  });
}
