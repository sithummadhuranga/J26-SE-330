import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/recommendations/guidance_screen.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';

import '../../sync/support/fakes.dart';

/// The guidance screen on a contract 1.0 response as the Recommendation Service writes it. Passage texts are
/// placeholders, never guideline text.
void main() {
  late AppServices services;
  late FakeGateway gateway;
  late List<int> png;

  Map<String, dynamic> response({Map<String, dynamic>? escalation, List<Map<String, dynamic>>? figures}) => {
        'contractVersion': '1.0',
        'mode': 'extractive',
        'corpusVersion': 'iwgdf-2023.r2',
        'sections': [
          {'heading': 'Management', 'text': 'First passage [S1]\nSecond passage [S2]', 'citationTags': ['S1', 'S2']},
          {'heading': 'Contraindications', 'text': 'Third passage [S3]', 'citationTags': ['S3']},
        ],
        'citations': [
          {'tag': 'S1', 'chunkHash': 'h1', 'source': 'PAD', 'chunkId': 'PAD-R01', 'pdfIndex': 12,
            'recommendation': 'PAD-3', 'polarity': 'RECOMMEND', 'grade': 'Strong; Moderate'},
          {'tag': 'S2', 'chunkHash': 'h2', 'source': 'Offloading', 'chunkId': 'OFF-P04', 'pdfIndex': 30},
          {'tag': 'S3', 'chunkHash': 'h3', 'source': 'PAD', 'chunkId': 'PAD-R09', 'pdfIndex': 15,
            'recommendation': 'PAD-9', 'polarity': 'PROHIBIT', 'grade': 'Best Practice Statement'},
        ],
        'withheld': [
          {'missingField': 'ulcerLocation', 'blockedCount': 16, 'message': '16 recommendations withheld: ulcer location not recorded.'},
          {'missingField': 'healingTrajectory', 'blockedCount': 1,
            'message': '1 recommendation withheld: healing trajectory not available (no earlier measurement).'},
        ],
        'nextBestAssessment': ['ulcerLocation', 'infectionGrade'],
        'escalation': escalation ?? {'triggered': false, 'clock': 'capturedAt', 'reason': 'no earlier measurement'},
        'figures': figures ?? const [],
      };

  Map<String, dynamic> figure(String id) => {
        'figureId': id,
        'corpusVersion': 'iwgdf-2023.r2',
        'label': 'Figure $id',
        'caption': 'Caption of $id',
        'source': 'PAD',
        'pdfIndex': 20,
        'citedBy': ['S1'],
        'attribution': '© 2023 payload attribution',
      };

  Future<void> settle(WidgetTester tester, [int rounds = 25]) async {
    for (var i = 0; i < rounds; i++) {
      await tester.pump(const Duration(milliseconds: 20));
    }
  }

  /// [served] are the figure ids the gateway holds; [offline] cuts the phone off after sign-in.
  Future<void> show(WidgetTester tester, Map<String, dynamic> payload,
      {bool signIn = true, List<String> served = const [], bool offline = false}) async {
    tester.view
      ..physicalSize = const Size(1080, 2340)
      ..devicePixelRatio = 2.6;
    addTearDown(tester.view.reset);
    png = (await tester.runAsync(() async {
      final recorder = ui.PictureRecorder();
      Canvas(recorder).drawRect(const Rect.fromLTWH(0, 0, 4, 4), Paint()..color = const Color(0xFF000000));
      final image = await recorder.endRecording().toImage(4, 4);
      return (await image.toByteData(format: ui.ImageByteFormat.png))!.buffer.asUint8List();
    }))!;
    gateway = FakeGateway();
    for (final id in served) {
      gateway.figures['iwgdf-2023.r2/$id'] = png;
    }
    services = await AppServices.open(
        db: AppDatabase.inMemory(), secrets: MemorySecretStore(), api: gateway, online: const Stream.empty());
    if (signIn) await services.signIn(username: 'n.silva', password: 'correct-password');
    gateway.reachable = !offline;
    await tester.pumpWidget(MaterialApp(
      theme: woundTheme(),
      home: GuidanceScreen(services: services, payload: payload, receivedAt: DateTime(2026, 10, 19, 9, 30)),
    ));
    await settle(tester);
  }

  Future<void> close(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox());
    await settle(tester);
    services.scheduler.dispose();
    final closing = services.db.close();
    await settle(tester);
    await closing;
  }

  Future<void> scrollTo(WidgetTester tester, Finder f) async {
    await tester.scrollUntilVisible(f, 200, scrollable: find.byType(Scrollable).first);
    await settle(tester);
  }

  testWidgets('each section is shown with its heading, its text unchanged and its tags with their sources',
      (tester) async {
    await show(tester, response());
    expect(find.text('Evidence-Based Guidance'), findsOneWidget);
    expect(find.text('Guideline extracts'), findsOneWidget);
    expect(find.text('Management'), findsOneWidget);
    expect(find.text('First passage [S1]\nSecond passage [S2]'), findsOneWidget);
    expect(find.text('S1 · PAD'), findsOneWidget);
    expect(find.text('S2 · Offloading'), findsOneWidget);
    await scrollTo(tester, find.text('Third passage [S3]'));
    expect(find.text('Contraindications'), findsOneWidget);
    await close(tester);
  });

  testWidgets('a stalled wound shows the escalation with its weeks, area reduction and rule', (tester) async {
    await show(tester, response(escalation: {
      'triggered': true, 'rule': 'IWGDF-STALL', 'par': 12.5, 'elapsedWeeks': 6.0, 'clock': 'capturedAt',
    }));
    final banner = find.byKey(const Key('escalation'));
    expect(banner, findsOneWidget);
    expect(find.descendant(of: banner, matching: find.textContaining('Healing may be stalled after 6 weeks')),
        findsOneWidget);
    expect(find.descendant(of: banner, matching: find.textContaining('12.5% area reduction')), findsOneWidget);
    expect(find.descendant(of: banner, matching: find.textContaining('IWGDF-STALL')), findsOneWidget);
    expect(find.byKey(const Key('trajectory')), findsNothing, reason: 'the banner already says it');
    await close(tester);
  });

  testWidgets('without escalation the trajectory says what was measured, or why it was not computed',
      (tester) async {
    await show(tester, response());
    expect(find.byKey(const Key('escalation')), findsNothing);
    expect(find.text('Healing trajectory not computed: no earlier measurement.'), findsOneWidget);
    await close(tester);

    await show(tester, response(escalation: {
      'triggered': false, 'rule': 'IWGDF-STALL', 'par': 55.0, 'elapsedWeeks': 4.3, 'clock': 'capturedAt',
    }));
    expect(find.text('Healing trajectory: 55% area reduction over 4.3 weeks.'), findsOneWidget);
    await close(tester);

    await show(tester, response(escalation: {
      'triggered': false, 'rule': 'IWGDF-STALL', 'par': -10.0, 'elapsedWeeks': 2.0, 'clock': 'capturedAt',
    }));
    expect(find.text('Healing trajectory: area grew 10% over 2 weeks.'), findsOneWidget);
    await close(tester);
  });

  testWidgets('withheld guidance names each field with its reason, and what to record next in order', (tester) async {
    await show(tester, response());
    await scrollTo(tester, find.byKey(const Key('recordNext')));
    expect(find.text('Ulcer location'), findsOneWidget);
    expect(find.text('16 withheld'), findsOneWidget);
    expect(find.text('16 recommendations withheld: ulcer location not recorded.'), findsOneWidget);
    expect(find.text('Healing trajectory'), findsOneWidget);
    expect(find.text('Record next: Ulcer location, Infection grade'), findsOneWidget);
    await close(tester);
  });

  testWidgets('a cited figure is shown whole from the cache with its label, caption, citing tags and attribution',
      (tester) async {
    await show(tester, response(figures: [figure('PAD-F1')]), served: ['PAD-F1']);
    await scrollTo(tester, find.byKey(const Key('figure-PAD-F1')));

    final card = find.byKey(const Key('figure-PAD-F1'));
    expect(find.descendant(of: card, matching: find.text('Figure PAD-F1')), findsOneWidget);
    expect(find.descendant(of: card, matching: find.text('Caption of PAD-F1')), findsOneWidget);
    expect(find.descendant(of: card, matching: find.text('Cited by S1 · PAD · PDF page 20')), findsOneWidget);
    expect(find.descendant(of: card, matching: find.text('Test corpus')), findsOneWidget,
        reason: 'the attribution that came with the image');
    final image = tester.widget<Image>(find.descendant(of: card, matching: find.byType(Image)));
    expect((image.image as MemoryImage).bytes, png);
    expect(image.fit, BoxFit.contain, reason: 'never cropped');
    expect(image.color, isNull, reason: 'never recoloured');

    await tester.tap(find.descendant(of: card, matching: find.text('View full screen')));
    await settle(tester);
    expect(find.text('Figure PAD-F1'), findsOneWidget, reason: 'the viewer is titled with the label');
    expect(find.byType(InteractiveViewer), findsOneWidget, reason: 'opens full screen to zoom');
    expect(find.text('Test corpus'), findsOneWidget);
    await close(tester);
  });

  testWidgets('a figure that is not cached says why, and the payload attribution still credits it', (tester) async {
    await show(tester, response(figures: [figure('PAD-F1')]), served: ['PAD-F1'], offline: true);
    await scrollTo(tester, find.byKey(const Key('figure-PAD-F1')));
    final card = find.byKey(const Key('figure-PAD-F1'));
    expect(find.descendant(of: card, matching: find.text('The figure shows once the phone is back online.')),
        findsOneWidget);
    expect(find.descendant(of: card, matching: find.text('© 2023 payload attribution')), findsOneWidget);
    expect(find.descendant(of: card, matching: find.byType(Image)), findsNothing);
    await close(tester);

    await show(tester, response(figures: [figure('PAD-F9')]));
    await scrollTo(tester, find.byKey(const Key('figure-PAD-F9')));
    expect(find.text('This figure is not available.'), findsOneWidget);
    await close(tester);

    await show(tester, response(figures: [figure('PAD-F9')]), signIn: false);
    await scrollTo(tester, find.byKey(const Key('figure-PAD-F9')));
    expect(find.text('Sign in to load this figure.'), findsOneWidget);
    await close(tester);
  });

  testWidgets('the evidence list gives every passage its tag, source, recommendation, grade and PDF page',
      (tester) async {
    await show(tester, response());
    await scrollTo(tester, find.text('Evidence (3 passages)'));
    expect(find.text('Strong; Moderate'), findsNothing, reason: 'collapsed until opened');
    await tester.tap(find.text('Evidence (3 passages)'));
    await settle(tester);
    await scrollTo(tester, find.byKey(const Key('evidence-S3')));

    final s1 = find.byKey(const Key('evidence-S1'));
    expect(find.descendant(of: s1, matching: find.text('S1')), findsOneWidget);
    expect(find.descendant(of: s1, matching: find.text('PAD · Recommendation PAD-3 · PDF page 12')), findsOneWidget);
    expect(find.descendant(of: s1, matching: find.text('Strong; Moderate')), findsOneWidget);
    final s2 = find.byKey(const Key('evidence-S2'));
    expect(find.descendant(of: s2, matching: find.text('Offloading · PDF page 30')), findsOneWidget);
    expect(find.descendant(of: find.byKey(const Key('evidence-S3')), matching: find.text('Best Practice Statement')),
        findsOneWidget);
    await close(tester);
  });

  testWidgets('a minimal response (only sections) shows them and nothing it does not have', (tester) async {
    await show(tester, {
      'mode': 'extractive',
      'sections': [
        {'heading': 'Fake', 'text': 'Fake advice [S1].', 'citationTags': ['S1']}
      ],
    });
    expect(find.text('Fake advice [S1].'), findsOneWidget);
    expect(find.text('S1'), findsOneWidget);
    expect(find.byKey(const Key('escalation')), findsNothing);
    expect(find.byKey(const Key('trajectory')), findsNothing);
    expect(find.byKey(const Key('recordNext')), findsNothing);
    expect(find.textContaining('Evidence ('), findsNothing);
    expect(find.text('Guideline snapshot —'), findsOneWidget);
    await close(tester);
  });
}
