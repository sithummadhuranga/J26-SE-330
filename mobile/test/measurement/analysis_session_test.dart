import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/analysis_session.dart';
import 'package:melanin_wound_cdss/features/measurement/measurement_pipeline.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

// A 4 x 2 PNG: red, green, blue, white / black, grey, yellow, cyan.
const tinyPng = 'iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAIAAADwyuo0AAAAGklEQVR4nGP4z8DAAMb//4MohoaGBhDj/38AhaALd0K3BmMAAAAASUVORK5CYII=';

/// A segmenter that finds one wound, or fails the first time (a phone whose GPU breaks at the first run).
class OneWoundSegmenter implements Segmenter {
  OneWoundSegmenter({this.failFirst = false});

  final bool failFirst;
  int calls = 0;

  @override
  WoundSegmentation segment(Float32List input, {double conf = 0.25}) {
    calls++;
    if (failFirst && calls == 1) throw StateError('GPU delegate failed');
    final mask = Uint8List(kInputW * kInputH);
    for (var y = 100; y < 120; y++) {
      for (var x = 200; x < 250; x++) {
        mask[y * kInputW + x] = 1;
      }
    }
    return WoundSegmentation(
      detections: [SegDetection(200, 100, 250, 120, 0.9, Float32List(kMaskDim))],
      mask: mask,
      areaPixels: 1000,
      inferenceMs: 5,
      postprocessMs: 1,
    );
  }
}

/// An engine without a model file: the pipeline is built around a fake segmenter.
class FakeEngine extends DemoEngine {
  FakeEngine({this.gpuAtStart = true, this.failGpuRun = false, this.readyAfterLoad = true}) : super();

  final bool gpuAtStart, failGpuRun, readyAfterLoad;
  bool _ready = true;
  int loadCalls = 0, pipelineCalls = 0;
  double? lastConfidence;

  @override
  bool get ready => _ready;

  @override
  Future<void> load() async {
    loadCalls++;
    _ready = readyAfterLoad;
    gpuActive = gpuWanted && gpuAtStart;
    modelBytes = (11.2 * 1048576).round();
    loadError = readyAfterLoad ? null : 'The model file is not on this phone yet (x).';
  }

  @override
  MeasurementPipeline pipeline(double confidence, {Calibrator? calibrator}) {
    pipelineCalls++;
    lastConfidence = confidence;
    return MeasurementPipeline(
      segmenter: OneWoundSegmenter(failFirst: failGpuRun && gpuActive),
      segmentationVersion: versionName,
      confidence: confidence,
      calibrator: calibrator,
    );
  }
}

