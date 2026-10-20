import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/app/app_shell.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/account/account_api.dart';
import 'package:melanin_wound_cdss/features/assessment/analysis/wound_analyzer.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';

import '../sync/support/fakes.dart';

/// Tests the app's screens on top of the real sync layer, with a fake gateway and an in-memory database.
void main() {
  late AppServices services;
  late FakeGateway gateway;
  late _FakeAccountApi account;
  var signedOut = 0;

  /// Lets the in-memory database (on the same isolate) and Drift's stream updates run on the test's fake clock.
  Future<void> settle(WidgetTester tester, [int rounds = 25]) async {
    for (var i = 0; i < rounds; i++) {
      await tester.pump(const Duration(milliseconds: 20));
    }
  }

  Future<void> open(WidgetTester tester, {bool signIn = true, WoundAnalyzer? analyzer}) async {
    account = _FakeAccountApi();
    signedOut = 0;
    tester.view
      ..physicalSize = const Size(1080, 2340)
      ..devicePixelRatio = 2.6;
    addTearDown(tester.view.reset);
    gateway = FakeGateway();
    services = await AppServices.open(
      db: AppDatabase.inMemory(),
      secrets: MemorySecretStore(),
      api: gateway,
      browser: FakeBrowserSignIn(gateway),
      online: const Stream.empty(),
      analyzer: analyzer,
      account: account,
    );
    if (signIn) await services.signIn();
    await tester.pumpWidget(
      MaterialApp(
        theme: woundTheme(),
        home: AppShell(services: services, onSignedOut: () => signedOut++),
      ),
    );
    await settle(tester);
  }

  Future<void> close(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox()); // cancels the screens' database streams first
    await settle(tester);
    services.scheduler.dispose();
    final closing = services.db.close();
    await settle(tester);
    await closing;
  }

  Future<void> tapAndSettle(WidgetTester tester, Finder f) async {
    await tester.ensureVisible(f);
    await tester.pump();
    await tester.tap(f);
    await settle(tester);
  }

  testWidgets('new assessment: answers, capture, analysis, saved, synced, guidance', (tester) async {
    await open(tester);
    expect(find.text('No assessments yet'), findsOneWidget);
    expect(find.textContaining('n.silva'), findsOneWidget, reason: 'the greeting names who is signed in');

    await tapAndSettle(tester, find.byKey(const Key('newAssessment')));
    expect(find.text('New Assessment'), findsOneWidget);
    await tapAndSettle(
      tester,
      find.descendant(of: find.byKey(const Key('pedalPulses')), matching: find.text('Present')),
    );
    await tapAndSettle(
      tester,
      find.descendant(of: find.byKey(const Key('protectiveSensation')), matching: find.text('Absent')),
    );

    await tapAndSettle(tester, find.byKey(const Key('takePhoto')));
    await tapAndSettle(tester, find.byKey(const Key('shutter')));
    expect(find.text('Review Image'), findsOneWidget);
    await tapAndSettle(tester, find.byKey(const Key('useImage')));
    expect(find.text('Analyzing Assessment'), findsOneWidget);

    // Four analysis steps, then the save, then the sync that brings the advice back.
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(seconds: 1));
      await settle(tester);
    }
    expect(find.text('Analysis complete'), findsOneWidget);
    expect(gateway.storedEvents, hasLength(1), reason: 'the assessment reached the server');
    final stored = gateway.storedEvents.values.single;
    expect(stored['clinicalAssessment'], {'pedalPulses': 'present', 'protectiveSensation': 'absent'});
    expect(stored['patientRef'], matches(RegExp(r'^p-[0-9a-f]{6,32}$')), reason: 'unlinked: a fresh pseudonymous ref');

    await tapAndSettle(tester, find.byKey(const Key('viewResults')));
    expect(find.text('Assessment Summary'), findsOneWidget);
    expect(find.byKey(const Key('woundArea')), findsOneWidget);
    expect(find.text('Advice ready'), findsOneWidget);
    // It's a long page, so scroll down to the clinical answers and the guidance button like a clinician would.
    final page = find.byType(Scrollable).first;
    await tester.scrollUntilVisible(find.text('Present'), 200, scrollable: page);
    expect(find.text('Absent'), findsOneWidget);
    await tester.scrollUntilVisible(find.byKey(const Key('viewGuidance')), 200, scrollable: page);
    await settle(tester);

    await tapAndSettle(tester, find.byKey(const Key('viewGuidance')));
    expect(find.text('Evidence-Based Guidance'), findsOneWidget);
    expect(find.text('Fake advice [S1].'), findsOneWidget);
    await tester.pageBack();
    await settle(tester);

    await tapAndSettle(tester, find.byKey(const Key('done')));
    expect(find.text('No assessments yet'), findsNothing);
    expect(find.text('Advice ready'), findsOneWidget, reason: 'Home lists the new assessment');
    await close(tester);
  });

  testWidgets('patients: add one by label, and the label reaches the server on the next sync', (tester) async {
    await open(tester);
    await tapAndSettle(tester, find.text('Patients'));
    expect(find.text('No patients yet'), findsOneWidget);

    await tapAndSettle(tester, find.byKey(const Key('addPatient')));
    await tester.enterText(find.byKey(const Key('patientLabel')), '   ');
    await tapAndSettle(tester, find.byKey(const Key('savePatient')));
    expect(find.text('Enter a label for the patient.'), findsOneWidget);

    await tester.enterText(find.byKey(const Key('patientLabel')), 'A.F. bed 12');
    await tapAndSettle(tester, find.byKey(const Key('savePatient')));
    expect(find.text('A.F. bed 12'), findsOneWidget);

    await tapAndSettle(tester, find.text('A.F. bed 12'));
    expect(find.text('Patient Record'), findsOneWidget);
    expect(find.text('No assessments linked yet'), findsOneWidget);

    unawaited(services.scheduler.syncNow());
    await settle(tester);
    expect(gateway.patientAliases.values, ['A.F. bed 12']);
    expect(gateway.patientAliases.keys.single, matches(RegExp(r'^p-[0-9a-f]{12}$')));
    expect(find.text('Shared'), findsOneWidget);
    await close(tester);
  });

  testWidgets('without a session, Profile and the banner say to sign in again', (tester) async {
    await open(tester, signIn: false);
    await tapAndSettle(tester, find.text('Profile'));
    await tapAndSettle(tester, find.byKey(const Key('syncNow')));
    expect(find.textContaining('Sign in again to sync'), findsOneWidget);
    expect(find.byKey(const Key('offlineBanner')), findsOneWidget);
    await close(tester);
  });

  testWidgets('saving before anyone signed in on this phone is refused with a message, never silently', (tester) async {
    await open(tester, signIn: false);
    await tapAndSettle(tester, find.byKey(const Key('newAssessment')));
    await tapAndSettle(tester, find.byKey(const Key('takePhoto')));
    await tapAndSettle(tester, find.byKey(const Key('shutter')));
    await tapAndSettle(tester, find.byKey(const Key('useImage')));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(seconds: 1));
      await settle(tester);
    }
    expect(find.text('Not saved'), findsOneWidget);
    expect(find.textContaining('Sign in once on this phone before saving assessments.'), findsOneWidget);
    expect(gateway.storedEvents, isEmpty);
    await close(tester);
  });

  testWidgets('a failed analysis says why on its step and saves nothing', (tester) async {
    await open(tester, analyzer: _FailingAnalyzer());
    await tapAndSettle(tester, find.byKey(const Key('newAssessment')));
    await tapAndSettle(tester, find.byKey(const Key('takePhoto')));
    await tapAndSettle(tester, find.byKey(const Key('shutter')));
    await tapAndSettle(tester, find.byKey(const Key('useImage')));
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(seconds: 1));
      await settle(tester);
    }
    expect(find.text('Not saved'), findsOneWidget);
    expect(find.text('Completed'), findsOneWidget, reason: 'calibration finished before segmentation failed');
    expect(
      find.text('Needs attention — Wound not found in the frame. Retake the photo.'),
      findsOneWidget,
      reason: 'the message sits on the segmentation step',
    );
    expect(await services.queue.all(), isEmpty, reason: 'nothing reaches the queue');
    expect(gateway.storedEvents, isEmpty);
    await close(tester);
  });

  testWidgets('when the session ends, tapping the banner signs in again in place and syncs', (tester) async {
    await open(tester, signIn: false);
    await services.queue.enqueue(sampleEvent());
    unawaited(services.scheduler.syncNow());
    await settle(tester);
    expect(find.textContaining('Tap to sign in again'), findsOneWidget);

    await tapAndSettle(tester, find.byKey(const Key('offlineBanner')));
    expect(find.byKey(const Key('signInAgainNote')), findsOneWidget);
    await tapAndSettle(tester, find.byKey(const Key('signInButton'))); // the login page opens and they sign in there
    await settle(tester);

    expect(find.byKey(const Key('signInAgainNote')), findsNothing, reason: 'back on the app, not a new start');
    expect(find.byKey(const Key('offlineBanner')), findsNothing);
    expect(gateway.storedEvents, hasLength(1), reason: 'the waiting assessment went with the sync after sign-in');
    await close(tester);
  });

  testWidgets('signing out with assessments still waiting asks first', (tester) async {
    await open(tester);
    gateway.reachable = false;
    await services.queue.enqueue(sampleEvent());
    await tapAndSettle(tester, find.text('Profile'));

    await tapAndSettle(tester, find.byKey(const Key('signOut')));
    expect(find.textContaining('1 assessment has not reached the server yet'), findsOneWidget);
    await tapAndSettle(tester, find.text('Cancel'));
    expect(await services.auth.hasSession(), isTrue);

    await tapAndSettle(tester, find.byKey(const Key('signOut')));
    await tapAndSettle(tester, find.byKey(const Key('signOutAnyway')));
    expect(await services.auth.hasSession(), isFalse);
    expect((await services.queue.counts()).waitingToSend, 1, reason: 'kept for the next sign-in');
    await close(tester);
  });

  Future<void> openAccount(WidgetTester tester) async {
    account.thisDevice = await services.queue.deviceId();
    await tapAndSettle(tester, find.text('Profile'));
    await tapAndSettle(tester, find.byKey(const Key('account')));
  }

  testWidgets('account: shows your details and sessions, marks this phone, and renames you', (tester) async {
    await open(tester);
    await openAccount(tester);
    expect(find.text('N. Silva'), findsOneWidget);
    expect(find.text('Nurse'), findsOneWidget);
    expect(find.text('This phone'), findsOneWidget);
    expect(find.text('Admin dashboard'), findsOneWidget);

    await tapAndSettle(tester, find.byKey(const Key('editName')));
    await tester.enterText(find.byKey(const Key('nameField')), '   ');
    await tapAndSettle(tester, find.byKey(const Key('saveName')));
    expect(find.text('Enter your full name.'), findsOneWidget, reason: 'checked before anything is sent');
    expect(account.renamedTo, isNull);

    await tester.enterText(find.byKey(const Key('nameField')), 'Nimali Silva');
    await tapAndSettle(tester, find.byKey(const Key('saveName')));
    expect(account.renamedTo, 'Nimali Silva');
    expect(find.text('Nimali Silva'), findsOneWidget);
    await close(tester);
  });

  testWidgets('account: a password change signs this phone out but keeps unsynced assessments', (tester) async {
    await open(tester);
    gateway.reachable = false;
    await services.queue.enqueue(sampleEvent());
    await openAccount(tester);

    await tapAndSettle(tester, find.byKey(const Key('changePassword')));
    await tester.enterText(find.byKey(const Key('currentPassword')), 'old-password');
    await tester.enterText(find.byKey(const Key('newPassword')), 'short');
    await tester.enterText(find.byKey(const Key('confirmPassword')), 'short');
    await tapAndSettle(tester, find.byKey(const Key('savePassword')));
    expect(find.text('At least $minPasswordLength characters.'), findsOneWidget);

    account.passwordError = ApiException(400, 'INVALID_CREDENTIALS');
    await tester.enterText(find.byKey(const Key('newPassword')), 'new-password');
    await tester.enterText(find.byKey(const Key('confirmPassword')), 'new-password');
    await tapAndSettle(tester, find.byKey(const Key('savePassword')));
    expect(find.text('Your current password isn\'t right.'), findsOneWidget);
    expect(await services.auth.hasSession(), isTrue);

    account.passwordError = null;
    await tapAndSettle(tester, find.byKey(const Key('savePassword')));
    expect(account.passwordChanges, 1);
    expect(find.textContaining('Your password was changed'), findsOneWidget);
    await tapAndSettle(tester, find.byKey(const Key('signedOutOk')));
    expect(signedOut, 1, reason: 'the app goes back to sign-in');
    expect(await services.auth.hasSession(), isFalse);
    expect((await services.queue.counts()).waitingToSend, 1, reason: 'kept for the next sign-in');
    await close(tester);
  });

  testWidgets('account: offline shows why, and tries again', (tester) async {
    await open(tester);
    account.offline = true;
    await openAccount(tester);
    expect(find.text('Couldn\'t load your account'), findsOneWidget);
    expect(find.textContaining('Can\'t reach the server'), findsOneWidget);

    account.offline = false;
    await tapAndSettle(tester, find.byKey(const Key('retry')));
    expect(find.text('N. Silva'), findsOneWidget);
    await close(tester);
  });
}

