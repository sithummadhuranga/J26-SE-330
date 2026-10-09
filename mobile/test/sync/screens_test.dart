import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/ui/sign_in_screen.dart';

import 'support/fakes.dart';

/// The sign-in screen against the fake gateway and an in-memory database (the app's other screens: test/app).
void main() {
  late AppServices services;
  late FakeGateway gateway;

  Future<void> open(WidgetTester tester, {bool requireMfa = false}) async {
    gateway = FakeGateway(requireMfa: requireMfa);
    services = await tester.runAsync(() => AppServices.open(
          db: AppDatabase.inMemory(),
          secrets: MemorySecretStore(),
          api: gateway,
          online: const Stream.empty(),
        )) as AppServices;
  }

  Future<void> close(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox());
    await tester.runAsync(services.close);
  }

  testWidgets('sign-in: wrong password, then the MFA code is asked for and accepted', (tester) async {
    await open(tester, requireMfa: true);
    var signedIn = false;
    await tester.pumpWidget(MaterialApp(home: SignInScreen(services: services, onSignedIn: () => signedIn = true)));

    await tester.enterText(find.byKey(const Key('username')), 'n.silva');
    await tester.enterText(find.byKey(const Key('password')), 'wrong');
    await tester.runAsync(() => tester.tap(find.byKey(const Key('signInButton'))));
    await tester.pump();
    expect(find.text(SignInScreen.message('INVALID_CREDENTIALS')), findsOneWidget);
    expect(find.byKey(const Key('totp')), findsNothing);

    await tester.enterText(find.byKey(const Key('password')), 'correct-password');
    await tester.runAsync(() => tester.tap(find.byKey(const Key('signInButton'))));
    await tester.pump();
    expect(find.byKey(const Key('totp')), findsOneWidget, reason: 'MFA_REQUIRED shows the code field');
    expect(signedIn, isFalse);

    await tester.enterText(find.byKey(const Key('totp')), '000000');
    await tester.runAsync(() => tester.tap(find.byKey(const Key('signInButton'))));
    await tester.pump();
    expect(find.text(SignInScreen.message('INVALID_TOTP')), findsOneWidget);

    await tester.enterText(find.byKey(const Key('totp')), '123456');
    await tester.runAsync(() async {
      await tester.tap(find.byKey(const Key('signInButton')));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
    expect(signedIn, isTrue);
    final cached = await tester.runAsync(services.cachedClinician);
    expect(cached, isNotNull, reason: 'who is signed in is cached for offline display, without the refresh token');
    await close(tester);
  });
}