Uint8List rgba(int w, int h) => Uint8List.fromList([for (var i = 0; i < w * h; i++) ...[180, 90, 60, 255]]);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('decodePhoto', () {
    test('returns the size and the RGBA pixels as the phone decoder delivers them', () async {
      final p = await decodePhoto(base64Decode(tinyPng));
      expect([p.width, p.height], [4, 2]);
      expect(p.rgba.length, 4 * 2 * 4);
      expect(p.rgba.sublist(0, 4), [255, 0, 0, 255]);            // red
      expect(p.rgba.sublist(8, 12), [0, 0, 255, 255]);           // blue
      expect(p.rgba.sublist(16, 20), [0, 0, 0, 255]);            // second row starts with black
      expect(p.rgba.sublist(24, 28), [255, 255, 0, 255]);        // yellow
    });

    test('a file that is not a picture is an error', () async {
      await expectLater(decodePhoto(Uint8List.fromList([1, 2, 3, 4])), throwsA(anything));
    });
  });

  group('where the demo finds its files', () {
    late Directory support;

    setUp(() {
      support = Directory.systemTemp.createTempSync('demo_files_');
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
        const MethodChannel('plugins.flutter.io/path_provider'),
        (call) async => call.method == 'getApplicationSupportDirectory' ? support.path : null,
      );
    });

    tearDown(() {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(const MethodChannel('plugins.flutter.io/path_provider'), null);
      support.deleteSync(recursive: true);
    });

    test('the model is expected in the models folder, found when it is there', () async {
      expect((await DemoEngine.modelFile()).path, '${support.path}/models/wound_seg.tflite');
      expect((await DemoEngine.modelFile()).existsSync(), isFalse);
      Directory('${support.path}/models').createSync();
      File('${support.path}/models/wound_seg.tflite').writeAsBytesSync([1, 2, 3]);
      expect((await DemoEngine.modelFile()).existsSync(), isTrue);
    });

    test('sample photos are the jpg, jpeg and png files, in name order, and nothing else', () async {
      expect(await DemoEngine.samplePhotos(), isEmpty);                     // no demo_photos folder yet
      final dir = Directory('${support.path}/demo_photos')..createSync();
      for (final n in ['b.png', 'a.JPG', 'c.txt', 'd.jpeg', 'e.PNG', 'notes']) {
        File('${dir.path}/$n').writeAsBytesSync([0]);
      }
      Directory('${dir.path}/sub.jpg').createSync();                        // a folder is not a photo
      expect((await DemoEngine.samplePhotos()).map((f) => f.path.split('/').last).toList(), ['a.JPG', 'b.png', 'd.jpeg', 'e.PNG']);
    });

    test('a missing model gives a clear message and an engine that is not ready', () async {
      final e = DemoEngine();
      await e.load();
      expect(e.ready, isFalse);
      expect(e.loadError, contains('The model file is not on this phone yet'));
      expect(e.loadError, contains('wound_seg.tflite'));
      e.dispose();
    });
  });

  group('EngineSession', () {
    test('describes the photo and the model', () {
      final engine = FakeEngine()..gpuNote = 'The GPU could not be used (x), so the CPU is used.';
      engine.modelBytes = (11.2 * 1048576).round();
      final s = EngineSession(engine, DecodedPhoto(64, 48, rgba(64, 48)));
      expect(s.photoDescription, '64 x 48 pixels, shown to the model at 640 x 480');
      expect(s.modelDescription, 'yolo11n-seg-dfuc2022-fp32, 11.2 MB\nThe GPU could not be used (x), so the CPU is used.');
    });

    test('the photo is prepared once and reused when the settings change', () async {
      final engine = FakeEngine();
      await engine.load();
      final s = EngineSession(engine, DecodedPhoto(64, 48, rgba(64, 48)));
      final first = await s.run(0.25);
      final second = await s.run(0.5);
      expect(first.timingsMs['prepare'], isNotNull);
      expect(second.timingsMs['prepare'], first.timingsMs['prepare']);       // the same preparation time: it was not redone
      expect(engine.pipelineCalls, 2);
      expect(engine.lastConfidence, 0.5);
      expect(first.segmentation.detections, hasLength(1));
      expect(first.image.originalWidth, 64);
    });

    test('the GPU switch reloads the model and says so when it cannot', () async {
      final engine = FakeEngine();
      await engine.load();
      final s = EngineSession(engine, DecodedPhoto(64, 48, rgba(64, 48)));
      expect(s.usingGpu, isTrue);
      await s.useGpu(false);
      expect(engine.gpuWanted, isFalse);
      expect(engine.loadCalls, 2);
      expect(s.usingGpu, isFalse);
      final broken = FakeEngine(readyAfterLoad: false);
      await expectLater(EngineSession(broken, DecodedPhoto(64, 48, rgba(64, 48))).useGpu(true), throwsA(isA<StateError>()));
    });

    test('a GPU that fails at the first run falls back to the CPU and measures again', () async {
      final engine = FakeEngine(failGpuRun: true);
      await engine.load();
      expect(engine.gpuActive, isTrue);
      final s = EngineSession(engine, DecodedPhoto(64, 48, rgba(64, 48)));
      final result = await s.run(0.25);
      expect(result.segmentation.detections, hasLength(1));
      expect(engine.gpuWanted, isFalse);
      expect(engine.gpuActive, isFalse);
      expect(engine.loadCalls, 2);
    });

    test('a failure on the CPU is passed on, not hidden', () async {
      final engine = FakeEngine(gpuAtStart: false, failGpuRun: true);
      await engine.load();
      // the CPU engine never fails in the fake, so force a failure through a pipeline that throws
      final failing = _ThrowingEngine();
      await failing.load();
      final s = EngineSession(failing, DecodedPhoto(64, 48, rgba(64, 48)));
      await expectLater(s.run(0.25), throwsA(isA<StateError>()));
      expect(failing.loadCalls, 1);                                          // no fallback when the CPU was already in use
    });
  });
}

class _ThrowingEngine extends FakeEngine {
  _ThrowingEngine() : super(gpuAtStart: false);

  @override
  MeasurementPipeline pipeline(double confidence, {Calibrator? calibrator}) => throw StateError('the model failed');
}
