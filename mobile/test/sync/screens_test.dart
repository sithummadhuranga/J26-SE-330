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
  late FakeBrowserSignIn browser;

  Future<void> open(WidgetTester tester) async {
    gateway = FakeGateway();
    browser = FakeBrowserSignIn(gateway);
    services = await tester.runAsync(() => AppServices.open(
          db: AppDatabase.inMemory(),
          secrets: MemorySecretStore(),
          api: gateway,
          browser: browser,
          online: const Stream.empty(),
        )) as AppServices;
  }

  Future<void> close(WidgetTester tester) async {
    await tester.pumpWidget(const SizedBox());
    await tester.runAsync(services.close);
  }

  Future<void> tapSignIn(WidgetTester tester) async {
    await tester.runAsync(() async {
      await tester.tap(find.byKey(const Key('signInButton')));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
  }

  testWidgets('sign-in: the app asks for no password; closing the login page, then signing in there', (tester) async {
    await open(tester);
    var signedIn = false;
    await tester.pumpWidget(MaterialApp(home: SignInScreen(services: services, onSignedIn: () => signedIn = true)));

    expect(find.byType(TextField), findsNothing, reason: 'the password and MFA code are entered on the login page');
    expect(find.byKey(const Key('signInHint')), findsOneWidget);

    browser.cancel = true;
    await tapSignIn(tester);
    expect(signedIn, isFalse);
    expect(find.byKey(const Key('signInError')), findsNothing, reason: 'closing the page is not an error');

    browser.cancel = false;
    await tapSignIn(tester);
    expect(signedIn, isTrue);
    expect(browser.deviceIds.toSet(), {await tester.runAsync(services.queue.deviceId)},
        reason: "the login page is told which phone this is (device_id)");
    final cached = await tester.runAsync(services.cachedClinician);
    expect(cached?.username, 'n.silva', reason: 'who is signed in is cached for offline display, from the ID token');
    await close(tester);
  });

  testWidgets('sign-in offline says the saved work is safe', (tester) async {
    await open(tester);
    gateway.reachable = false;
    await tester.pumpWidget(MaterialApp(home: SignInScreen(services: services, onSignedIn: () {})));

    await tapSignIn(tester);
    expect(find.text(SignInScreen.offlineMessage), findsOneWidget);
    await close(tester);
  });
}
