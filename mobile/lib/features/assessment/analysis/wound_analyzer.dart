import 'dart:math';
import 'dart:typed_data';

import '../../sync/domain/wound_event_validator.dart';
import '../../sync/ui/sample_assessment.dart';

/// What the capture step hands to the analysis. [imageBytes] stays null until Member 1's camera module is
/// plugged in; the photo itself never leaves the phone (only the numbers in the analytics are synced).
class WoundCapture {
  const WoundCapture({this.imageBytes});

  final Uint8List? imageBytes;
}

/// The four stages the processing screen shows, in the order an analyzer goes through them.
enum AnalysisStage {
  calibration('Skin tone calibration'),
  segmentation('Wound segmentation'),
  measurement('Area measurement'),
  colour('Color distribution analysis');

  const AnalysisStage(this.label);

  /// What the processing screen calls this stage.
  final String label;
}

/// Turns a captured wound photo into the `analytics` part of a wound event.
///
/// Members 1 and 2 implement this with the real calibration and segmentation models. The result must match
/// `analytics` in contracts/wound-event.schema.json; [runWoundAnalysis] checks that before anything is saved.
/// See README.md in this folder.
abstract class WoundAnalyzer {
  /// Analyses [capture] and returns the analytics map. Call [onStage] as each stage starts, so the
  /// processing screen can show progress. Throw [AnalysisFailed] with a message a clinician can act on
  /// (for example "Reference marker not found") if the photo can't be analysed.
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage});
}

/// The analysis couldn't produce a result. [message] is shown to the clinician, and nothing is saved.
class AnalysisFailed implements Exception {
  AnalysisFailed(this.message, {this.stage});

  final String message;

  /// The stage that failed, if known, so the screen can mark that step.
  final AnalysisStage? stage;

  @override
  String toString() => 'AnalysisFailed(${stage?.name ?? '-'}): $message';
}

/// Runs [analyzer] and checks its output against the contract. Returns the analytics, or throws [AnalysisFailed]
/// for anything that went wrong, including a result in the wrong shape, so bad data never reaches the queue.
Future<Map<String, dynamic>> runWoundAnalysis(
  WoundAnalyzer analyzer,
  WoundCapture capture, {
  void Function(AnalysisStage stage)? onStage,
}) async {
  AnalysisStage? current;
  final Map<String, dynamic> analytics;
  try {
    analytics = await analyzer.analyze(
      capture,
      onStage: (stage) {
        current = stage;
        onStage?.call(stage);
      },
    );
  } on AnalysisFailed {
    rethrow;
  } catch (_) {
    throw AnalysisFailed('The analysis stopped unexpectedly. Retake the photo and try again.', stage: current);
  }
  final problems = WoundEventValidator.validateAnalytics(analytics);
  if (problems.isNotEmpty) {
    // A bug in the analyzer, not something the clinician did; say so plainly and keep the details for the log.
    throw AnalysisFailed('The analysis gave a result this app can\'t save (${problems.first}).', stage: current);
  }
  return analytics;
}

/// Stands in for the real pipeline until Members 1–2 plug theirs in. It steps through the stages with a short
/// pause each, then returns made-up but valid analytics ([sampleAnalytics]).
class SampleWoundAnalyzer implements WoundAnalyzer {
  SampleWoundAnalyzer({this.stageDuration = const Duration(milliseconds: 900), Random? random}) : _random = random;

  /// How long each stage pretends to take, so the progress is visible.
  final Duration stageDuration;
  final Random? _random;

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    for (final stage in AnalysisStage.values) {
      onStage?.call(stage);
      await Future<void>.delayed(stageDuration);
    }
    return sampleAnalytics(random: _random);
  }
}
