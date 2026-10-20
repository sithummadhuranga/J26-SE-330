import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../api/sync_api.dart';
import '../api/sync_models.dart';
import 'browser_sign_in.dart';

/// Where secrets are kept: the Android Keystore on a phone, or [MemorySecretStore] in tests.
abstract class SecretStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

class KeystoreSecretStore implements SecretStore {
  const KeystoreSecretStore([this._storage = const FlutterSecureStorage()]);

  final FlutterSecureStorage _storage;

  @override
  Future<String?> read(String key) => _storage.read(key: key);

  @override
  Future<void> write(String key, String value) => _storage.write(key: key, value: value);

  @override
  Future<void> delete(String key) => _storage.delete(key: key);
}

class MemorySecretStore implements SecretStore {
  final Map<String, String> values = {};

  @override
  Future<String?> read(String key) async => values[key];

  @override
  Future<void> write(String key, String value) async => values[key] = value;

  @override
  Future<void> delete(String key) async => values.remove(key);
}

/// The SQLCipher database key. It's 32 random bytes, made once per install and only ever kept in the keystore (§12).
abstract final class DatabaseKeyStore {
  static const _name = 'db_key_v1';

  static Future<String> getOrCreate(SecretStore secrets, {Random? random}) async {
    final existing = await secrets.read(_name);
    if (existing != null) return existing;
    final rng = random ?? Random.secure();
    final key = List.generate(32, (_) => rng.nextInt(256)).map((b) => b.toRadixString(16).padLeft(2, '0')).join();
    await secrets.write(_name, key);
    return key;
  }
}

/// There's no usable session, so the clinician has to sign in. The offline queue isn't touched.
class NeedsSignIn implements Exception {
  NeedsSignIn(this.reason);

  final String reason;

  @override
  String toString() => 'NeedsSignIn: $reason';
}

/// Who is signed in, read from the access token's claims: sub, device_id, facility_id and role (§7.3).
class Clinician {
  const Clinician({required this.id, required this.username, required this.role, required this.facilityId});

  final String id;
  final String username;
  final String role;
  final String facilityId;
}

/// The phone's login session. The access token lives in memory and the refresh token in the keystore,
/// never in the database. Signing in happens on the identity service's own page ([BrowserSignIn]),
/// while refreshing and signing out go through [SyncApi].
class AuthSession {
  AuthSession(this._api, this._secrets, {BrowserSignIn? browser, DateTime Function()? clock})
      : _browser = browser,
        _now = clock ?? DateTime.now;

  static const _refreshKey = 'refresh_token_v1';
  static const _usernameKey = 'username_v1';

  /// We refresh a little early so a request never goes out with a token that's about to expire.
  static const refreshMargin = Duration(seconds: 60);

  final SyncApi _api;
  final SecretStore _secrets;
  final BrowserSignIn? _browser;
  final DateTime Function() _now;

  String? _accessToken;
  DateTime? _expiresAt;
  Clinician? _clinician;

  Clinician? get clinician => _clinician;

  Future<bool> hasSession() async => await _secrets.read(_refreshKey) != null;

  /// Opens the identity service's sign-in page for this phone. The page handles wrong passwords, codes and
  /// locked accounts itself. Throws [SignInCancelled] if the clinician closes it, or [TransportException] when offline.
  Future<Clinician> signIn({required String deviceId}) async {
    final browser = _browser;
    if (browser == null) throw StateError('AuthSession has no BrowserSignIn');
    return signedIn(await browser.signIn(deviceId: deviceId));
  }

  /// Saves the tokens from a sign-in. Tests also use this to sign in without a browser.
  Future<Clinician> signedIn(TokenPair tokens) async {
    final username = _claims(tokens.idToken ?? '')['preferred_username'] as String? ?? '';
    await _secrets.write(_usernameKey, username);
    return await _store(tokens, username);
  }

  /// Returns a token that's good for at least [refreshMargin], refreshing it first if needed.
  /// Throws [NeedsSignIn] if that isn't possible.
  Future<String> validAccessToken() async {
    final token = _accessToken;
    if (token != null && _expiresAt!.isAfter(_now().add(refreshMargin))) return token;
    return refresh();
  }

  /// Swaps both tokens for new ones (§7.3). This runs at the start of every sync and after any 401.
  Future<String> refresh() async {
    final refreshToken = await _secrets.read(_refreshKey);
    if (refreshToken == null) throw NeedsSignIn('NOT_SIGNED_IN');
    try {
      final tokens = await _api.refresh(refreshToken);
      await _store(tokens, await _secrets.read(_usernameKey) ?? '');
      return tokens.accessToken;
    } on ApiException catch (e) {
      // Once the session is over, /connect/token answers 400 invalid_grant.
      if (e.status == 401 || e.code == 'invalid_grant') {
        // The token expired, was revoked (signed out elsewhere, account deactivated) or was already used.
        // Only a fresh sign-in can fix that.
        await _clear();
        throw NeedsSignIn(e.code ?? 'INVALID_REFRESH_TOKEN');
      }
      rethrow;
    }
  }

  Future<void> signOut() async {
    final refreshToken = await _secrets.read(_refreshKey);
    if (refreshToken != null) {
      try {
        await _api.logout(refreshToken);
      } on Exception {
        // We're offline. The session on the phone ends anyway, and the server's copy will expire on its own.
      }
    }
    await _clear();
  }

  Future<void> _clear() async {
    _accessToken = null;
    _expiresAt = null;
    _clinician = null;
    await _secrets.delete(_refreshKey);
  }

  /// Save the new refresh token before using the new access token, because the old one no longer works.
  Future<Clinician> _store(TokenPair tokens, String username) async {
    await _secrets.write(_refreshKey, tokens.refreshToken);
    _accessToken = tokens.accessToken;
    _expiresAt = _now().add(tokens.expiresIn);
    final claims = _claims(tokens.accessToken);
    return _clinician = Clinician(
      id: claims['sub'] as String? ?? '',
      username: username,
      role: claims['role'] as String? ?? '',
      facilityId: claims['facility_id'] as String? ?? '',
    );
  }

  /// Reads the JWT payload just for display. Checking the signature is the backend's job, not the app's.
  static Map<String, dynamic> _claims(String jwt) {
    final parts = jwt.split('.');
    if (parts.length != 3) return {};
    try {
      return jsonDecode(utf8.decode(base64Url.decode(base64Url.normalize(parts[1])))) as Map<String, dynamic>;
    } catch (_) {
      return {};
    }
  }
}
