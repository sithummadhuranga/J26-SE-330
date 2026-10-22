import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:path_provider/path_provider.dart';

/// Runs the real model on a device and compares it with the Python reference.
///
/// Opt in with --dart-define=WOUND_PARITY=true. It needs these files in the app's files folder
/// (research/IT23294066/on-device-export/push_parity_files.sh copies them there):
///   wound_parity/model.tflite, plus ID.jpg, ID.rgb.u8, ID.union.u8 and manifest.json from litert_reference.py
/// The photos are DFUC2022 images (research use only), so they are pushed for the test and never bundled in the app.
///
/// The device decodes JPEGs and rounds floats a little differently, so a detection scoring within 0.05 of the threshold
/// may flip. Everything else must match, and masks may differ by up to 2.5% of the area (plus 20 px for tiny wounds).
///
/// Timings are from whatever device runs this. An emulator is not a phone, so take latency from the real handset.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const conf = 0.25;
  const borderline = 0.05;
  const enabled = bool.fromEnvironment('WOUND_PARITY');

  testWidgets('on-device model reproduces the Python reference masks', skip: !enabled, (tester) async {
    final base = Directory('${(await getApplicationSupportDirectory()).path}/wound_parity');
    final modelFile = File('${base.path}/model.tflite');
    final manifestFile = File('${base.path}/manifest.json');
    // flutter test uninstalls the app afterwards, so the files are pushed right after it installs. Wait for them.
    for (var waited = 0; waited < 180 && !(modelFile.existsSync() && manifestFile.existsSync()); waited++) {
      await Future<void>.delayed(const Duration(seconds: 1));
    }
    if (!modelFile.existsSync() || !manifestFile.existsSync()) {
      // ignore: avoid_print
      print('SKIPPED: no model or golden files in ${base.path}');
      return;
    }
    // Let the copy finish writing before reading.
    await Future<void>.delayed(const Duration(seconds: 3));
    final manifest = jsonDecode(manifestFile.readAsStringSync()) as Map<String, dynamic>;
    final images = (manifest['images'] as List).cast<Map<String, dynamic>>();

    final segmenter = WoundSegmenter.fromFile(modelFile);
    final inference = <double>[];
    final post = <double>[];
    final failures = <String>[];
    var worstMaskPct = 0.0;
    for (final img in images) {
      final id = img['id'] as String;
      final codec = await ui.instantiateImageCodec(File('${base.path}/$id.jpg').readAsBytesSync());
      final frame = (await codec.getNextFrame()).image;
      final rgba = (await frame.toByteData(format: ui.ImageByteFormat.rawRgba))!.buffer.asUint8List();

      // Difference between the device's JPEG decoder and OpenCV's, per colour value (0..255).
      var decodeDiff = 'n/a';
      final rgbFile = File('${base.path}/$id.rgb.u8');
      if (rgbFile.existsSync()) {
        final ref = rgbFile.readAsBytesSync();
        var sum = 0, maxd = 0;
        for (var p = 0; p < frame.width * frame.height; p++) {
          for (var c = 0; c < 3; c++) {
            final d = (rgba[4 * p + c] - ref[3 * p + c]).abs();
            sum += d;
            if (d > maxd) maxd = d;
          }
        }
        decodeDiff = 'mean ${(sum / (3 * frame.width * frame.height)).toStringAsFixed(3)}, max $maxd';
      }

      final input = rgbaToModelInput(rgba, frame.width, frame.height);
      segmenter.segment(input, conf: conf); // warm-up, not timed
      final result = segmenter.segment(input, conf: conf);
      inference.add(result.inferenceMs);
      post.add(result.postprocessMs);

      final scores = (img['scores'] as List).cast<num>();
      final nBorderline = scores.where((s) => (s - conf).abs() <= borderline).length;
      final expected = img['n_det'] as int;
      if ((result.detections.length - expected).abs() > nBorderline) {
        failures.add('$id: ${result.detections.length} detections, expected $expected');
      }

      final golden = File('${base.path}/$id.union.u8').readAsBytesSync();
      var mismatch = 0, goldenArea = 0;
      for (var p = 0; p < golden.length; p++) {
        if (result.mask[p] != golden[p]) mismatch++;
        goldenArea += golden[p];
      }
      final maskPct = goldenArea == 0 ? (mismatch == 0 ? 0.0 : 100.0) : 100.0 * mismatch / goldenArea;
      final flipped = result.detections.length != expected;
      if (!flipped) {
        if (maskPct > worstMaskPct) worstMaskPct = maskPct;
        if (mismatch > 0.025 * goldenArea + 20) failures.add('$id: $mismatch differing pixels of $goldenArea');
      }
      // ignore: avoid_print
      print('$id: ${result.detections.length}/$expected detections${flipped ? ' (borderline flip)' : ''}, '
          'area ${result.areaPixels} vs ${img['union_pixels']} px, $mismatch differing px (${maskPct.toStringAsFixed(2)} %), '
          'decode diff $decodeDiff, model ${result.inferenceMs.toStringAsFixed(1)} ms + post ${result.postprocessMs.toStringAsFixed(1)} ms');
    }
    segmenter.close();

    double pct(List<double> v, double q) => (List.of(v)..sort())[((v.length - 1) * q).round()];
    // ignore: avoid_print
    print('SUMMARY ${images.length} images: worst mask difference ${worstMaskPct.toStringAsFixed(2)} % (unflipped images); '
        'model ms median ${pct(inference, 0.5).toStringAsFixed(1)} p90 ${pct(inference, 0.9).toStringAsFixed(1)}; '
        'post ms median ${pct(post, 0.5).toStringAsFixed(1)} p90 ${pct(post, 0.9).toStringAsFixed(1)}');
    expect(failures, isEmpty, reason: failures.join('; '));
  });
}
