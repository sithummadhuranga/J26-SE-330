import 'dart:convert';

import 'package:http/http.dart' as http;

import '../sync/api/sync_api.dart';

/// DRAFT (not agreed with Member 4): the follow-up chat through the sync gateway, keyed by assessment and revision.
/// Bodies are the Recommendation Service's draft rag-chat schema (session and turn), passed through unchanged.
abstract class ChatApi {
  /// The chat session on this advice, started if needed. Throws [ApiException] or [TransportException].
  Future<Map<String, dynamic>> open(String accessToken, String assessmentId, int revision);

  /// Asks one question; returns the stored turn. Throws [ApiException] or [TransportException].
  Future<Map<String, dynamic>> ask(String accessToken, String assessmentId, int revision, String question, String language);
}

class HttpChatApi implements ChatApi {
  HttpChatApi(this.baseUri, {http.Client? client}) : _http = client ?? http.Client();

  final Uri baseUri;
  final http.Client _http;

  static const openTimeout = Duration(seconds: 30);

  /// A turn may take several checked model calls; a little above the gateway's own 150 s.
  static const askTimeout = Duration(seconds: 160);

  Uri _uri(String assessmentId, int revision, [String suffix = '']) => baseUri.replace(
      path: '${baseUri.path.replaceAll(RegExp(r'/$'), '')}/v1/assessments/${Uri.encodeComponent(assessmentId)}/revisions/$revision/chat$suffix');

  @override
  Future<Map<String, dynamic>> open(String accessToken, String assessmentId, int revision) =>
      _post(_uri(assessmentId, revision), accessToken, null, openTimeout);

  @override
  Future<Map<String, dynamic>> ask(String accessToken, String assessmentId, int revision, String question, String language) =>
      _post(_uri(assessmentId, revision, '/turns'), accessToken, {'question': question, 'language': language}, askTimeout);

  Future<Map<String, dynamic>> _post(Uri uri, String accessToken, Map<String, Object>? body, Duration timeout) async {
    final http.Response r;
    try {
      r = await _http
          .post(uri,
              headers: {'Authorization': 'Bearer $accessToken', 'Content-Type': 'application/json'},
              body: body == null ? null : jsonEncode(body))
          .timeout(timeout);
    } on Exception catch (e) {
      throw TransportException(e.toString());
    }
    if (r.statusCode == 200 || r.statusCode == 201) {
      try {
        return jsonDecode(utf8.decode(r.bodyBytes)) as Map<String, dynamic>;
      } on FormatException {
        throw ApiException(502, 'NOT_JSON'); // e.g. a Wi-Fi login page answered instead of the gateway
      }
    }
    throw ApiException(r.statusCode, _code(r));
  }

  // The gateway names its own errors "code", the service "error".
  static String? _code(http.Response r) {
    try {
      final body = jsonDecode(r.body) as Map<String, dynamic>;
      return (body['code'] ?? body['error']) as String?;
    } catch (_) {
      return null;
    }
  }
}
