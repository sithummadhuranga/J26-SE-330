/// Sync protocol wire types, mirroring the auth and sync-changes JSON schemas.
library;

import 'dart:convert';

/// §7.1: one result per pushed event. DUPLICATE is treated exactly like ACCEPTED.
class PushEventResult {
  const PushEventResult(this.eventId, this.status, [this.code]);

  final String eventId;
  final String status; // ACCEPTED | DUPLICATE | REJECTED
  final String? code;

  factory PushEventResult.fromJson(Map<String, dynamic> j) =>
      PushEventResult(j['eventId'] as String, j['status'] as String, j['code'] as String?);
}

/// One change after the cursor; upserted by (assessmentId, revision, type) so repeats are harmless.
class SyncChange {
  const SyncChange({
    required this.seq,
    required this.type,
    required this.assessmentId,
    required this.revision,
    this.mode,
    this.recommendationJson,
  });

  final int seq;
  final String type; // PERSISTED | RECOMMENDATION_READY | ADVICE_DEFERRED | SUPERSEDED
  final String assessmentId;
  final int revision;
  final String? mode;

  /// The advice (RECOMMENDATION_READY) as JSON text, ready to store: encoded once, with the page, off the UI isolate.
  final String? recommendationJson;

  factory SyncChange.fromJson(Map<String, dynamic> j) {
    final recommendation = j['recommendation'] as Map<String, dynamic>?;
    return SyncChange(
      seq: j['seq'] as int,
      type: j['type'] as String,
      assessmentId: j['assessmentId'] as String,
      revision: j['revision'] as int,
      mode: j['mode'] as String? ?? recommendation?['mode'] as String?,
      recommendationJson: recommendation == null ? null : jsonEncode(recommendation),
    );
  }
}

class PullPage {
  const PullPage(this.changes, this.nextCursor, this.hasMore);

  final List<SyncChange> changes;
  final int nextCursor;
  final bool hasMore;

  factory PullPage.fromJson(Map<String, dynamic> j) => PullPage(
        (j['changes'] as List).map((c) => SyncChange.fromJson(c as Map<String, dynamic>)).toList(),
        j['nextCursor'] as int,
        j['hasMore'] as bool,
      );
}

/// §7.3: a 15-minute access JWT and a rotating opaque refresh token.
class TokenPair {
  const TokenPair(this.accessToken, this.refreshToken, this.expiresIn);

  final String accessToken;
  final String refreshToken;
  final Duration expiresIn;

  factory TokenPair.fromJson(Map<String, dynamic> j) =>
      TokenPair(j['accessToken'] as String, j['refreshToken'] as String, Duration(seconds: j['expiresIn'] as int));
}
