import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/sync/domain/wound_event_validator.dart';

/// An analyzer that returns whatever the test gives it, or throws.
class _ScriptedAnalyzer implements WoundAnalyzer {
  _ScriptedAnalyzer({this.result, this.error, this.stages = AnalysisStage.values});

  final Map<String, dynamic>? result;
  final Object? error;
  final List<AnalysisStage> stages;

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    for (final stage in stages) {
      onStage?.call(stage);
    }
    if (error != null) throw error!;
    return result!;
  }
}

Map<String, dynamic> _valid() => {
  'areaMm2': 412.5,
  'colourRegions': [
    {'cluster': 1, 'percent': 60.0},
    {'cluster': 2, 'percent': 40.0},
  ],
  'fitzpatrickClass': 'V',
  'pipeline': {'calibration': '2.1.0', 'segmentation': 'yolo11n-seg-0.4'},
};

void main() {
  group('SampleWoundAnalyzer', () {
    test('always gives analytics that match the contract', () async {
      final analyzer = SampleWoundAnalyzer(stageDuration: Duration.zero, random: Random(7));
      for (var i = 0; i < 200; i++) {
        final analytics = await analyzer.analyze(const WoundCapture());
        expect(WoundEventValidator.validateAnalytics(analytics), isEmpty, reason: 'run $i: $analytics');
      }
    });

    test('goes through every stage in order', () async {
      final seen = <AnalysisStage>[];
      await SampleWoundAnalyzer(stageDuration: Duration.zero).analyze(const WoundCapture(), onStage: seen.add);
      expect(seen, AnalysisStage.values);
    });
  });

  group('runWoundAnalysis', () {
    test('returns valid analytics untouched', () async {
      final analytics = await runWoundAnalysis(_ScriptedAnalyzer(result: _valid()), const WoundCapture());
      expect(analytics, _valid());
    });

    test('refuses a result with a field the contract does not have', () async {
      final bad = _valid()..['confidence'] = 0.9;
      await expectLater(
        runWoundAnalysis(_ScriptedAnalyzer(result: bad), const WoundCapture()),
        throwsA(
          isA<AnalysisFailed>()
              .having((e) => e.message, 'message', contains('analytics.confidence is not allowed'))
              .having((e) => e.stage, 'stage', AnalysisStage.colour),
        ),
      );
    });

    test('refuses out-of-range and missing values', () async {
      final tooBig = _valid()
        ..['colourRegions'] = [
          {'cluster': 1, 'percent': 120},
        ];
      final noPipeline = _valid()..remove('pipeline');
      final badClass = _valid()..['fitzpatrickClass'] = 'VII';
      for (final bad in [tooBig, noPipeline, badClass]) {
        await expectLater(
          runWoundAnalysis(_ScriptedAnalyzer(result: bad), const WoundCapture()),
          throwsA(isA<AnalysisFailed>()),
          reason: '$bad',
        );
      }
    });

    test('passes on an AnalysisFailed from the analyzer as it is', () async {
      final failure = AnalysisFailed('Reference marker not found', stage: AnalysisStage.calibration);
      await expectLater(
        runWoundAnalysis(_ScriptedAnalyzer(error: failure), const WoundCapture()),
        throwsA(same(failure)),
      );
    });

    test('turns any other crash into a plain message on the stage that was running', () async {
      await expectLater(
        runWoundAnalysis(
          _ScriptedAnalyzer(error: StateError('model file missing'), stages: AnalysisStage.values.take(2).toList()),
          const WoundCapture(),
        ),
        throwsA(
          isA<AnalysisFailed>()
              .having((e) => e.stage, 'stage', AnalysisStage.segmentation)
              .having((e) => e.message, 'message', isNot(contains('model file'))),
        ),
      );
    });
  });
}
