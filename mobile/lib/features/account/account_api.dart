import 'dart:convert';

import 'package:http/http.dart' as http;

import '../sync/api/sync_api.dart';
import '../sync/auth/auth_session.dart';

/// The server's rules for your own account (CredentialValidator and ProfileService), checked here first
/// so the clinician hears about them before anything is sent.
const minPasswordLength = 8;
const maxFullNameLength = 120;

/// Your own account (GET /v1/me).
class MyProfile {
  const MyProfile({
    required this.username,
    required this.fullName,
    required this.role,
    required this.facilityId,
    required this.mfaEnabled,
    required this.createdAt,
    this.lastSignInAt,
  });

  factory MyProfile.fromJson(Map<String, dynamic> j) => MyProfile(
    username: j['username'] as String,
    fullName: j['fullName'] as String,
    role: j['role'] as String,
    facilityId: j['facilityId'] as String,
    mfaEnabled: j['mfaEnabled'] as bool,
    createdAt: DateTime.parse(j['createdAt'] as String).toLocal(),
    lastSignInAt: j['lastSignInAt'] == null ? null : DateTime.parse(j['lastSignInAt'] as String).toLocal(),
  );

  final String username;
  final String fullName;
  final String role;
  final String facilityId;
  final bool mfaEnabled;
  final DateTime createdAt;

  /// Null before the first sign-in is recorded.
  final DateTime? lastSignInAt;
}

/// One of your sign-ins that's still going (GET /v1/me/sessions).
class MySession {
  const MySession({
    required this.sessionId,
    required this.client,
    required this.signedInAt,
    required this.lastUsedAt,
    this.deviceId,
  });

  factory MySession.fromJson(Map<String, dynamic> j) => MySession(
    sessionId: j['sessionId'] as String,
    client: j['client'] as String,
    deviceId: j['deviceId'] as String?,
    signedInAt: DateTime.parse(j['signedInAt'] as String).toLocal(),
    lastUsedAt: DateTime.parse(j['lastUsedAt'] as String).toLocal(),
  );

  final String sessionId;

  /// "mobile" or "admin-dashboard".
  final String client;

  /// The phone, for mobile sessions. Null for the dashboard.
  final String? deviceId;
  final DateTime signedInAt;

  /// The latest sign-in or token refresh in this session.
  final DateTime lastUsedAt;
}

/// Your own account on the server, through the same /v1/me endpoints the dashboard uses.
/// Every call throws [ApiException] with the server's reason code, [TransportException] when offline,
/// or [NeedsSignIn] when the session is over.
abstract class AccountApi {
  Future<MyProfile> profile();

  /// Throws ApiException 400 MISSING_FULL_NAME or FULL_NAME_TOO_LONG.
  Future<MyProfile> rename(String fullName);

  /// If it works, the server ends every session, including this phone's, so the app has to sign in again.
  /// Throws ApiException 400 INVALID_CREDENTIALS, PASSWORD_TOO_SHORT or PASSWORD_UNCHANGED, or 409 CREDENTIAL_LOCKED.
  Future<void> changePassword(String currentPassword, String newPassword);

  /// Most recently used first.
  Future<List<MySession>> sessions();

  /// Throws ApiException 404 if it had already ended.
  Future<void> endSession(String sessionId);

  /// Ends every session, including this phone's, so the app has to sign in again.
  Future<void> endAllSessions();
}

class HttpAccountApi implements AccountApi {
  HttpAccountApi(this.baseUri, this._auth, {http.Client? client}) : _http = client ?? http.Client();

  final Uri baseUri;
  final AuthSession _auth;
  final http.Client _http;

  Uri _uri(String path) => baseUri.replace(path: '${baseUri.path.replaceAll(RegExp(r'/$'), '')}/$path');

  /// Sends with the access token. On a 401 it refreshes once and tries again, like push and pull do.
  Future<http.Response> _call(String method, String path, [Object? body]) async {
    Future<http.Response> send(String token) async {
      final request = http.Request(method, _uri(path))..headers['Authorization'] = 'Bearer $token';
      if (body != null) {
        request.headers['Content-Type'] = 'application/json';
        request.body = jsonEncode(body);
      }
      try {
        return await http.Response.fromStream(await _http.send(request)).timeout(HttpSyncApi.requestTimeout);
      } on Exception catch (e) {
        throw TransportException(e.toString());
      }
    }

    var r = await send(await _auth.validAccessToken());
    if (r.statusCode == 401) r = await send(await _auth.refresh());
    if (r.statusCode >= 400) throw ApiException(r.statusCode, _code(r));
    return r;
  }

  static String? _code(http.Response r) {
    try {
      return (jsonDecode(r.body) as Map<String, dynamic>)['code'] as String?;
    } catch (_) {
      return null;
    }
  }

  static Map<String, dynamic> _json(http.Response r) => jsonDecode(r.body) as Map<String, dynamic>;

  @override
  Future<MyProfile> profile() async => MyProfile.fromJson(_json(await _call('GET', 'v1/me')));

  @override
  Future<MyProfile> rename(String fullName) async =>
      MyProfile.fromJson(_json(await _call('PATCH', 'v1/me', {'fullName': fullName})));

  @override
  Future<void> changePassword(String currentPassword, String newPassword) =>
      _call('POST', 'v1/me/password', {'currentPassword': currentPassword, 'newPassword': newPassword});

  @override
  Future<List<MySession>> sessions() async {
    final r = await _call('GET', 'v1/me/sessions');
    return (jsonDecode(r.body) as List).map((e) => MySession.fromJson(e as Map<String, dynamic>)).toList();
  }

  @override
  Future<void> endSession(String sessionId) => _call('DELETE', 'v1/me/sessions/${Uri.encodeComponent(sessionId)}');

  @override
  Future<void> endAllSessions() => _call('DELETE', 'v1/me/sessions');
}

/// What a clinician should read for each reason code the account endpoints send.
String accountMessage(Object error) => switch (error) {
  TransportException() => 'Can\'t reach the server. Check the connection and try again.',
  NeedsSignIn() => 'Your session has ended. Sign in again to manage your account.',
  ApiException(code: 'INVALID_CREDENTIALS') => 'Your current password isn\'t right.',
  ApiException(code: 'PASSWORD_TOO_SHORT') => 'The new password must be at least $minPasswordLength characters.',
  ApiException(code: 'PASSWORD_UNCHANGED') => 'Choose a password different from your current one.',
  ApiException(code: 'CREDENTIAL_LOCKED') =>
    'Too many wrong passwords. Your account is locked for 15 minutes, or until an admin unlocks it.',
  ApiException(code: 'MISSING_FULL_NAME') => 'Enter your full name.',
  ApiException(code: 'FULL_NAME_TOO_LONG') => 'Your full name can be at most $maxFullNameLength characters.',
  ApiException(code: 'NOT_FOUND') => 'That has already ended, or your account is no longer active.',
  _ => 'Something went wrong. Please try again.',
};
