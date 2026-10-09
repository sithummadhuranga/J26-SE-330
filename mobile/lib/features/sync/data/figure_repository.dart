import 'dart:convert';

import 'package:drift/drift.dart';

import '../api/sync_api.dart';
import '../auth/auth_session.dart';
import 'app_database.dart';

/// Why a figure could not be shown.
enum FigureMiss {
  /// Not cached and the gateway is unreachable. Shown once the phone is back online.
  offline,

  /// The service has no such figure (404). Not retried in this app session.
  notFound,

  /// Not cached and nobody is signed in; figures need a device token like pull.
  needsSignIn,

  /// The gateway answered with an error (e.g. 502 FIGURE_UNAVAILABLE); try again later.
  unavailable,
}

/// The answer to [FigureRepository.figureFor]: the cached figure, or why there is none.
class FigureLookup {
  const FigureLookup.found(FiguresLocalData this.figure) : miss = null;
  const FigureLookup.missing(FigureMiss this.miss) : figure = null;

  final FiguresLocalData? figure;
  final FigureMiss? miss;

  bool get isFound => figure != null;
}

/// Cached guideline figures (with license and attribution); prefetched after each pull so they work offline.
class FigureRepository {
  FigureRepository(this._db, this._api, this._auth, {DateTime Function()? clock}) : _now = clock ?? DateTime.now;

  final AppDatabase _db;
  final SyncApi _api;
  final AuthSession _auth;
  final DateTime Function() _now;

  /// Figures the service answered 404 for, so they are not asked for again on every sync in this app session.
  final Set<String> _notFound = {};

  static String _key(String corpusVersion, String figureId) => '$corpusVersion/$figureId';

  /// The cached figure, or null. Never touches the network.
  Future<FiguresLocalData?> cached(String corpusVersion, String figureId) => (_db.select(_db.figuresLocal)
        ..where((f) => f.corpusVersion.equals(corpusVersion) & f.figureId.equals(figureId)))
      .getSingleOrNull();

  /// The figure to show: from the cache, or fetched (and cached) when it is not there yet.
  Future<FigureLookup> figureFor(String corpusVersion, String figureId) async {
    final hit = await cached(corpusVersion, figureId);
    if (hit != null) return FigureLookup.found(hit);
    if (_notFound.contains(_key(corpusVersion, figureId))) return const FigureLookup.missing(FigureMiss.notFound);

    final String token;
    try {
      token = await _auth.validAccessToken();
    } on NeedsSignIn {
      return const FigureLookup.missing(FigureMiss.needsSignIn);
    } on TransportException {
      return const FigureLookup.missing(FigureMiss.offline);
    } on ApiException {
      return const FigureLookup.missing(FigureMiss.unavailable);
    }
    return _fetch(corpusVersion, figureId, token, mayRefresh: true);
  }

  /// Figures cited by stored advice that are not cached yet, oldest advice first.
  Future<List<({String corpusVersion, String figureId})>> missingReferences() async {
    final cachedKeys = {
      for (final f in await _db.select(_db.figuresLocal).get()) _key(f.corpusVersion, f.figureId),
    };
    final rows = await (_db.select(_db.recommendationLocal)..orderBy([(r) => OrderingTerm.asc(r.receivedAt)])).get();
    final missing = <({String corpusVersion, String figureId})>[];
    final seen = <String>{};
    for (final row in rows) {
      for (final ref in figureReferences(row.payloadJson)) {
        final key = _key(ref.corpusVersion, ref.figureId);
        if (cachedKeys.contains(key) || _notFound.contains(key) || !seen.add(key)) continue;
        missing.add(ref);
      }
    }
    return missing;
  }

  /// Caches up to [limit] missing figures; best effort, returns how many were cached.
  Future<int> prefetchMissing(String accessToken, {int limit = 20}) async {
    var cachedCount = 0;
    for (final ref in (await missingReferences()).take(limit)) {
      final result = await _fetch(ref.corpusVersion, ref.figureId, accessToken, mayRefresh: false);
      if (result.isFound) {
        cachedCount++;
      } else if (result.miss != FigureMiss.notFound) {
        break; // offline, signed out or the service is failing: no point asking for the rest now
      }
    }
    return cachedCount;
  }

  /// The `figures` array of a recommendation (contracts/rag-response.schema.json); malformed entries are skipped.
  static List<({String corpusVersion, String figureId})> figureReferences(String payloadJson) {
    final Object? payload;
    try {
      payload = jsonDecode(payloadJson);
    } on FormatException {
      return const [];
    }
    final figures = payload is Map<String, dynamic> ? payload['figures'] : null;
    if (figures is! List) return const [];
    return [
      for (final f in figures)
        if (f is Map<String, dynamic> && f['corpusVersion'] is String && f['figureId'] is String)
          (corpusVersion: f['corpusVersion'] as String, figureId: f['figureId'] as String),
    ];
  }

  Future<FigureLookup> _fetch(String corpusVersion, String figureId, String token, {required bool mayRefresh}) async {
    final FigureResponse response;
    try {
      response = await _api.figure(token, corpusVersion, figureId);
    } on TransportException {
      return const FigureLookup.missing(FigureMiss.offline);
    } on ApiException catch (e) {
      if (e.status == 401 && mayRefresh) {
        // The access token lapsed between check and use: refresh once, as push and pull do (§7.1).
        try {
          return await _fetch(corpusVersion, figureId, await _auth.refresh(), mayRefresh: false);
        } on NeedsSignIn {
          return const FigureLookup.missing(FigureMiss.needsSignIn);
        } on TransportException {
          return const FigureLookup.missing(FigureMiss.offline);
        } on ApiException {
          return const FigureLookup.missing(FigureMiss.unavailable);
        }
      }
      return FigureLookup.missing(e.status == 401 ? FigureMiss.needsSignIn : FigureMiss.unavailable);
    }

    switch (response.status) {
      case 200:
        final h = response.headers; // the http package lower-cases header names
        await _db.into(_db.figuresLocal).insertOnConflictUpdate(FiguresLocalCompanion.insert(
              corpusVersion: corpusVersion,
              figureId: figureId,
              bytes: Uint8List.fromList(response.bytes),
              contentType: h['content-type'] ?? 'application/octet-stream',
              licence: h['x-figure-licence'] ?? '',
              attribution: h['x-figure-attribution'] ?? '',
              etag: Value(h['etag']),
              cachedAt: _now(),
            ));
        return FigureLookup.found((await cached(corpusVersion, figureId))!);
      case 404:
        _notFound.add(_key(corpusVersion, figureId));
        return const FigureLookup.missing(FigureMiss.notFound);
      default:
        // 304 cannot happen (nothing is cached, so no If-None-Match was sent); treat anything else as transient.
        return const FigureLookup.missing(FigureMiss.unavailable);
    }
  }
}
