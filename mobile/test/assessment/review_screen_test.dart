import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/assessment/assessment_draft.dart';
import 'package:melanin_wound_cdss/features/assessment/processing_screen.dart';
import 'package:melanin_wound_cdss/features/assessment/review_screen.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_check.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_runner.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/capture_verdict.dart';
import 'package:melanin_wound_cdss/features/measurement/capture/sticker_gate.dart';
import 'package:melanin_wound_cdss/features/measurement/light_calibrator.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';

import '../measurement/analysis_session_test.dart' show tinyPng;
import '../sync/support/fakes.dart';

CheckedPhoto _checked(Verdict v, [String? finderCode]) {
  final finder = [for (final c in FinderCode.values) if (c.code == finderCode) c].firstOrNull;
  return CheckedPhoto(
    4,
    2,
    CaptureCheck(
      GateScan(finder == null ? const [GateSticker([(0.5, 0.2), (3.5, 0.3), (3.4, 1.8), (0.6, 1.7)], 1, 0.9, 0.9)] : const [], finder, 0.5, 0.5, 1),
      null,
      const SkinType(fitzpatrick: 'III', skinPixels: 5000),
      CaptureVerdict(v, finderCode),
    ),
  );
}

class _StopsEarly implements WoundAnalyzer {
  int calls = 0;

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    calls++;
    throw AnalysisFailed('stopped for the test', stage: AnalysisStage.calibration);
  }
}

void main() {
  late AppServices services;
  final png = base64Decode(tinyPng);

  Future<AssessmentDraft> show(WidgetTester tester, {Uint8List? bytes, Future<CheckedPhoto> Function(Uint8List)? checker, WoundAnalyzer? analyzer}) async {
    tester.view
      ..physicalSize = const Size(1080, 2400)
      ..devicePixelRatio = 2.6;
    addTearDown(tester.view.reset);
    final gateway = FakeGateway();
    services = await AppServices.open(
      db: AppDatabase.inMemory(),
      secrets: MemorySecretStore(),
      api: gateway,
      browser: FakeBrowserSignIn(gateway),
      online: const Stream.empty(),
      analyzer: analyzer,
    );
    final draft = AssessmentDraft()..capture = WoundCapture(imageBytes: bytes);
    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: ReviewScreen(services: services, draft: draft, checker: checker)));
    addTearDown(() async {
      await tester.pumpWidget(const SizedBox());
      services.scheduler.dispose();
      await services.db.close();
    });
    return draft;
  }

  testWidgets('without a photo (the sample flow) the review is the old one', (tester) async {
    await show(tester);
    expect(find.text('Review Image'), findsOneWidget);
    expect(find.text('Detected'), findsOneWidget);
    expect(find.byKey(const Key('useImage')), findsOneWidget);
  });

  testWidgets('with a photo it says it is checking, then shows the verdict instead of the made-up rows', (tester) async {
    final gate = Completer<CheckedPhoto>();
    await show(tester, bytes: png, checker: (_) => gate.future);
    expect(find.text('Review Image'), findsOneWidget);
    expect(find.textContaining('Checking the photo'), findsOneWidget);
    expect(find.text('Detected'), findsNothing);
    gate.complete(_checked(Verdict.ok));
    await tester.pumpAndSettle();
    expect(find.text('Ready'), findsOneWidget);
    expect(find.text('Detected'), findsNothing);
    expect(find.byKey(const Key('useImage')), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Use this image'), findsOneWidget);
  });

  testWidgets('the photo that was taken is the one that is checked', (tester) async {
    Uint8List? seen;
    await show(tester, bytes: png, checker: (b) async {
      seen = b;
      return _checked(Verdict.ok);
    });
    await tester.pumpAndSettle();
    expect(seen, png);
  });

  testWidgets('ok: Use this image goes on to the analysis', (tester) async {
    final analyzer = _StopsEarly();
    await show(tester, bytes: png, checker: (_) async => _checked(Verdict.ok), analyzer: analyzer);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('useImage')));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byType(ProcessingScreen), findsOneWidget);
    expect(analyzer.calls, 1);
  });

  testWidgets('warn: Retake is the main button and the photo can still be used', (tester) async {
    final analyzer = _StopsEarly();
    await show(tester, bytes: png, checker: (_) async => _checked(Verdict.warn), analyzer: analyzer);
    await tester.pumpAndSettle();
    expect(find.text('Check'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Retake'), findsOneWidget);
    await tester.tap(find.widgetWithText(OutlinedButton, 'Use anyway'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(find.byType(ProcessingScreen), findsOneWidget);
  });

  testWidgets('retake: there is no way to use the photo, only to retake it', (tester) async {
    final analyzer = _StopsEarly();
    await show(tester, bytes: png, checker: (_) async => _checked(Verdict.retake, 'weak_frame_contrast'), analyzer: analyzer);
    await tester.pumpAndSettle();
    expect(find.text('Glare or shadow on the sticker.'), findsOneWidget);
    expect(find.byKey(const Key('useImage')), findsNothing);
    expect(find.text('Use anyway'), findsNothing);
    expect(find.text('Measure without a sticker'), findsNothing);
    expect(find.widgetWithText(FilledButton, 'Retake'), findsOneWidget);
    expect(analyzer.calls, 0);
  });

  testWidgets('a photo that cannot be opened says so and offers Retake', (tester) async {
    await show(tester, bytes: png, checker: (_) async => throw const FormatException('bad picture'));
    await tester.pumpAndSettle();
    expect(find.textContaining('could not be opened'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Retake'), findsOneWidget);
    expect(find.byKey(const Key('useImage')), findsNothing);
  });
}
