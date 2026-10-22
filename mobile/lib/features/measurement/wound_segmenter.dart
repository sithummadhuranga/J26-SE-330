import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_litert/flutter_litert.dart';

import 'yolo_seg_postprocess.dart';

/// Result of one run, in the 480x640 input image.
class WoundSegmentation {
  const WoundSegmentation({
    required this.detections,
    required this.mask,
    required this.areaPixels,
    required this.inferenceMs,
    required this.postprocessMs,
  });

  /// All ulcers found, best score first.
  final List<SegDetection> detections;

  /// All ulcers merged, 480x640 row-major, 0 or 1.
  final Uint8List mask;

  /// Mask pixel count. Multiply by the marker's mm² per pixel to get the area.
  final int areaPixels;

  /// Time for the model, and for the post-processing after it.
  final double inferenceMs;
  final double postprocessMs;
}

/// Anything that can segment a wound in the model's 480x640 input. The real one is [WoundSegmenter]; tests use fakes.
abstract class Segmenter {
  /// [input]: the R plane, then G, then B, values 0..1, length 3 x 480 x 640.
  WoundSegmentation segment(Float32List input, {double conf = 0.25});
}

/// Runs the exported YOLO11n-seg model (input 1x3x480x640 float32, RGB, 0..1) and post-processes the output.
class WoundSegmenter implements Segmenter {
  WoundSegmenter._(this._interpreter, this._gpu);

  final Interpreter _interpreter;
  final GpuDelegateV2? _gpu;
  final Float32List _pred = Float32List(37 * kAnchors);
  final Float32List _proto = Float32List(kMaskDim * kProtoH * kProtoW);

  /// [useGpu] adds the Android GPU delegate; the model then runs in 16-bit float on the GPU, so check its accuracy.
  factory WoundSegmenter.fromFile(File modelFile, {int threads = 4, bool useGpu = false}) {
    final options = InterpreterOptions()..threads = threads;
    final gpu = useGpu ? GpuDelegateV2() : null;
    if (gpu != null) options.addDelegate(gpu);
    return WoundSegmenter._(Interpreter.fromFile(modelFile, options: options), gpu);
  }

  @override
  WoundSegmentation segment(Float32List input, {double conf = 0.25}) {
    assert(input.length == 3 * kInputH * kInputW, 'expected a 3x480x640 input');
    final t0 = Stopwatch()..start();
    _interpreter.runForMultipleInputs([input.buffer], {0: _pred, 1: _proto});
    final inferenceMs = t0.elapsedMicroseconds / 1000;

    final t1 = Stopwatch()..start();
    final detections = decodeDetections(_pred, conf: conf);
    final mask = unionMask(_proto, detections);
    final postprocessMs = t1.elapsedMicroseconds / 1000;
    return WoundSegmentation(
      detections: detections,
      mask: mask,
      areaPixels: maskArea(mask),
      inferenceMs: inferenceMs,
      postprocessMs: postprocessMs,
    );
  }

  void close() {
    _interpreter.close();
    _gpu?.delete();
  }
}

/// Converts the RGBA pixels of a 640x480 image into the model's input layout. Other sizes need resizing first, which
/// comes with the capture screen.
Float32List rgbaToModelInput(Uint8List rgba, int width, int height) {
  if (width != kInputW || height != kInputH) {
    throw ArgumentError('only $kInputW x $kInputH images are supported so far, got $width x $height');
  }
  final plane = width * height;
  final out = Float32List(3 * plane);
  for (var p = 0; p < plane; p++) {
    out[p] = rgba[4 * p] / 255.0;
    out[plane + p] = rgba[4 * p + 1] / 255.0;
    out[2 * plane + p] = rgba[4 * p + 2] / 255.0;
  }
  return out;
}
