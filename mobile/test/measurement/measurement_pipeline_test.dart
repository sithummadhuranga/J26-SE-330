import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/measurement_pipeline.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_area.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';
import 'package:melanin_wound_cdss/features/sync/domain/wound_event_validator.dart';
import 'package:melanin_wound_cdss/features/sync/ui/sample_assessment.dart';

/// A segmenter that always finds the same wound, so the pipeline can be tested without a model.
class FakeSegmenter implements Segmenter {
  FakeSegmenter({required this.mask, required this.found});

  final Uint8List mask;
  final bool found;
  Float32List? lastInput;

  @override
  WoundSegmentation segment(Float32List input, {double conf = 0.25}) {
    lastInput = input;
    return WoundSegmentation(
      detections: found ? [SegDetection(200, 100, 250, 120, 0.9, Float32List(kMaskDim))] : const [],
      mask: mask,
      areaPixels: mask.fold<int>(0, (a, b) => a + b),
      inferenceMs: 1,
      postprocessMs: 0.1,
    );
  }
}

class FakeCalibrator implements Calibrator {
  FakeCalibrator({this.fail = false, this.wrongSize = false, this.skin = 'V', this.version = 'cal-1'});

  final bool fail, wrongSize;
  final String? skin;
  final String version;
  int? seenWidth, seenHeight;
  Uint8List? seenMarker;

  @override
  CalibrationResult calibrate(Uint8List rgba, int width, int height, {Uint8List? markerRgb}) {
    seenWidth = width;
    seenHeight = height;
    seenMarker = markerRgb;
    if (fail) throw StateError('boom');
    if (wrongSize) return CalibrationResult(Uint8List(10), version: version);
    final out = Uint8List.fromList(rgba);
    for (var p = 0; p < width * height; p++) {
      out[4 * p] = 255 - rgba[4 * p]; // invert red, so a calibrated image is easy to recognise
    }
    return CalibrationResult(out, fitzpatrickClass: skin, version: version, notes: markerRgb == null ? const [] : ['sticker used']);
  }
}

Uint8List solidPhoto(int w, int h, List<int> rgb) {
  final px = Uint8List(w * h * 4);
  for (var p = 0; p < w * h; p++) {
    px.setRange(4 * p, 4 * p + 4, [...rgb, 255]);
  }
  return px;
}

/// A 20 x 50 pixel wound (1000 pixels) on the 640x480 canvas.
Uint8List blob() {
  final m = Uint8List(kInputW * kInputH);
  for (var y = 100; y < 120; y++) {
    for (var x = 200; x < 250; x++) {
      m[y * kInputW + x] = 1;
    }
  }
  return m;
}

