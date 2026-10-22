import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/measurement/cv_wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';
import 'package:melanin_wound_cdss/features/measurement/measurement_pipeline.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';
import 'package:melanin_wound_cdss/features/sync/domain/wound_event_validator.dart';

import 'analysis_session_test.dart' show OneWoundSegmenter;

const _dir = 'test/measurement/golden/gate';

class _NoWound implements Segmenter {
  @override
  WoundSegmentation segment(Float32List input, {double conf = 0.25}) =>
      WoundSegmentation(detections: const [], mask: Uint8List(kInputW * kInputH), areaPixels: 0, inferenceMs: 1, postprocessMs: 1);
}

CvWoundAnalyzer _analyzer(Segmenter s) => CvWoundAnalyzer(
      pipeline: () async => MeasurementPipeline(segmenter: s, segmentationVersion: 'fake-model-1', calibrator: const LightCalibrator()),
    );

WoundCapture _photo(String name) => WoundCapture(imageBytes: File('$_dir/$name.jpg').readAsBytesSync());

void main() {
  testWidgets('a photo with a sticker gives valid analytics: area in mm2 from the sticker, colour regions, a skin class', (tester) async {
    await tester.runAsync(() async {
      final stages = <AnalysisStage>[];
      final analytics = await _analyzer(OneWoundSegmenter()).analyze(_photo('sim_one_ok'), onStage: stages.add);
      expect(WoundEventValidator.validateAnalytics(analytics), isEmpty);
      expect(stages, AnalysisStage.values, reason: 'the four stages the processing screen shows, in order');
      // The fake wound is 1000 pixels of the 640 x 480 picture. The sticker is 50 mm and about 80 pixels wide there
      // (3 px/mm in the 1200 px photo, shrunk to 640), so one pixel is about 0.39 mm2.
      expect(analytics['areaMm2'] as num, inInclusiveRange(355, 430));
      expect(analytics['fitzpatrickClass'], isIn(['I', 'II', 'III', 'IV', 'V', 'VI']));
      expect((analytics['pipeline'] as Map)['calibration'], startsWith('light-1'));
      expect((analytics['pipeline'] as Map)['segmentation'], 'fake-model-1');
      expect((analytics['colourRegions'] as List), isNotEmpty);
    });
  });

  testWidgets('a photo without a sticker stops with the sentence the nurse would see on the review screen', (tester) async {
    await tester.runAsync(() async {
      final stages = <AnalysisStage>[];
      await expectLater(
        _analyzer(OneWoundSegmenter()).analyze(_photo('blank'), onStage: stages.add),
        throwsA(isA<AnalysisFailed>()
            .having((e) => e.message, 'message', 'No sticker found. Put the sticker flat beside the wound.')
            .having((e) => e.stage, 'stage', AnalysisStage.calibration)),
      );
      expect(stages, [AnalysisStage.calibration]);
    });
  });

  testWidgets('a glare photo says glare', (tester) async {
    await tester.runAsync(() async {
      await expectLater(
        _analyzer(OneWoundSegmenter()).analyze(_photo('real_glare_a')),
        throwsA(isA<AnalysisFailed>().having((e) => e.message, 'message', startsWith('Glare or shadow on the sticker.'))),
      );
    });
  });

  testWidgets('a photo where no wound is found says so and names the segmentation step', (tester) async {
    await tester.runAsync(() async {
      final stages = <AnalysisStage>[];
      await expectLater(
        _analyzer(_NoWound()).analyze(_photo('sim_one_ok'), onStage: stages.add),
        throwsA(isA<AnalysisFailed>()
            .having((e) => e.message, 'message', startsWith('No wound found'))
            .having((e) => e.stage, 'stage', AnalysisStage.segmentation)),
      );
      expect(stages, [AnalysisStage.calibration, AnalysisStage.segmentation], reason: 'measurement and colour must not start when there is no wound');
    });
  });

  testWidgets('a missing model is reported plainly', (tester) async {
    await tester.runAsync(() async {
      final a = CvWoundAnalyzer(pipeline: () async => throw const ModelMissing('The model file is not on this phone yet (x).'));
      await expectLater(
        a.analyze(_photo('sim_one_ok')),
        throwsA(isA<AnalysisFailed>().having((e) => e.message, 'message', contains('wound model is not on this phone'))),
      );
    });
  });

  testWidgets('a model that is there but cannot be started is not reported as missing', (tester) async {
    await tester.runAsync(() async {
      final a = CvWoundAnalyzer(pipeline: () async => throw const ModelMissing('The model could not be loaded: x'));
      await expectLater(
        a.analyze(_photo('sim_one_ok')),
        throwsA(isA<AnalysisFailed>().having((e) => e.message, 'message', contains('could not be started'))),
      );
    });
  });

  testWidgets('no photo, and bytes that are not a picture, are reported', (tester) async {
    await tester.runAsync(() async {
      final a = _analyzer(OneWoundSegmenter());
      await expectLater(a.analyze(const WoundCapture()), throwsA(isA<AnalysisFailed>().having((e) => e.message, 'm', contains('No photo'))));
      await expectLater(
        a.analyze(WoundCapture(imageBytes: Uint8List.fromList([1, 2, 3]))),
        throwsA(isA<AnalysisFailed>().having((e) => e.message, 'm', contains('could not be opened'))),
      );
    });
  });

  testWidgets('the app wrapper accepts what the analyzer gives', (tester) async {
    await tester.runAsync(() async {
      final analytics = await runWoundAnalysis(_analyzer(OneWoundSegmenter()), _photo('sim_one_ok'));
      expect(analytics['areaMm2'], isA<num>());
    });
  });
}
