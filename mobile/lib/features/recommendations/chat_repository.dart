import 'dart:convert';

import 'package:drift/drift.dart';

import '../sync/api/sync_api.dart';
import '../sync/auth/auth_session.dart';
import '../sync/data/app_database.dart';
import 'chat_api.dart';

/// Why the latest chat call did not go through; the saved conversation is still shown.
enum ChatProblem {
  /// No connection: questions need one; the saved history is readable.
  offline,

  /// Nobody signed in, or the session ended.
  needsSignIn,

  /// No delivered advice to chat about (404).
  notAvailable,

  /// The question was refused as invalid (422).
  rejected,

  /// Another question was answered at the same moment (409); reloading shows it.
  conflict,

  /// The gateway or the service failed; try again later.
  unavailable,
}

/// The conversation as last saved, and why the latest call failed, if it did.
class ChatState {
  const ChatState(this.session, [this.problem]);

  final Map<String, dynamic>? session;
  final ChatProblem? problem;

  List<Map<String, dynamic>> get turns =>
      ((session?['turns'] as List?) ?? const []).whereType<Map<String, dynamic>>().toList();
}

/// The follow-up chat on one advice: the gateway when online, the saved session (chat_local) always. The session is
/// saved after every call, so what was shown stays readable offline.
class ChatRepository {
  ChatRepository(this._db, this._api, this._auth, {DateTime Function()? clock}) : _now = clock ?? DateTime.now;

  final AppDatabase _db;
  final ChatApi _api;
  final AuthSession _auth;
  final DateTime Function() _now;

  Future<Map<String, dynamic>?> cached(String assessmentId, int revision) async {
    final row = await (_db.select(_db.chatLocal)
          ..where((c) => c.assessmentId.equals(assessmentId) & c.revision.equals(revision)))
        .getSingleOrNull();
    return row == null ? null : jsonDecode(row.sessionJson) as Map<String, dynamic>;
  }

  /// Starts or reloads the session; offline, the saved one.
  Future<ChatState> open(String assessmentId, int revision) async {
    final saved = await cached(assessmentId, revision);
    final result = await _call((token) => _api.open(token, assessmentId, revision));
    if (result.problem != null) return ChatState(saved, result.problem);
    await _save(assessmentId, revision, result.body!);
    return ChatState(result.body);
  }

  /// Asks one question and adds the stored turn to the saved session.
  Future<ChatState> ask(String assessmentId, int revision, String question, {String language = 'auto'}) async {
    var saved = await cached(assessmentId, revision);
    final result = await _call((token) => _api.ask(token, assessmentId, revision, question, language));
    if (result.problem != null) return ChatState(saved, result.problem);
    saved ??= {'turns': <Object>[]};
    final updated = {
      ...saved,
      'turns': [...((saved['turns'] as List?) ?? const []), result.body],
    };
    await _save(assessmentId, revision, updated);
    return ChatState(updated);
  }

  // One refresh after a 401, as figures, push and pull do (§7.1).
  Future<({Map<String, dynamic>? body, ChatProblem? problem})> _call(
      Future<Map<String, dynamic>> Function(String token) send) async {
    try {
      try {
        return (body: await send(await _auth.validAccessToken()), problem: null);
      } on ApiException catch (e) {
        if (e.status != 401) rethrow;
        return (body: await send(await _auth.refresh()), problem: null);
      }
    } on NeedsSignIn {
      return (body: null, problem: ChatProblem.needsSignIn);
    } on TransportException {
      return (body: null, problem: ChatProblem.offline);
    } on ApiException catch (e) {
      final problem = switch (e.status) {
        401 => ChatProblem.needsSignIn,
        404 => ChatProblem.notAvailable,
        409 => ChatProblem.conflict,
        422 => ChatProblem.rejected,
        _ => ChatProblem.unavailable,
      };
      return (body: null, problem: problem);
    }
  }

  Future<void> _save(String assessmentId, int revision, Map<String, dynamic> session) =>
      _db.into(_db.chatLocal).insertOnConflictUpdate(ChatLocalCompanion.insert(
            assessmentId: assessmentId,
            revision: revision,
            sessionJson: jsonEncode(session),
            updatedAt: _now(),
          ));
}
