import 'dart:typed_data';

import 'colour_regions.dart';
import 'image_prepare.dart';
import 'wound_area.dart';
import 'wound_segmenter.dart';
import 'yolo_seg_postprocess.dart';

/// What the colour calibration hands back: the calibrated image (RGBA, the same size it was given), the skin type it
/// estimated if any, and the version of the calibration code, which goes into the wound event. When a sticker picture was
/// used it also says so, in plain words ([markerLabel], for example 'relative calibration, stand-in reference colours').
class CalibrationResult {
  const CalibrationResult(
    this.rgba, {
    this.fitzpatrickClass,
    required this.version,
    this.markerUsed = false,
    this.markerLabel,
    this.itaAngle,
    this.notes = const [],
  });

  final Uint8List rgba;
  final String? fitzpatrickClass;
  final String version;
  final bool markerUsed;
  final String? markerLabel;

  /// The skin angle the class was read from, in degrees, if one was computed.
  final double? itaAngle;

  /// What a person should know about, for example that the sticker was rejected.
  final List<String> notes;
}

/// The colour calibration step. Its code belongs to someone else and may change, so the pipeline only relies on this.
/// [markerRgb] is the straightened picture of the reference sticker (square, interleaved R, G, B, cut from the full-size
/// photo), or null when no sticker was found.
abstract class Calibrator {
  CalibrationResult calibrate(Uint8List rgba, int width, int height, {Uint8List? markerRgb});
}

/// Whether the calibration gets the original photo or the 640x480 copy made for the model.
enum CalibrationInput { original, canvas }

/// The measurement is missing something the wound event needs (no wound, no marker scale, no skin type).
class MeasurementIncomplete implements Exception {
  const MeasurementIncomplete(this.reason);

  final String reason;

  @override
  String toString() => 'MeasurementIncomplete: $reason';
}

class MeasurementResult {
  const MeasurementResult({
    required this.image,
    required this.segmentation,
    required this.colourRegions,
    required this.areaMm2,
    required this.fitzpatrickClass,
    required this.calibrationVersion,
    required this.segmentationVersion,
    required this.notes,
    required this.timingsMs,
  });

  /// The 640x480 picture that was segmented (calibrated if calibration worked), with the scale back to the photo.
  final PreparedImage image;
  final WoundSegmentation segmentation;
  final List<ColourRegion> colourRegions;

  /// Null when no marker scale was given.
  final double? areaMm2;

  /// Null when calibration was skipped, failed, or did not estimate a skin type.
  final String? fitzpatrickClass;

  /// 'none' when the original photo was used.
  final String calibrationVersion;
  final String segmentationVersion;

  /// What happened that a person should know about, for example that calibration failed and was skipped.
  final List<String> notes;
  final Map<String, double> timingsMs;

  /// The `analytics` part of the wound event (contracts/wound-event.schema.json).
  /// Throws [MeasurementIncomplete] when a required value is missing, so the app can ask for another photo.
  Map<String, dynamic> toAnalytics() {
    if (segmentation.detections.isEmpty) throw const MeasurementIncomplete('no wound was found in the photo');
    final area = areaMm2;
    if (area == null) {
      throw const MeasurementIncomplete('the reference marker was not found, so the area cannot be given in mm²');
    }
    final skin = fitzpatrickClass;
    if (skin == null) throw const MeasurementIncomplete('the skin type was not estimated');
    return {
      'areaMm2': (area * 10).round() / 10,
      'colourRegions': [
        for (final r in colourRegions) {'cluster': r.cluster, 'percent': r.percent},
      ],
      'fitzpatrickClass': skin,
      'pipeline': {'calibration': calibrationVersion, 'segmentation': segmentationVersion},
    };
  }
}

/// One call from a photo to the numbers of a wound assessment: colour calibration (if there is one), fitting the photo to
/// the model, segmentation, colour regions, and the area when a marker scale is available.
class MeasurementPipeline {
  MeasurementPipeline({
    required this.segmenter,
    required this.segmentationVersion,
    this.calibrator,
    this.calibrateOn = CalibrationInput.canvas,
    this.confidence = 0.25,
  });

  final Segmenter segmenter;

  /// Identifies the model file, for example 'yolo11n-seg-dfuc2022-fp32-1'.
  final String segmentationVersion;
  final Calibrator? calibrator;
  final CalibrationInput calibrateOn;
  final double confidence;

