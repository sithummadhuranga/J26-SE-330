import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_check.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/photo_check_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/sticker_gate.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/analysis_session.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/demo_home_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/demo_result_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/measurement_demo_main.dart';

import 'analysis_session_test.dart' show FakeEngine, tinyPng;
import 'demo_result_screen_test.dart' show FakeSession;

/// A capture check with the given outcome, so the tests need no real photo of a sticker.
CaptureCheck fakeCheck(Verdict v, [String? finderCode]) {
  final finder = [for (final c in FinderCode.values) if (c.code == finderCode) c].firstOrNull;
  return CaptureCheck(
    GateScan(finder == null ? const [GateSticker([(0.5, 0.2), (3.5, 0.3), (3.4, 1.8), (0.6, 1.7)], 1, 0.9, 0.9)] : const [], finder, 0.5, 0.5, 1),
    null,
    const SkinType(fitzpatrick: 'III', skinPixels: 5000),
    CaptureVerdict(v, finderCode),
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final png = base64Decode(tinyPng);
  late Directory dir;
  late List<File> samples;

  setUp(() {
    dir = Directory.systemTemp.createTempSync('home_test_');
    samples = [for (final n in ['a.png', 'b.png', 'c.png']) File('${dir.path}/$n')..writeAsBytesSync(png)];
  });

  tearDown(() => dir.deleteSync(recursive: true));

  Widget app({
    FakeEngine? engine,
    Future<List<File>> Function()? loadSamples,
    Future<List<int>?> Function(ImageSource)? picker,
    List<DecodedPhoto>? opened,
    Future<CaptureCheck> Function(DecodedPhoto)? checker,
    List<DecodedPhoto>? checked,
  }) =>
      MaterialApp(
        theme: woundTheme(),
        home: DemoHomeScreen(
          engine: engine ?? FakeEngine(),
          loadSamples: loadSamples ?? () async => const [],
          picker: picker ?? (_) async => null,
          checker: (photo) async {
            checked?.add(photo);
            return checker == null ? fakeCheck(Verdict.ok) : checker(photo);
          },
          sessionFactory: (e, photo) {
            opened?.add(photo);
            return FakeSession();
          },
        ),
      );

  /// Real file reads and image decoding finish on the real event loop, so give them a few rounds.
  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 4; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await tester.pump(const Duration(milliseconds: 100));
    }
  }

  testWidgets('without a model it says so, shows why, and the scan buttons are off', (tester) async {
    await tester.pumpWidget(app(engine: FakeEngine(readyAfterLoad: false)));
    await settle(tester);
    expect(find.text('The model is not ready'), findsOneWidget);
    expect(find.textContaining('The model file is not on this phone yet'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Scan a wound with the camera')).onPressed, isNull);
    expect(tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'Choose a photo')).onPressed, isNull);
    expect(find.textContaining('None on this phone yet'), findsOneWidget);
  });

  testWidgets('with the model on the GPU it says so with the file size', (tester) async {
    await tester.pumpWidget(app());
    await settle(tester);
    expect(find.text('Model ready (GPU)'), findsOneWidget);
    expect(find.text('yolo11n-seg-dfuc2022-fp32, 11.2 MB'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Scan a wound with the camera')).onPressed, isNotNull);
  });

  testWidgets('with the CPU it says CPU and shows why the GPU was not used', (tester) async {
    final e = FakeEngine(gpuAtStart: false)..gpuNote = 'The GPU could not be used (x), so the CPU is used.';
    await tester.pumpWidget(app(engine: e));
    await settle(tester);
    expect(find.text('Model ready (CPU)'), findsOneWidget);
    expect(find.textContaining('The GPU could not be used (x)'), findsOneWidget);
  });

  testWidgets('sample photos are shown in a grid with the privacy line', (tester) async {
    await tester.pumpWidget(app(loadSamples: () async => samples));
    await settle(tester);
    expect(find.byType(Image), findsNWidgets(3));
    expect(find.textContaining('Tap a photo to measure it'), findsOneWidget);
    expect(find.textContaining('not in the gallery'), findsOneWidget);
    expect(find.textContaining('None on this phone yet'), findsNothing);
  });

  testWidgets('"Check again" loads the model and the sample list again', (tester) async {
    final engine = FakeEngine();
    var lists = 0;
    await tester.pumpWidget(app(engine: engine, loadSamples: () async {
      lists++;
      return samples;
    }));
    await settle(tester);
    expect([engine.loadCalls, lists], [1, 1]);
    await tester.tap(find.text('Check again'));
    await settle(tester);
    expect([engine.loadCalls, lists], [2, 2]);
  });

  testWidgets('choosing a photo from the gallery opens the result with the decoded photo', (tester) async {
    final asked = <ImageSource>[];
    final opened = <DecodedPhoto>[];
    await tester.pumpWidget(app(picker: (s) async {
      asked.add(s);
      return png;
    }, opened: opened));
    await settle(tester);
    await tester.tap(find.text('Choose a photo'));
    await settle(tester);
    expect(asked, [ImageSource.gallery]);
    expect(find.byType(PhotoCheckScreen), findsOneWidget, reason: 'the photo is checked before it is measured');
    expect(find.byType(DemoResultScreen), findsNothing);
    expect(opened, isEmpty);
    await tester.tap(find.text('Measure this photo'));
    await settle(tester);
    expect(find.byType(DemoResultScreen), findsOneWidget);
    expect([opened.single.width, opened.single.height], [4, 2]);
  });

  testWidgets('the camera button asks for the camera', (tester) async {
    final asked = <ImageSource>[];
    await tester.pumpWidget(app(picker: (s) async {
      asked.add(s);
      return png;
    }));
    await settle(tester);
    await tester.tap(find.text('Scan a wound with the camera'));
    await settle(tester);
    expect(asked, [ImageSource.camera]);
    await tester.tap(find.text('Measure this photo'));
    await settle(tester);
    expect(find.byType(DemoResultScreen), findsOneWidget);
  });

  testWidgets('cancelling the picker closes the progress circle and opens nothing', (tester) async {
    await tester.pumpWidget(app(picker: (_) async => null));
    await settle(tester);
    await tester.tap(find.text('Choose a photo'));
    await settle(tester);
    expect(find.byType(DemoResultScreen), findsNothing);
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('tapping a sample photo measures that photo', (tester) async {
    await tester.binding.setSurfaceSize(const Size(800, 1800));            // tall enough that the photo grid is on screen
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final opened = <DecodedPhoto>[];
    await tester.pumpWidget(app(loadSamples: () async => samples, opened: opened));
    await settle(tester);
    // The tap starts a real file read; run it on the real event loop so it can finish.
    await tester.runAsync(() => tester.tap(find.descendant(of: find.byType(GridView), matching: find.byType(InkWell)).first));
    await settle(tester);
    expect(find.byType(PhotoCheckScreen), findsOneWidget);
    await tester.tap(find.text('Measure this photo'));
    await settle(tester);
    expect(find.byType(DemoResultScreen), findsOneWidget);
    expect(opened, hasLength(1));
  });

  testWidgets('the check gets the decoded photo, and its verdict is what the screen shows', (tester) async {
    final checked = <DecodedPhoto>[];
    await tester.pumpWidget(app(picker: (_) async => png, checked: checked, checker: (_) async => fakeCheck(Verdict.warn)));
    await settle(tester);
    await tester.tap(find.text('Choose a photo'));
    await settle(tester);
    expect([checked.single.width, checked.single.height], [4, 2]);
    expect(find.text('Check'), findsOneWidget);
    expect(find.text('Measure anyway'), findsOneWidget);
  });

  testWidgets('a photo that must be retaken goes back to the start, and measures nothing', (tester) async {
    final opened = <DecodedPhoto>[];
    await tester.pumpWidget(app(picker: (_) async => png, opened: opened, checker: (_) async => fakeCheck(Verdict.retake, 'mirrored')));
    await settle(tester);
    await tester.tap(find.text('Choose a photo'));
    await settle(tester);
    expect(find.text('The sticker looks mirrored.'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Retake'));
    await tester.pumpAndSettle();
    expect(find.byType(PhotoCheckScreen), findsNothing);
    expect(find.text('Scan a wound with the camera'), findsOneWidget);
    expect(opened, isEmpty);
  });

  testWidgets('the demo can still measure a photo that has no sticker, after saying so', (tester) async {
    final opened = <DecodedPhoto>[];
    await tester.pumpWidget(app(picker: (_) async => png, opened: opened, checker: (_) async => fakeCheck(Verdict.retake, 'no_candidate')));
    await settle(tester);
    await tester.tap(find.text('Choose a photo'));
    await settle(tester);
    expect(find.text('No sticker found.'), findsOneWidget);
    await tester.tap(find.text('Measure without a sticker'));
    await settle(tester);
    expect(find.byType(DemoResultScreen), findsOneWidget);
    expect(opened, hasLength(1));
  });

  testWidgets('the demo app opens on this screen and says the model is missing when it is', (tester) async {
    final support = Directory.systemTemp.createTempSync('app_test_');
    addTearDown(() => support.deleteSync(recursive: true));
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.flutter.io/path_provider'),
      (call) async => call.method == 'getApplicationSupportDirectory' ? support.path : null,
    );
    addTearDown(() => TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(const MethodChannel('plugins.flutter.io/path_provider'), null));
    await tester.pumpWidget(const MeasurementDemoApp());
    await settle(tester);
    expect(find.text('Wound measurement demo'), findsWidgets);
    expect(find.text('The model is not ready'), findsOneWidget);
    expect(find.textContaining('Research prototype, not for clinical use'), findsOneWidget);
    expect(tester.widget<MaterialApp>(find.byType(MaterialApp)).debugShowCheckedModeBanner, isFalse);
  });
}