void main() {
  MeasurementPipeline pipeline(Segmenter s, {Calibrator? c, CalibrationInput on = CalibrationInput.canvas}) =>
      MeasurementPipeline(segmenter: s, segmentationVersion: 'seg-test-1', calibrator: c, calibrateOn: on);

  final photo = solidPhoto(1280, 960, [180, 90, 60]);

  test('the sticker picture goes to the calibration and its notes reach the result', () {
    final cal = FakeCalibrator();
    final sticker = Uint8List(500 * 500 * 3);
    final withSticker = pipeline(FakeSegmenter(mask: blob(), found: true), c: cal).analyze(photo, 1280, 960, markerRgb: sticker);
    expect(cal.seenMarker, same(sticker));
    expect(withSticker.notes, ['sticker used']);
    final without = pipeline(FakeSegmenter(mask: blob(), found: true), c: cal).analyze(photo, 1280, 960);
    expect(cal.seenMarker, isNull);
    expect(without.notes, isEmpty);
  });

  test('a complete measurement becomes a valid wound event analytics', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true), c: FakeCalibrator())
        .analyze(photo, 1280, 960, scale: const UniformScale(0.5));
    expect(result.areaMm2, 250.0);
    expect(result.fitzpatrickClass, 'V');
    expect(result.calibrationVersion, 'cal-1');
    expect(result.colourRegions.length, 1);
    expect(result.colourRegions[0].centre, [75.0, 90.0, 60.0]); // the calibrated colour, not the original
    expect(result.notes, isEmpty);

    final analytics = result.toAnalytics();
    expect(analytics, {
      'areaMm2': 250.0,
      'colourRegions': [
        {'cluster': 1, 'percent': 100.0},
      ],
      'fitzpatrickClass': 'V',
      'pipeline': {'calibration': 'cal-1', 'segmentation': 'seg-test-1'},
    });
    // The team's own validator accepts it inside a full event.
    final event = sampleAssessment(deviceId: 'device-1', facilityId: 'facility-1');
    event['analytics'] = analytics;
    expect(WoundEventValidator.validate(event), isEmpty);
  });

  test('the segmenter gets the calibrated picture', () {
    final seg = FakeSegmenter(mask: blob(), found: true);
    pipeline(seg, c: FakeCalibrator()).analyze(photo, 1280, 960);
    expect(seg.lastInput![0], closeTo(75 / 255, 1e-6)); // red plane, first pixel
  });

  test('calibration can work on the original photo or on the 640x480 copy', () {
    final onCanvas = FakeCalibrator(), onOriginal = FakeCalibrator();
    pipeline(FakeSegmenter(mask: blob(), found: true), c: onCanvas).analyze(photo, 1280, 960);
    pipeline(FakeSegmenter(mask: blob(), found: true), c: onOriginal, on: CalibrationInput.original).analyze(photo, 1280, 960);
    expect([onCanvas.seenWidth, onCanvas.seenHeight], [640, 480]);
    expect([onOriginal.seenWidth, onOriginal.seenHeight], [1280, 960]);
  });

  test('without calibration the original photo is used and the skin type is missing', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true)).analyze(photo, 1280, 960, scale: const UniformScale(0.5));
    expect(result.calibrationVersion, 'none');
    expect(result.colourRegions[0].centre, [180.0, 90.0, 60.0]);
    expect(() => result.toAnalytics(), throwsA(isA<MeasurementIncomplete>().having((e) => e.reason, 'reason', contains('skin type'))));
  });

  test('a calibration that throws is skipped and the measurement still works', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true), c: FakeCalibrator(fail: true))
        .analyze(photo, 1280, 960, scale: const UniformScale(0.5));
    expect(result.calibrationVersion, 'none');
    expect(result.areaMm2, 250.0);
    expect(result.colourRegions[0].centre, [180.0, 90.0, 60.0]);
    expect(result.notes.single, contains('calibration failed'));
  });

  test('a calibration that returns the wrong size is skipped', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true), c: FakeCalibrator(wrongSize: true)).analyze(photo, 1280, 960);
    expect(result.calibrationVersion, 'none');
    expect(result.notes.single, contains('wrong size'));
  });

  test('no marker scale means no area, and no analytics', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true), c: FakeCalibrator()).analyze(photo, 1280, 960);
    expect(result.areaMm2, isNull);
    expect(() => result.toAnalytics(), throwsA(isA<MeasurementIncomplete>().having((e) => e.reason, 'reason', contains('marker'))));
  });

  test('no wound found means no analytics', () {
    final result = pipeline(FakeSegmenter(mask: Uint8List(kInputW * kInputH), found: false), c: FakeCalibrator())
        .analyze(photo, 1280, 960, scale: const UniformScale(0.5));
    expect(result.colourRegions, isEmpty);
    expect(() => result.toAnalytics(), throwsA(isA<MeasurementIncomplete>().having((e) => e.reason, 'reason', contains('no wound'))));
  });

  test('every step is timed', () {
    final result = pipeline(FakeSegmenter(mask: blob(), found: true), c: FakeCalibrator()).analyze(photo, 1280, 960, scale: const UniformScale(1));
    expect(result.timingsMs.keys.toSet(), {'prepare', 'calibration', 'segmentation', 'colour', 'area'});
  });
}
