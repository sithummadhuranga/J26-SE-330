import 'dart:convert';
import 'dart:io' show gzip;
import 'dart:isolate';

import 'package:http/http.dart' as http;
import 'package:uuid/uuid.dart';

import 'sync_models.dart';

/// We got no HTTP response at all. It's safe to retry, because sending an event twice does no harm.
class TransportException implements Exception {
  TransportException(this.message);

  final String message;

  @override
  String toString() => 'TransportException: $message';
}

/// The server answered with an HTTP error, along with the reason code the backend sends
/// (authError in contracts/auth.schema.json).
class ApiException implements Exception {
  ApiException(this.status, [this.code, this.retryAfter]);

  final int status;
  final String? code;
  final Duration? retryAfter;

  @override
  String toString() => 'ApiException($status${code == null ? '' : ' $code'})';
}

/// The result of one push. Status codes other than 200 are returned rather than thrown,
/// so the engine can decide what to do.
class PushResponse {
  const PushResponse(this.status, this.results, {this.retryAfter, this.code});

  final int status;
  final List<PushEventResult> results;
  final Duration? retryAfter;
  final String? code;
}

class FigureResponse {
  const FigureResponse(this.status, this.bytes, this.headers);

  final int status; // 200, 304 or 404
  final List<int> bytes;
  final Map<String, String> headers;
}

/// The phone's side of the sync protocol (§7). Everything goes through the API gateway (ADR 0004).
abstract class SyncApi {
  Future<bool> health();
  /// Swaps both tokens for new ones through /connect/token. Once the session is over,
  /// this throws [ApiException] 400 invalid_grant.
  Future<TokenPair> refresh(String refreshToken);

  /// Ends the session on the server through /connect/revocation. Signing in is handled by [BrowserSignIn].
  Future<void> logout(String refreshToken);
  Future<PushResponse> push(String accessToken, String deviceId, List<String> eventJson);
  Future<PullPage> pull(String accessToken, int cursor, {int limit});
  Future<FigureResponse> figure(String accessToken, String corpusVersion, String figureId, {String? etag});

  /// Saves a patient's display alias on the server. Throws [ApiException] or [TransportException].
  Future<void> setPatientAlias(String accessToken, String patientRef, String alias);
}

class HttpSyncApi implements SyncApi {
  HttpSyncApi(this.baseUri, {http.Client? client}) : _http = client ?? http.Client();

  final Uri baseUri;
  final http.Client _http;

  static const healthTimeout = Duration(seconds: 3); // §6.2: probe with a short timeout
  static const requestTimeout = Duration(seconds: 30);

  /// Bodies bigger than this are gzipped on a separate isolate, so the UI never has to do that work (§6.2).
  static const isolateGzipThreshold = 32 * 1024;

  /// Pull pages bigger than this are decoded away from the UI isolate, so the app doesn't drop frames.
  static const isolateDecodeThreshold = 32 * 1024;

  Uri _uri(String path, [Map<String, String>? query]) =>
      baseUri.replace(path: '${baseUri.path.replaceAll(RegExp(r'/$'), '')}/$path', queryParameters: query);

  Future<http.Response> _send(Future<http.Response> request, Duration timeout) async {
    try {
      return await request.timeout(timeout);
    } on Exception catch (e) {
      throw TransportException(e.toString());
    }
  }

  /// We only count the gateway as reachable if it answers {"status":"ok"}. A 200 from a Wi-Fi login page doesn't count.
  @override
  Future<bool> health() async {
    try {
      final r = await _send(_http.get(_uri('health')), healthTimeout);
      if (r.statusCode != 200) return false;
      final body = jsonDecode(r.body);
      return body is Map && body['status'] == 'ok';
    } on TransportException {
      return false;
    } on FormatException {
      return false;
    }
  }

  /// The OAuth endpoints take form fields, not JSON.
  Future<http.Response> _form(String path, Map<String, String> fields) => _send(
      _http.post(_uri(path),
          headers: {'Content-Type': 'application/x-www-form-urlencoded'}, body: {'client_id': 'mobile', ...fields}),
      requestTimeout);

