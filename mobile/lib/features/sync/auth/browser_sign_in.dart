import 'package:flutter/services.dart';
import 'package:flutter_appauth/flutter_appauth.dart';

import '../api/sync_api.dart';
import '../api/sync_models.dart';

/// The clinician closed the sign-in page without signing in, so nothing changed.
class SignInCancelled implements Exception {
  @override
  String toString() => 'SignInCancelled';
}

/// Signs in on the identity service's own page (ADR 0006), so the app never sees the password or the MFA code.
abstract class BrowserSignIn {
  /// Opens the login page for this phone and returns the tokens. Throws [SignInCancelled] if the clinician
  /// closes it, or [TransportException] if the server can't be reached.
  Future<TokenPair> signIn({required String deviceId});
}

/// Uses AppAuth to run the authorization code flow with PKCE in the system browser (RFC 8252).
class AppAuthSignIn implements BrowserSignIn {
  AppAuthSignIn(this.baseUri, {FlutterAppAuth? appAuth}) : _appAuth = appAuth ?? const FlutterAppAuth();

  static const clientId = 'mobile';

  /// This has to match what's registered with the identity service and in android/app/build.gradle.kts
  /// (appAuthRedirectScheme).
  static const redirectUrl = 'lk.sliit.cdss:/oauth2redirect';

  final Uri baseUri;
  final FlutterAppAuth _appAuth;

  String _endpoint(String path) => baseUri.replace(path: '${baseUri.path.replaceAll(RegExp(r'/$'), '')}/$path').toString();

  @override
  Future<TokenPair> signIn({required String deviceId}) async {
    try {
      final r = await _appAuth.authorizeAndExchangeCode(AuthorizationTokenRequest(
        clientId,
        redirectUrl,
        serviceConfiguration: AuthorizationServiceConfiguration(
          authorizationEndpoint: _endpoint('connect/authorize'),
          tokenEndpoint: _endpoint('connect/token'),
        ),
        scopes: const ['openid', 'profile', 'offline_access'],
        additionalParameters: {'device_id': deviceId},
        // Plain HTTP is only allowed for the local stack (10.0.2.2). A real deployment uses HTTPS.
        allowInsecureConnections: baseUri.scheme == 'http',
      ));
      final access = r.accessToken;
      final refresh = r.refreshToken;
      if (access == null || refresh == null) throw ApiException(502, 'INCOMPLETE_TOKEN_RESPONSE');
      final expiresAt = r.accessTokenExpirationDateTime;
      return TokenPair(access, refresh,
          expiresAt == null ? const Duration(minutes: 15) : expiresAt.difference(DateTime.now()),
          idToken: r.idToken);
    } on FlutterAppAuthUserCancelledException {
      throw SignInCancelled();
    } on PlatformException catch (e) {
      // AppAuth reports network failures as platform errors. Either way, the queue isn't touched.
      throw TransportException(e.message ?? e.code);
    }
  }
}
