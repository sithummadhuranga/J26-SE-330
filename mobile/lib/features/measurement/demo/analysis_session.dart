import 'dart:async';
import 'dart:io';
import 'dart:isolate';

import 'package:path_provider/path_provider.dart';

import '../capture/decoded_photo.dart';
import '../image_prepare.dart';
import '../measurement_pipeline.dart';
import '../wound_segmenter.dart';

export '../capture/decoded_photo.dart';

/// One photo that can be analysed again with different settings. The demo screens only talk to this, so tests can swap in
/// a fake that needs no model.
abstract class AnalysisSession {
  /// For example "4000 x 3000 pixels, shown to the model at 640 x 480".
  String get photoDescription;

  /// Whether the model is running on the phone's GPU right now.
  bool get usingGpu;

  /// Where the model comes from, for the details section.
  String get modelDescription;

  Future<void> useGpu(bool gpu);

  /// Runs the whole measurement. The photo is fitted to the model input only once; later runs reuse it.
  Future<MeasurementResult> run(double confidence);
}

/// Loads the exported model from the app's private folder and keeps one [WoundSegmenter] alive.
/// The model file is not part of the app: it is copied to the phone for the demo (see the home screen).
class DemoEngine {
  DemoEngine({this.versionName = 'yolo11n-seg-dfuc2022-fp32'});

  final String versionName;
  WoundSegmenter? _segmenter;
  bool gpuWanted = true;
  bool gpuActive = false;
  String? loadError;
  String? gpuNote;
  int? modelBytes;

  bool get ready => _segmenter != null;

  /// The folders the demo reads from: the app's external files folder (filled with adb push, works in release builds) and
  /// its private folder (filled with run-as, debug builds only).
  static Future<List<Directory>> dataFolders() async => [
        if (Platform.isAndroid) ?(await getExternalStorageDirectory()),
        await getApplicationSupportDirectory(),
      ];

  /// The first model file found, or the place where it is expected.
  static Future<File> modelFile() async {
    final folders = await dataFolders();
    for (final d in folders) {
      final f = File('${d.path}/models/wound_seg.tflite');
      if (f.existsSync()) return f;
    }
    return File('${folders.first.path}/models/wound_seg.tflite');
  }

  /// Photos for the demo's "Sample photos" strip, from both folders.
  static Future<List<File>> samplePhotos() async {
    final found = <File>[];
    for (final d in await dataFolders()) {
      final dir = Directory('${d.path}/demo_photos');
      if (dir.existsSync()) {
        found.addAll(dir.listSync().whereType<File>().where((f) => RegExp(r'\.(jpe?g|png)$', caseSensitive: false).hasMatch(f.path)));
      }
    }
    found.sort((a, b) => a.path.split('/').last.compareTo(b.path.split('/').last));
    return found;
  }

  Future<void> load() async {
    _segmenter?.close();
    _segmenter = null;
    loadError = null;
    gpuNote = null;
    gpuActive = false;
    final file = await modelFile();
    if (!file.existsSync()) {
      loadError = 'The model file is not on this phone yet (${file.path}).';
      return;
    }
    modelBytes = file.lengthSync();
    if (gpuWanted) {
      try {
        _segmenter = WoundSegmenter.fromFile(file, useGpu: true);
        gpuActive = true;
        return;
      } catch (e) {
        gpuNote = 'The GPU could not be used ($e), so the CPU is used.';
      }
    }
    try {
      _segmenter = WoundSegmenter.fromFile(file, threads: 4);
    } catch (e) {
      loadError = 'The model could not be loaded: $e';
    }
  }

  MeasurementPipeline pipeline(double confidence, {Calibrator? calibrator}) =>
      MeasurementPipeline(segmenter: _segmenter!, segmentationVersion: versionName, confidence: confidence, calibrator: calibrator);

  void dispose() => _segmenter?.close();
}

/// The real session: decodes the photo, fits it to the model input in a background isolate, and runs the pipeline.
class EngineSession implements AnalysisSession {
  EngineSession(this.engine, this.photo);

  final DemoEngine engine;
  final DecodedPhoto photo;
  PreparedImage? _prepared;
  double _prepareMs = 0;

  @override
  String get photoDescription => '${photo.width} x ${photo.height} pixels, shown to the model at 640 x 480';

  @override
  bool get usingGpu => engine.gpuActive;

  @override
  String get modelDescription =>
      '${engine.versionName}, ${((engine.modelBytes ?? 0) / 1048576).toStringAsFixed(1)} MB'
      '${engine.gpuNote == null ? '' : '\n${engine.gpuNote}'}';

  @override
  Future<void> useGpu(bool gpu) async {
    engine.gpuWanted = gpu;
    await engine.load();
    if (!engine.ready) throw StateError(engine.loadError ?? 'The model is not available.');
  }

  @override
  Future<MeasurementResult> run(double confidence) async {
    if (_prepared == null) {
      final clock = Stopwatch()..start();
      final transfer = TransferableTypedData.fromList([photo.rgba]);
      final w = photo.width, h = photo.height;
      _prepared = await Isolate.run(() => prepareImage(transfer.materialize().asUint8List(), w, h));
      _prepareMs = clock.elapsedMicroseconds / 1000;
    }
    // Let the progress indicator paint before the model blocks this isolate for a moment.
    await Future<void>.delayed(const Duration(milliseconds: 30));
    try {
      return engine.pipeline(confidence).analyzePrepared(_prepared!, prepareMs: _prepareMs);
    } catch (e) {
      if (!engine.gpuActive) rethrow;
      // The GPU can fail at the first run on some phones: fall back to the CPU and try once more.
      engine.gpuWanted = false;
      await engine.load();
      return engine.pipeline(confidence).analyzePrepared(_prepared!, prepareMs: _prepareMs);
    }
  }
}
