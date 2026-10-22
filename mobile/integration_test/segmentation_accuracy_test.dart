import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:path_provider/path_provider.dart';

/// Measures accuracy with the real model running on a device: Dice and IoU against the DFUC2022 ground-truth masks, and the
/// time per image.
///
/// Opt in with --dart-define=WOUND_ACCURACY=true. It needs the model and a set of images in the app's files folder
/// wound_accuracy. make_accuracy_set.py (in research/IT23294066/on-device-export) writes ID.jpg, ID.gt.u8 and manifest.json,
/// and push_parity_files.sh copies them over with the model; pass wound_accuracy as its last argument. The images are
/// DFUC2022 photos (research use only), so they are pushed for the test and never bundled in the app.
///
/// Add --dart-define=WOUND_THREADS=n for the CPU thread count (default 4) or --dart-define=WOUND_GPU=true for the GPU.
///
/// The score is compared with the Python result for the same model and images, which the manifest holds. They should agree
/// within 0.01 Dice. Timings are from whatever device runs this. An emulator is not a phone, so take latency from the real
/// handset.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const enabled = bool.fromEnvironment('WOUND_ACCURACY');
  const threads = int.fromEnvironment('WOUND_THREADS', defaultValue: 4);
  const useGpu = bool.fromEnvironment('WOUND_GPU');
  const conf = 0.25;

  testWidgets('on-device Dice matches the Python result', skip: !enabled, (tester) async {
    final base = Directory('${(await getApplicationSupportDirectory()).path}/wound_accuracy');
    final modelFile = File('${base.path}/model.tflite');
    final manifestFile = File('${base.path}/manifest.json');
    // flutter test uninstalls the app afterwards, so the files are pushed right after it installs. Wait for them.
    for (var waited = 0; waited < 300 && !(modelFile.existsSync() && manifestFile.existsSync()); waited++) {
      await Future<void>.delayed(const Duration(seconds: 1));
    }
    if (!modelFile.existsSync() || !manifestFile.existsSync()) {
      // ignore: avoid_print
      print('SKIPPED: no model or test files in ${base.path}');
      return;
    }
    final manifest = jsonDecode(manifestFile.readAsStringSync()) as Map<String, dynamic>;
    final ids = (manifest['ids'] as List).cast<String>();
    // The manifest can show up before the last files finish copying.
    for (var waited = 0; waited < 120 && !File('${base.path}/${ids.last}.gt.u8').existsSync(); waited++) {
      await Future<void>.delayed(const Duration(seconds: 1));
    }
    await Future<void>.delayed(const Duration(seconds: 2));

    final segmenter = WoundSegmenter.fromFile(modelFile, threads: threads, useGpu: useGpu);
    final dice = <double>[];
    final iou = <double>[];
    final inferenceMs = <double>[];
    final postMs = <double>[];
    var inter = 0, predSum = 0, gtSum = 0, missed = 0;
    var first = true;
    for (final id in ids) {
      final codec = await ui.instantiateImageCodec(File('${base.path}/$id.jpg').readAsBytesSync());
      final frame = (await codec.getNextFrame()).image;
      final rgba = (await frame.toByteData(format: ui.ImageByteFormat.rawRgba))!.buffer.asUint8List();
      final input = rgbaToModelInput(rgba, frame.width, frame.height);
      if (first) {
        segmenter.segment(input, conf: conf); // warm-up, not counted
        first = false;
      }
      final result = segmenter.segment(input, conf: conf);
      inferenceMs.add(result.inferenceMs);
      postMs.add(result.postprocessMs);

      final gt = File('${base.path}/$id.gt.u8').readAsBytesSync();
      var i = 0, g = 0;
      for (var p = 0; p < gt.length; p++) {
        g += gt[p];
        if (gt[p] == 1 && result.mask[p] == 1) i++;
      }
      final pred = result.areaPixels;
      dice.add(pred + g == 0 ? 1 : 2 * i / (pred + g));
      iou.add(pred + g - i == 0 ? 1 : i / (pred + g - i));
      inter += i;
      predSum += pred;
      gtSum += g;
      if (pred == 0) missed++;
    }
    segmenter.close();

    double mean(List<double> v) => v.reduce((a, b) => a + b) / v.length;
    double pct(List<double> v, double q) => (List.of(v)..sort())[((v.length - 1) * q).round()];
    final meanDice = mean(dice);
    final pythonDice = (manifest['python_mean_dice'] as num).toDouble();
    // ignore: avoid_print
    print('RESULT ${ids.length} images, model ${manifest['model']}, ${useGpu ? 'GPU' : '$threads CPU threads'}, conf $conf: '
        'mean Dice ${meanDice.toStringAsFixed(4)} (Python ${pythonDice.toStringAsFixed(4)}), '
        'mean IoU ${mean(iou).toStringAsFixed(4)} (Python ${(manifest['python_mean_iou'] as num).toStringAsFixed(4)}), '
        'pooled Dice ${(2 * inter / (predSum + gtSum)).toStringAsFixed(4)} (Python ${(manifest['python_pooled_dice'] as num).toStringAsFixed(4)}), '
        'missed $missed (Python ${manifest['python_missed']}); '
        'model ms median ${pct(inferenceMs, 0.5).toStringAsFixed(1)} p90 ${pct(inferenceMs, 0.9).toStringAsFixed(1)}, '
        'post ms median ${pct(postMs, 0.5).toStringAsFixed(1)} p90 ${pct(postMs, 0.9).toStringAsFixed(1)}');
    expect((meanDice - pythonDice).abs(), lessThanOrEqualTo(0.01), reason: 'device Dice differs from the Python result');
  });
}
