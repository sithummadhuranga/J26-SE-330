import 'dart:convert';
import 'dart:math';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../api/sync_api.dart';
import '../api/sync_models.dart';

/// Where secrets live: Android Keystore on the phone, [MemorySecretStore] in tests.
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

/// The SQLCipher key: 32 random bytes made once per install and kept only in the keystore (§12).
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

/// No usable session, so the clinician must sign in; the offline queue is untouched.
class NeedsSignIn implements Exception {
  NeedsSignIn(this.reason);

  final String reason;

  @override
  String toString() => 'NeedsSignIn: $reason';
}

/// Who is signed in, from the access token's claims (§7.3: sub, device_id, facility_id, role).
class Clinician {
  const Clinician({required this.id, required this.username, required this.role, required this.facilityId});

  final String id;
  final String username;
  final String role;
  final String facilityId;
}

/// Device login session: access token in memory, refresh token in the keystore, never in the database.
class AuthSession {
  AuthSession(this._api, this._secrets, {DateTime Function()? clock}) : _now = clock ?? DateTime.now;

  static const _refreshKey = 'refresh_token_v1';
  static const _usernameKey = 'username_v1';

  /// Refresh a little before expiry so a request never leaves with a token about to lapse.
  static const refreshMargin = Duration(seconds: 60);

  final SyncApi _api;
  final SecretStore _secrets;
  final DateTime Function() _now;

  String? _accessToken;
  DateTime? _expiresAt;
  Clinician? _clinician;

  Clinician? get clinician => _clinician;

  Future<bool> hasSession() async => await _secrets.read(_refreshKey) != null;

  /// Throws [ApiException] with the backend's reason code on failure (e.g. 401 MFA_REQUIRED: ask for a code).
  Future<Clinician> signIn({required String username, required String password, required String deviceId, String? totp}) async {
    final tokens = await _api.login(username: username, password: password, deviceId: deviceId, totp: totp);
    await _secrets.write(_usernameKey, username);
    return await _store(tokens, username);
  }

  /// Returns a token valid for at least [refreshMargin], refreshing if needed; throws [NeedsSignIn] otherwise.
  Future<String> validAccessToken() async {
    final token = _accessToken;
    if (token != null && _expiresAt!.isAfter(_now().add(refreshMargin))) return token;
    return refresh();
  }

  /// Rotates both tokens (§7.3). Called first in every sync run and after any 401.
  Future<String> refresh() async {
    final refreshToken = await _secrets.read(_refreshKey);
    if (refreshToken == null) throw NeedsSignIn('NOT_SIGNED_IN');
    try {
      final tokens = await _api.refresh(refreshToken);
      await _store(tokens, await _secrets.read(_usernameKey) ?? '');
      return tokens.accessToken;
    } on ApiException catch (e) {
      if (e.status == 401) {
        // Expired, revoked (logout elsewhere, deactivated) or already rotated: only a new sign-in helps.
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
        // Offline: the local session ends anyway; the server-side session expires on its own.
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

  /// Save the rotated refresh token before using the new access token, since the old one is already revoked.
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

  /// Reads the JWT payload for display; the signature is the backend's job to check, never the app's.
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
