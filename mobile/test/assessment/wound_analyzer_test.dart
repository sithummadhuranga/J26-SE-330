import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/sync/domain/wound_event_validator.dart';

/// The on-device analysis slot: what an analyzer may hand back before an assessment is saved (§5).
void main() {
  const photo = WoundCapture();

  test('the sample analyzer always gives analytics the contract accepts', () async {
    final analyzer = SampleWoundAnalyzer(stageDuration: Duration.zero, random: Random(7));
    for (var i = 0; i < 200; i++) {
      final analytics = await analyzer.analyze(photo);
      expect(WoundEventValidator.validateAnalytics(analytics), isEmpty, reason: 'run $i: $analytics');
    }
  });

  test('the sample analyzer reports every stage, in order, so the screen can tick them off', () async {
    final seen = <AnalysisStage>[];
    await SampleWoundAnalyzer(stageDuration: Duration.zero).analyze(photo, onStage: seen.add);
    expect(seen, AnalysisStage.values);
  });

  test('a valid result comes back unchanged', () async {
    expect(await runWoundAnalysis(_Analyzer.returning(_analytics()), photo), _analytics());
  });

  test('a result with a field the contract does not have is refused before it is saved (§5)', () async {
    final extra = _analytics()..['confidence'] = 0.9;
    await expectLater(
      runWoundAnalysis(_Analyzer.returning(extra), photo),
      throwsA(
        isA<AnalysisFailed>()
            .having((e) => e.message, 'message', contains('analytics.confidence is not allowed'))
            .having((e) => e.stage, 'stage', AnalysisStage.colour),
      ),
    );
  });

  test('out-of-range and missing values are refused too: percent over 100, no pipeline, class VII', () async {
    final results = [
      _analytics()
        ..['colourRegions'] = [
          {'cluster': 1, 'percent': 120},
        ],
      _analytics()..remove('pipeline'),
      _analytics()..['fitzpatrickClass'] = 'VII',
    ];
    for (final result in results) {
      await expectLater(
        runWoundAnalysis(_Analyzer.returning(result), photo),
        throwsA(isA<AnalysisFailed>()),
        reason: '$result',
      );
    }
  });

  test('an analyzer\'s own AnalysisFailed reaches the screen as it is, with its stage', () async {
    final failure = AnalysisFailed('Reference marker not found', stage: AnalysisStage.calibration);
    await expectLater(runWoundAnalysis(_Analyzer.throwing(failure), photo), throwsA(same(failure)));
  });

  test('any other crash becomes a plain message on the stage that was running, without the internals', () async {
    final crash = _Analyzer.throwing(StateError('model file missing'), stages: AnalysisStage.values.take(2));
    await expectLater(
      runWoundAnalysis(crash, photo),
      throwsA(
        isA<AnalysisFailed>()
            .having((e) => e.stage, 'stage', AnalysisStage.segmentation)
            .having((e) => e.message, 'message', isNot(contains('model file'))),
      ),
    );
  });
}

/// Valid analytics, shaped like the contract's own example.
Map<String, dynamic> _analytics() => {
  'areaMm2': 412.5,
  'colourRegions': [
    {'cluster': 1, 'percent': 60.0},
    {'cluster': 2, 'percent': 40.0},
  ],
  'fitzpatrickClass': 'V',
  'pipeline': {'calibration': '2.1.0', 'segmentation': 'yolo11n-seg-0.4'},
};

/// An analyzer that goes through [stages], then returns a set result or throws.
class _Analyzer implements WoundAnalyzer {
  _Analyzer.returning(Map<String, dynamic> this._result) : _error = null, _stages = AnalysisStage.values;

  _Analyzer.throwing(Object this._error, {Iterable<AnalysisStage> stages = AnalysisStage.values})
    : _result = null,
      _stages = stages;

  final Map<String, dynamic>? _result;
  final Object? _error;
  final Iterable<AnalysisStage> _stages;

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    _stages.forEach(onStage ?? (_) {});
    if (_error case final error?) throw error;
    return _result!;
  }
}
