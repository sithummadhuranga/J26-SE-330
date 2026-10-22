import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/measurement/colour_regions.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/analysis_session.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/demo_result_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/photo_overlay.dart';
import 'package:melanin_wound_cdss/features/measurement/image_prepare.dart';
import 'package:melanin_wound_cdss/features/measurement/measurement_pipeline.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_segmenter.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

/// A session that returns a made-up measurement, so the screen can be tested without a model.
class FakeSession implements AnalysisSession {
  FakeSession({this.found = true});

  final bool found;
  bool gpu = true;

  @override
  String get photoDescription => '4000 x 3000 pixels, shown to the model at 640 x 480';

  @override
  String get modelDescription => 'test model';

  @override
  bool get usingGpu => gpu;

  @override
  Future<void> useGpu(bool value) async => gpu = value;

  @override
  Future<MeasurementResult> run(double confidence) async {
    final mask = Uint8List(kInputW * kInputH);
    if (found) {
      for (var y = 100; y < 120; y++) {
        for (var x = 200; x < 250; x++) {
          mask[y * kInputW + x] = 1;
        }
      }
    }
    final canvas = Uint8List(kInputW * kInputH * 4)..fillRange(0, kInputW * kInputH * 4, 120);
    return MeasurementResult(
      image: PreparedImage(rgba: canvas, scale: 0.16, padLeft: 0, padTop: 0, originalWidth: 4000, originalHeight: 3000),
      segmentation: WoundSegmentation(
        detections: found ? [SegDetection(200, 100, 250, 120, 0.9, Float32List(kMaskDim))] : const [],
        mask: mask,
        areaPixels: found ? 1000 : 0,
        inferenceMs: 46,
        postprocessMs: 4,
      ),
      colourRegions: found ? const [ColourRegion(1, 55.6, [180, 90, 60]), ColourRegion(2, 44.4, [120, 60, 40])] : const [],
      areaMm2: null,
      fitzpatrickClass: null,
      calibrationVersion: 'none',
      segmentationVersion: 'test',
      notes: const [],
      timingsMs: const {'prepare': 300, 'segmentation': 46, 'colour': 3, 'area': 0.1},
    );
  }
}

void main() {
  Widget app(AnalysisSession s) => MaterialApp(theme: woundTheme(), home: DemoResultScreen(session: s));

  testWidgets('shows the wound, the colour regions, the speed and that the area needs a scale', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 2400));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(app(FakeSession()));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    expect(find.text('Wound found.'), findsOneWidget);
    expect(find.text('Group 1'), findsOneWidget);
    expect(find.text('55.6%'), findsOneWidget);
    expect(find.text('44.4%'), findsOneWidget);
    expect(find.text('needs a scale: tap "Set scale"'), findsOneWidget);
    expect(find.text('46.0 ms'), findsOneWidget); // the model time
    expect(find.textContaining('Not complete yet'), findsOneWidget);
    expect(find.textContaining('reference marker'), findsOneWidget); // why the wound event cannot be written yet
  });

  testWidgets('says so when no wound is found', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 2400));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(app(FakeSession(found: false)));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.textContaining('No wound found'), findsOneWidget);
    expect(find.text('Wound found.'), findsNothing);
  });

  testWidgets('two taps on a known length turn the pixel count into mm²', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 2400));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(app(FakeSession()));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));

    await tester.tap(find.text('Set scale'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextField), '50');
    await tester.tap(find.text('Tap its ends'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Tap one end of the known length'), findsOneWidget);

    // The photo is shown 768 logical pixels wide for 640 model pixels, so two taps 120 pixels apart are 100 model pixels.
    final topLeft = tester.getTopLeft(find.byType(PhotoOverlay));
    await tester.tapAt(topLeft + const Offset(100, 100));
    await tester.tapAt(topLeft + const Offset(220, 100));
    await tester.pump();

    final text = (tester.widget(find.byKey(const Key('areaValue'))) as Text).data!;
    final area = double.parse(text.replaceAll(' mm²', ''));
    // 50 mm over 100 pixels is 0.5 mm per pixel, so 1000 pixels are 250 mm².
    expect(area, closeTo(250, 3));
  });

  test('the overlay is red inside the wound, white on its edge and clear elsewhere', () {
    final mask = Uint8List(kInputW * kInputH);
    for (var y = 10; y < 15; y++) {
      for (var x = 10; x < 15; x++) {
        mask[y * kInputW + x] = 1;
      }
    }
    final o = buildOverlayRgba(mask);
    List<int> px(int x, int y) => o.sublist(4 * (y * kInputW + x), 4 * (y * kInputW + x) + 4);
    expect(px(12, 12), [255, 64, 56, 105]); // inside
    expect(px(10, 12), [255, 255, 255, 255]); // edge
    expect(px(30, 30), [0, 0, 0, 0]); // outside
  });
}
