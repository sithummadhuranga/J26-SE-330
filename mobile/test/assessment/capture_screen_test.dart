import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/assessment/assessment_draft.dart';
import 'package:melanin_wound_cdss/features/assessment/capture_screen.dart';
import 'package:melanin_wound_cdss/features/assessment/photo_source.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';

import '../sync/support/fakes.dart';

class _Source implements PhotoSource {
  _Source(this.result);

  final WoundCapture? result;
  final asked = <PhotoOrigin>[];

  @override
  bool get isSample => false;

  @override
  Future<WoundCapture?> pick(PhotoOrigin origin) async {
    asked.add(origin);
    return result;
  }
}

void main() {
  late AppServices services;

  Future<AssessmentDraft> show(WidgetTester tester, PhotoSource source) async {
    tester.view
      ..physicalSize = const Size(1080, 2340)
      ..devicePixelRatio = 2.6;
    addTearDown(tester.view.reset);
    final gateway = FakeGateway();
    services = await AppServices.open(
      db: AppDatabase.inMemory(),
      secrets: MemorySecretStore(),
      api: gateway,
      browser: FakeBrowserSignIn(gateway),
      online: const Stream.empty(),
      photoSource: source,
    );
    final draft = AssessmentDraft();
    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: CaptureScreen(services: services, draft: draft)));
    await tester.pump();
    addTearDown(() async {
      await tester.pumpWidget(const SizedBox());
      services.scheduler.dispose();
      await services.db.close();
    });
    return draft;
  }

  testWidgets('with a camera, the shutter keeps the photo in the draft and goes on to the review', (tester) async {
    final photo = WoundCapture(imageBytes: Uint8List.fromList([9, 9, 9]));
    final source = _Source(photo);
    final draft = await show(tester, source);
    await tester.tap(find.byKey(const Key('shutter')));
    await tester.pumpAndSettle();
    expect(source.asked, [PhotoOrigin.camera]);
    expect(draft.capture.imageBytes, [9, 9, 9]);
    expect(find.text('Review Image'), findsOneWidget);
  });

  testWidgets('"Choose from device" asks for the gallery', (tester) async {
    final source = _Source(WoundCapture(imageBytes: Uint8List.fromList([1])));
    await show(tester, source);
    await tester.tap(find.text('Choose from device'));
    await tester.pumpAndSettle();
    expect(source.asked, [PhotoOrigin.device]);
    expect(find.text('Review Image'), findsOneWidget);
  });

  testWidgets('cancelling the camera stays on the capture screen with an empty draft', (tester) async {
    final draft = await show(tester, _Source(null));
    await tester.tap(find.byKey(const Key('shutter')));
    await tester.pumpAndSettle();
    expect(find.text('Capture Image'), findsOneWidget);
    expect(draft.capture.imageBytes, isNull);
  });

  testWidgets('with the sample source the old flow is unchanged and the prototype notice stays', (tester) async {
    final draft = await show(tester, const SamplePhotoSource());
    expect(find.textContaining('Capturing here uses a sample image'), findsOneWidget);
    await tester.tap(find.byKey(const Key('shutter')));
    await tester.pumpAndSettle();
    expect(find.text('Review Image'), findsOneWidget);
    expect(draft.capture.imageBytes, isNull);
  });

  testWidgets('with a real camera the notice says photos are checked on the phone and not uploaded', (tester) async {
    await show(tester, _Source(null));
    expect(find.textContaining('Capturing here uses a sample image'), findsNothing);
    expect(find.textContaining('checked on this phone'), findsOneWidget);
    expect(find.textContaining('not uploaded'), findsOneWidget);
  });

  testWidgets('a camera that fails shows the reason instead of nothing', (tester) async {
    final failing = _Failing();
    await show(tester, failing);
    await tester.tap(find.byKey(const Key('shutter')));
    await tester.pumpAndSettle();
    expect(find.textContaining('Choose from device'), findsWidgets);
    expect(find.text('Capture Image'), findsOneWidget);
  });
}

class _Failing implements PhotoSource {
  @override
  bool get isSample => false;

  @override
  Future<WoundCapture?> pick(PhotoOrigin origin) async => throw const PhotoUnavailable('The camera is not available. Use Choose from device.');
}
