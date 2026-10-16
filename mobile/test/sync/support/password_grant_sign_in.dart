import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_models.dart';
import 'package:melanin_wound_cdss/features/sync/auth/browser_sign_in.dart';

/// Signs in against the real backend without a browser, using the password grant the local stack enables for tests
/// (Oidc:AllowPasswordGrant). The app itself always goes through the login page; the tokens are the same.
class PasswordGrantSignIn implements BrowserSignIn {
  PasswordGrantSignIn(this.baseUri, this.username, this.password);

  final Uri baseUri;
  final String username;
  final String password;

  @override
  Future<TokenPair> signIn({required String deviceId}) async {
    final r = await http.post(baseUri.resolve('connect/token'), body: {
      'grant_type': 'password',
      'client_id': 'mobile',
      'username': username,
      'password': password,
      'device_id': deviceId,
      'scope': 'openid profile offline_access',
    });
    final body = jsonDecode(r.body) as Map<String, dynamic>;
    if (r.statusCode != 200) throw ApiException(r.statusCode, body['error_description'] as String?);
    return TokenPair.fromJson(body);
  }
}
