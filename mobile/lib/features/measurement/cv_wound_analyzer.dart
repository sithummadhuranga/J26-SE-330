import 'dart:isolate';

import '../assessment/analysis/wound_analyzer.dart';
import 'capture/capture_messages.dart';
import 'capture/capture_runner.dart';
import 'capture/capture_verdict.dart';
import 'capture/decoded_photo.dart';
import 'demo/analysis_session.dart' show DemoEngine;
import 'image_prepare.dart';
import 'light_calibrator.dart';
import 'measurement_pipeline.dart';
import 'wound_area.dart';

/// The wound model is not on this phone (or could not be loaded).
class ModelMissing implements Exception {
  const ModelMissing(this.reason);

  final String reason;
}

typedef PipelineFactory = Future<MeasurementPipeline> Function();

/// The app's real wound analysis: the CV and colour components behind the [WoundAnalyzer] slot. It looks for the sticker, reads
/// the light from its patches, fits the photo to the model, finds the wound, measures its area with the sticker as the scale, and
/// returns the numbers the wound event carries. Everything runs on the phone; nothing leaves it.
///
/// The photo is checked again here (the review screen already did) so this class is correct on its own: no sticker, no analysis.
class CvWoundAnalyzer implements WoundAnalyzer {
  /// [pipeline] builds the measurement once the model is available; the real model file on the phone when null.
  CvWoundAnalyzer({PipelineFactory? pipeline, DemoEngine? engine, double confidence = 0.25})
      : _pipeline = pipeline ?? _onDevice(engine ?? DemoEngine(), confidence);

  final PipelineFactory _pipeline;

  static PipelineFactory _onDevice(DemoEngine engine, double confidence) {
    MeasurementPipeline? cached;
    return () async {
      if (cached != null) return cached!;
      if (!engine.ready) await engine.load();
      if (!engine.ready) throw ModelMissing(engine.loadError ?? 'The model could not be loaded.');
      return cached = engine.pipeline(confidence, calibrator: const LightCalibrator());
    };
  }

  /// Gives the screen a moment to paint the stage that just started.
  static Future<void> _paint() => Future<void>.delayed(const Duration(milliseconds: 30));

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    final bytes = capture.imageBytes;
    if (bytes == null) throw AnalysisFailed('No photo to analyse. Retake the photo.');

    onStage?.call(AnalysisStage.calibration);
    await _paint();
    final DecodedPhoto photo;
    try {
      photo = await decodePhoto(bytes);
    } catch (_) {
      throw AnalysisFailed('This photo could not be opened. Retake it.', stage: AnalysisStage.calibration);
    }
    final check = await checkInBackground(photo);
    final crop = check.stickerCrop;
    if (!check.scan.found || crop == null || check.verdict.verdict == Verdict.retake) {
      throw AnalysisFailed(captureMessage(check.verdict.code), stage: AnalysisStage.calibration);
    }
    final MeasurementPipeline pipeline;
    try {
      pipeline = await _pipeline();
    } on ModelMissing {
      throw AnalysisFailed('The wound model is not on this phone yet. Ask for it to be installed, then try again.', stage: AnalysisStage.segmentation);
    }

    final w = photo.width, h = photo.height;
    final transfer = TransferableTypedData.fromList([photo.rgba]);
    final prepared = await Isolate.run(() => prepareImage(transfer.materialize().asUint8List(), w, h));
    // The sticker's corners in the 640 x 480 picture the model sees; its side is 50 mm, which gives the area.
    final scale = HomographyScale.fromSquare([
      for (final c in check.scan.stickers.first.corners) ((c.$1 + 0.5) * prepared.scale + prepared.padLeft, (c.$2 + 0.5) * prepared.scale + prepared.padTop),
    ], 50.0);

    onStage?.call(AnalysisStage.segmentation);
    await _paint();
    final result = pipeline.analyzePrepared(prepared, scale: scale, markerRgb: crop);
    onStage?.call(AnalysisStage.measurement);
    await _paint();
    onStage?.call(AnalysisStage.colour);
    await _paint();
    try {
      return result.toAnalytics();
    } on MeasurementIncomplete catch (e) {
      throw _failure(e.reason);
    }
  }

  AnalysisFailed _failure(String reason) {
    if (reason.contains('no wound')) {
      return AnalysisFailed('No wound found in the photo. Retake it with the wound in the middle of the frame.', stage: AnalysisStage.segmentation);
    }
    if (reason.contains('skin type')) {
      return AnalysisFailed('Skin tone could not be read. Include more skin in the photo.', stage: AnalysisStage.calibration);
    }
    return AnalysisFailed('The sticker could not be used to measure the area. Retake the photo.', stage: AnalysisStage.measurement);
  }
}