/// Gets through calibration, then can't find the wound, the way a real segmentation model might fail.
class _FailingAnalyzer implements WoundAnalyzer {
  @override
  Future<Map<String, dynamic>> analyze(WoundCapture capture, {void Function(AnalysisStage stage)? onStage}) async {
    onStage?.call(AnalysisStage.calibration);
    await Future<void>.delayed(const Duration(milliseconds: 300));
    onStage?.call(AnalysisStage.segmentation);
    await Future<void>.delayed(const Duration(milliseconds: 300));
    throw AnalysisFailed('Wound not found in the frame. Retake the photo.', stage: AnalysisStage.segmentation);
  }
}

/// The account endpoints, answered in memory.
class _FakeAccountApi implements AccountApi {
  String thisDevice = 'dev-test';
  bool offline = false;
  String? renamedTo;
  int passwordChanges = 0;
  Object? passwordError;
  var _profile = MyProfile(
    username: 'n.silva',
    fullName: 'N. Silva',
    role: 'nurse',
    facilityId: 'fac-001',
    mfaEnabled: false,
    createdAt: DateTime(2026, 10, 20, 9),
  );

  void _online() {
    if (offline) throw TransportException('unreachable');
  }

  @override
  Future<MyProfile> profile() async {
    _online();
    return _profile;
  }

  @override
  Future<MyProfile> rename(String fullName) async {
    _online();
    renamedTo = fullName;
    return _profile = MyProfile(
      username: _profile.username,
      fullName: fullName,
      role: _profile.role,
      facilityId: _profile.facilityId,
      mfaEnabled: _profile.mfaEnabled,
      createdAt: _profile.createdAt,
    );
  }

  @override
  Future<void> changePassword(String currentPassword, String newPassword) async {
    _online();
    if (passwordError case final e?) throw e;
    passwordChanges++;
  }

  @override
  Future<List<MySession>> sessions() async {
    _online();
    return [
      MySession(
        sessionId: 's-phone',
        client: 'mobile',
        deviceId: thisDevice,
        signedInAt: DateTime(2026, 10, 20, 9),
        lastUsedAt: DateTime(2026, 10, 20, 10),
      ),
      MySession(
        sessionId: 's-dashboard',
        client: 'admin-dashboard',
        signedInAt: DateTime(2026, 10, 20, 8),
        lastUsedAt: DateTime(2026, 10, 20, 8, 30),
      ),
    ];
  }

  @override
  Future<void> endSession(String sessionId) async => _online();

  @override
  Future<void> endAllSessions() async => _online();
}
