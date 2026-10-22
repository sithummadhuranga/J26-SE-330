import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

/// Compares the Dart post-processing with golden files from research/IT23294066/on-device-export/litert_reference.py
/// (raw model output plus the expected mask). They contain DFUC2022 photos, which we may not share, so they stay outside
/// the repo. Set WOUND_GOLDEN_DIR to that folder to run this; otherwise the test is skipped.
void main() {
  final dir = Platform.environment['WOUND_GOLDEN_DIR'];

  Float32List readF32(String path) {
    final b = File(path).readAsBytesSync();
    return Float32List.view(b.buffer, b.offsetInBytes, b.lengthInBytes ~/ 4);
  }

  test(
    'detections and merged masks match the Python reference',
    () {
      final manifest = jsonDecode(File('$dir/manifest.json').readAsStringSync()) as Map<String, dynamic>;
      expect(manifest['input_hw'], [kInputH, kInputW]);
      final images = (manifest['images'] as List).cast<Map<String, dynamic>>();
      expect(images, isNotEmpty);

      var worstMismatch = 0;
      for (final img in images) {
        final id = img['id'] as String;
        final dets = decodeDetections(readF32('$dir/$id.pred.f32'));
        expect(dets.length, img['n_det'], reason: 'detection count for $id');

        final scores = (img['scores'] as List).cast<num>();
        final boxes = (img['boxes'] as List).map((b) => (b as List).cast<num>()).toList();
        for (var i = 0; i < dets.length; i++) {
          expect(dets[i].score, closeTo(scores[i].toDouble(), 1e-4), reason: 'score $i of $id');
          expect(dets[i].x1, closeTo(boxes[i][0].toDouble(), 0.01), reason: 'x1 of box $i of $id');
          expect(dets[i].y1, closeTo(boxes[i][1].toDouble(), 0.01), reason: 'y1 of box $i of $id');
          expect(dets[i].x2, closeTo(boxes[i][2].toDouble(), 0.01), reason: 'x2 of box $i of $id');
          expect(dets[i].y2, closeTo(boxes[i][3].toDouble(), 0.01), reason: 'y2 of box $i of $id');
        }

        final mask = unionMask(readF32('$dir/$id.proto.f32'), dets);
        final golden = File('$dir/$id.union.u8').readAsBytesSync();
        expect(mask.length, golden.length);
        var mismatch = 0;
        for (var p = 0; p < mask.length; p++) {
          if (mask[p] != golden[p]) mismatch++;
        }
        worstMismatch = mismatch > worstMismatch ? mismatch : worstMismatch;
        final goldenArea = maskArea(Uint8List.fromList(golden));
        // numpy uses float32 and Dart uses double, so a pixel on a mask edge can flip.
        expect(mismatch, lessThanOrEqualTo(5 + goldenArea ~/ 2000), reason: 'differing pixels for $id');
      }
      // ignore: avoid_print
      print('${images.length} golden images checked; worst case $worstMismatch differing pixels out of ${kInputH * kInputW}');
    },
    skip: dir == null ? 'set WOUND_GOLDEN_DIR to run' : false,
  );
}
