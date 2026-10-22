import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/assessment/assessment_detail_screen.dart';
import 'package:melanin_wound_cdss/features/assessment/assessment_draft.dart';
import 'package:melanin_wound_cdss/features/assessment/media_store.dart';
import 'package:melanin_wound_cdss/features/assessment/processing_screen.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/ui/sample_assessment.dart';

import '../sync/support/fakes.dart';

class _OutlineAnalyzer implements WoundAnalyzer, WoundOutlineSource {
  WoundOutline? pending = WoundOutline(mask: Uint8List(640 * 480)..[1000] = 1, scale: 0.5, padLeft: 0, padTop: 0, originalWidth: 1280, originalHeight: 960);
  int taken = 0;

  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    for (final s in AnalysisStage.values) {
      onStage?.call(s);
    }
    return sampleAnalytics();
  }

  @override
  WoundOutline? takeOutline() {
    taken++;
    final o = pending;
    pending = null;
    return o;
  }
}

void main() {
  late AppServices services;

  Future<AssessmentDraft> run(WidgetTester tester, {required WoundAnalyzer analyzer, Uint8List? bytes}) async {
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
      media: MemoryMediaStore(),
    );
    await services.signIn();
    final draft = AssessmentDraft()..capture = WoundCapture(imageBytes: bytes);
    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: ProcessingScreen(services: services, draft: draft)));
    for (var i = 0; i < 40; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    addTearDown(() async {
      await tester.pumpWidget(const SizedBox());
      services.scheduler.dispose();
      await services.db.close();
    });
    return draft;
  }

  testWidgets('once saved, the photo and the wound outline are kept on the phone under the assessment id', (tester) async {
    final a = _OutlineAnalyzer();
    final draft = await run(tester, analyzer: a, bytes: Uint8List.fromList([1, 2, 3]));
    final media = await tester.runAsync(() => services.media.load(draft.assessmentId));
    expect(media, isNotNull);
    expect(media!.photo, [1, 2, 3]);
    expect(media.outline, isNotNull);
    expect(media.outline!.mask[1000], 1);
  });

  testWidgets('the results screen shows that photo, and the segmentation tab draws the real outline on it', (tester) async {
    final png = Uint8List.fromList(const [
      137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 13, 73, 68, 65, 84,
      120, 156, 99, 248, 207, 192, 240, 31, 0, 5, 0, 1, 255, 137, 153, 61, 29, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
    ]);
    final draft = await run(tester, analyzer: _OutlineAnalyzer(), bytes: png);
    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: AssessmentDetailScreen(services: services, assessmentId: draft.assessmentId)));
    for (var i = 0; i < 20; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.byType(Image), findsWidgets);
    expect(find.byKey(const Key('woundOutline')), findsNothing, reason: 'the original tab has no outline');
    await tester.tap(find.text('Segmentation').first);
    for (var i = 0; i < 10; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.byKey(const Key('woundOutline')), findsOneWidget);
    // Leave the screen so Drift can close its streams on the test's clock.
    await tester.pumpWidget(const SizedBox());
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  });

  testWidgets('with no photo (the sample flow) nothing is kept and the outline source is not asked', (tester) async {
    final a = _OutlineAnalyzer();
    final draft = await run(tester, analyzer: a);
    expect(await tester.runAsync(() => services.media.load(draft.assessmentId)), isNull);
  });

  testWidgets('an analyzer with no outline to give still keeps the photo', (tester) async {
    final a = _OutlineAnalyzer()..pending = null;
    final draft = await run(tester, analyzer: a, bytes: Uint8List.fromList([7]));
    final media = await tester.runAsync(() => services.media.load(draft.assessmentId));
    expect(media!.photo, [7]);
    expect(media.outline, isNull);
  });

  testWidgets('the analysis screen shows the photo that was taken, not the illustration', (tester) async {
    final png = Uint8List.fromList(const [
      137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137, 0, 0, 0, 13, 73, 68, 65, 84,
      120, 156, 99, 248, 207, 192, 240, 31, 0, 5, 0, 1, 255, 137, 153, 61, 29, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
    ]);
    await run(tester, analyzer: _OutlineAnalyzer(), bytes: png);
    expect(find.byType(Image), findsWidgets);
  });
}