  /// [rgba] is the photo (RGBA, [width] x [height]). [scale] turns pixels of the 640x480 picture into mm² and comes from
  /// the reference marker; without it the result has no area. A calibration that throws, or returns the wrong size, is
  /// skipped and the original photo is used, so a problem there never stops the measurement.
  MeasurementResult analyze(Uint8List rgba, int width, int height, {PlaneScale? scale, Uint8List? markerRgb}) {
    final notes = <String>[];
    final timings = <String, double>{};
    final clock = Stopwatch()..start();
    CalibrationResult? calibration;
    PreparedImage image;
    if (calibrator != null && calibrateOn == CalibrationInput.original) {
      calibration = _calibrate(rgba, width, height, notes, markerRgb);
      timings['calibration'] = _lap(clock);
      image = prepareImage(calibration?.rgba ?? rgba, width, height);
      timings['prepare'] = _lap(clock);
      return _finish(image, calibration, scale, notes, timings, clock);
    }
    image = prepareImage(rgba, width, height);
    timings['prepare'] = _lap(clock);
    return _continueFrom(image, scale, notes, timings, clock, markerRgb);
  }

  /// Like [analyze] for a photo that was already fitted to the 640x480 input (for example in a background isolate,
  /// because shrinking a 12-megapixel photo takes a moment). Calibration, if there is one, runs on this canvas.
  MeasurementResult analyzePrepared(PreparedImage prepared, {PlaneScale? scale, double? prepareMs, Uint8List? markerRgb}) {
    final timings = <String, double>{'prepare': ?prepareMs};
    return _continueFrom(prepared, scale, <String>[], timings, Stopwatch()..start(), markerRgb);
  }

  MeasurementResult _continueFrom(
    PreparedImage prepared,
    PlaneScale? scale,
    List<String> notes,
    Map<String, double> timings,
    Stopwatch clock,
    Uint8List? markerRgb,
  ) {
    var image = prepared;
    CalibrationResult? calibration;
    if (calibrator != null) {
      calibration = _calibrate(prepared.rgba, kInputW, kInputH, notes, markerRgb);
      timings['calibration'] = _lap(clock);
      if (calibration != null) {
        image = PreparedImage(
          rgba: calibration.rgba,
          scale: prepared.scale,
          padLeft: prepared.padLeft,
          padTop: prepared.padTop,
          originalWidth: prepared.originalWidth,
          originalHeight: prepared.originalHeight,
        );
      }
    }
    return _finish(image, calibration, scale, notes, timings, clock);
  }

  MeasurementResult _finish(
    PreparedImage image,
    CalibrationResult? calibration,
    PlaneScale? scale,
    List<String> notes,
    Map<String, double> timings,
    Stopwatch clock,
  ) {
    final segmentation = segmenter.segment(rgbaToModelInput(image.rgba, kInputW, kInputH), conf: confidence);
    timings['segmentation'] = _lap(clock);
    final regions = colourRegions(image.rgba, segmentation.mask);
    timings['colour'] = _lap(clock);
    final area = scale?.areaMm2(segmentation.mask);
    timings['area'] = _lap(clock);
    return MeasurementResult(
      image: image,
      segmentation: segmentation,
      colourRegions: regions,
      areaMm2: area,
      fitzpatrickClass: calibration?.fitzpatrickClass,
      calibrationVersion: calibration?.version ?? 'none',
      segmentationVersion: segmentationVersion,
      notes: notes,
      timingsMs: timings,
    );
  }

  /// Milliseconds since the last lap; the clock keeps running.
  static double _lap(Stopwatch clock) {
    final ms = clock.elapsedMicroseconds / 1000;
    clock
      ..reset()
      ..start();
    return ms;
  }

  CalibrationResult? _calibrate(Uint8List rgba, int width, int height, List<String> notes, Uint8List? markerRgb) {
    try {
      final result = calibrator!.calibrate(rgba, width, height, markerRgb: markerRgb);
      if (result.rgba.length != rgba.length) {
        notes.add('calibration returned an image of the wrong size, so the original photo was used');
        return null;
      }
      notes.addAll(result.notes);
      return result;
    } catch (e) {
      notes.add('calibration failed ($e), so the original photo was used');
      return null;
    }
  }
}
