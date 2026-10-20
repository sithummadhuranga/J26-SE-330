import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:melanin_wound_cdss/features/account/account_api.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';

import '../sync/support/fakes.dart';

/// The account calls against a scripted server, signed in through the fake gateway.
void main() {
  late FakeGateway gateway;
  late AuthSession auth;

  setUp(() async {
    gateway = FakeGateway();
    auth = AuthSession(gateway, MemorySecretStore(), browser: FakeBrowserSignIn(gateway));
    await auth.signIn(deviceId: 'dev-test');
  });

  HttpAccountApi api(Future<http.Response> Function(http.Request r) handler) =>
      HttpAccountApi(Uri.parse('http://gateway.test'), auth, client: MockClient(handler));

  http.Response json(Object body, [int status = 200]) =>
      http.Response(jsonEncode(body), status, headers: {'content-type': 'application/json'});

  final profileJson = {
    'username': 'n.silva',
    'fullName': 'N. Silva',
    'role': 'nurse',
    'facilityId': 'fac-001',
    'mfaEnabled': false,
    'mustChangePassword': false,
    'createdAt': '2026-10-20T03:33:00Z',
    'lastSignInAt': null,
  };

  test('reads your profile with the access token', () async {
    late http.Request seen;
    final profile = await api((r) async {
      seen = r;
      return json(profileJson);
    }).profile();
    expect(seen.method, 'GET');
    expect(seen.url.path, '/v1/me');
    expect(seen.headers['Authorization'], startsWith('Bearer '));
    expect(profile.fullName, 'N. Silva');
    expect(profile.lastSignInAt, isNull);
  });

  test('on a 401 it refreshes once and tries again', () async {
    final tokens = <String?>[];
    final profile = await api((r) async {
      tokens.add(r.headers['Authorization']);
      return tokens.length == 1 ? json({'code': 'UNAUTHORIZED'}, 401) : json(profileJson);
    }).profile();
    expect(profile.username, 'n.silva');
    expect(gateway.refreshes, 1);
    expect(tokens[0], isNot(tokens[1]), reason: 'the retry used the new token');
  });

  test('sends the name and password as JSON, and lists sessions', () async {
    final bodies = <String, Object?>{};
    final a = api((r) async {
      bodies['${r.method} ${r.url.path}'] = r.body.isEmpty ? null : jsonDecode(r.body);
      return switch ((r.method, r.url.path)) {
        ('PATCH', '/v1/me') => json({...profileJson, 'fullName': 'Nimali Silva'}),
        ('GET', '/v1/me/sessions') => json([
          {
            'sessionId': 'b1f0c2d4-0000-4000-8000-000000000001',
            'client': 'mobile',
            'deviceId': 'dev-test',
            'signedInAt': '2026-10-20T03:33:00Z',
            'lastUsedAt': '2026-10-20T04:00:00Z',
            'expiresAt': '2026-11-19T04:00:00Z',
          },
        ]),
        _ => http.Response('', 204),
      };
    });
    expect((await a.rename('Nimali Silva')).fullName, 'Nimali Silva');
    await a.changePassword('old-password', 'new-password');
    final sessions = await a.sessions();
    await a.endSession(sessions.single.sessionId);
    await a.endAllSessions();
    expect(bodies, {
      'PATCH /v1/me': {'fullName': 'Nimali Silva'},
      'POST /v1/me/password': {'currentPassword': 'old-password', 'newPassword': 'new-password'},
      'GET /v1/me/sessions': null,
      'DELETE /v1/me/sessions/b1f0c2d4-0000-4000-8000-000000000001': null,
      'DELETE /v1/me/sessions': null,
    });
    expect(sessions.single.deviceId, 'dev-test');
  });

  test('server refusals come back as ApiException with the reason, and read as plain messages', () async {
    final a = api((r) async => json({'code': 'INVALID_CREDENTIALS'}, 400));
    await expectLater(
      a.changePassword('wrong', 'new-password'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code', 'INVALID_CREDENTIALS')),
    );
    expect(accountMessage(ApiException(400, 'INVALID_CREDENTIALS')), 'Your current password isn\'t right.');
    expect(accountMessage(ApiException(409, 'CREDENTIAL_LOCKED')), contains('locked'));
    expect(accountMessage(ApiException(500)), 'Something went wrong. Please try again.');
  });

  test('no connection is a TransportException, and a finished session asks to sign in again', () async {
    await expectLater(
      api((r) async => throw http.ClientException('connection refused')).profile(),
      throwsA(isA<TransportException>()),
    );
    expect(accountMessage(TransportException('x')), contains('Can\'t reach the server'));

    gateway.sessionRevoked = true;
    gateway.expireAccessToken();
    await expectLater(
      api((r) async => json({'code': 'UNAUTHORIZED'}, 401)).profile(),
      throwsA(isA<NeedsSignIn>()),
      reason: 'the refresh is refused, so only a new sign-in helps',
    );
  });
}