  @override
  Future<TokenPair> refresh(String refreshToken) async {
    final r = await _form('connect/token', {'grant_type': 'refresh_token', 'refresh_token': refreshToken});
    if (r.statusCode == 200) return TokenPair.fromJson(jsonDecode(r.body) as Map<String, dynamic>);
    throw ApiException(r.statusCode, _code(r), _retryAfter(r));
  }

  @override
  Future<void> logout(String refreshToken) async {
    await _form('connect/revocation', {'token': refreshToken, 'token_type_hint': 'refresh_token'});
  }

  @override
  Future<PushResponse> push(String accessToken, String deviceId, List<String> eventJson) async {
    // The queue already stores each event as JSON, so we drop them straight in instead of decoding and re-encoding.
    final body = '{"deviceId":${jsonEncode(deviceId)},"batchId":"${const Uuid().v4()}",'
        '"events":[${eventJson.join(',')}]}';
    final raw = utf8.encode(body);
    final compressed = raw.length > isolateGzipThreshold ? await Isolate.run(() => gzip.encode(raw)) : gzip.encode(raw);

    final r = await _send(
      _http.post(_uri('v1/sync/push'),
          headers: {
            'Authorization': 'Bearer $accessToken',
            'Content-Type': 'application/json',
            'Content-Encoding': 'gzip',
          },
          body: compressed),
      requestTimeout,
    );
    if (r.statusCode != 200) return PushResponse(r.statusCode, const [], retryAfter: _retryAfter(r), code: _code(r));
    final results = ((jsonDecode(r.body) as Map<String, dynamic>)['results'] as List)
        .map((e) => PushEventResult.fromJson(e as Map<String, dynamic>))
        .toList();
    return PushResponse(200, results);
  }

  @override
  Future<PullPage> pull(String accessToken, int cursor, {int limit = 200}) async {
    final r = await _send(
        _http.get(_uri('v1/sync/changes', {'cursor': '$cursor', 'limit': '$limit'}),
            headers: {'Authorization': 'Bearer $accessToken'}),
        requestTimeout);
    if (r.statusCode != 200) throw ApiException(r.statusCode, _code(r), _retryAfter(r));
    final bytes = r.bodyBytes;
    return bytes.length > isolateDecodeThreshold ? Isolate.run(() => _pullPage(bytes)) : _pullPage(bytes);
  }

  static PullPage _pullPage(List<int> bytes) =>
      PullPage.fromJson(jsonDecode(utf8.decode(bytes)) as Map<String, dynamic>);

  @override
  Future<FigureResponse> figure(String accessToken, String corpusVersion, String figureId, {String? etag}) async {
    final r = await _send(
        _http.get(
            _uri('v1/figures/${Uri.encodeComponent(corpusVersion)}/${Uri.encodeComponent(figureId)}'),
            headers: {'Authorization': 'Bearer $accessToken', 'If-None-Match': ?etag}),
        requestTimeout);
    if (r.statusCode == 200 || r.statusCode == 304 || r.statusCode == 404) {
      return FigureResponse(r.statusCode, r.bodyBytes, r.headers);
    }
    throw ApiException(r.statusCode, _code(r), _retryAfter(r));
  }

  @override
  Future<void> setPatientAlias(String accessToken, String patientRef, String alias) async {
    final r = await _send(
        _http.put(_uri('v1/patients/${Uri.encodeComponent(patientRef)}/alias'),
            headers: {'Authorization': 'Bearer $accessToken', 'Content-Type': 'application/json'},
            body: jsonEncode({'displayAlias': alias})),
        requestTimeout);
    if (r.statusCode != 200) throw ApiException(r.statusCode, _code(r), _retryAfter(r));
  }

  /// Our own API puts the reason in {"code"}, while the OAuth endpoints use {"error"}.
  static String? _code(http.Response r) {
    try {
      final body = jsonDecode(r.body) as Map<String, dynamic>;
      return (body['code'] ?? body['error']) as String?;
    } catch (_) {
      return null;
    }
  }

  static Duration? _retryAfter(http.Response r) {
    final seconds = int.tryParse(r.headers['retry-after'] ?? '');
    return seconds == null ? null : Duration(seconds: seconds);
  }
}
