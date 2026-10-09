import 'dart:convert';
import 'dart:math';

import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_models.dart';
import 'package:uuid/uuid.dart';

/// A valid Wound Event (contracts/wound-event.schema.json). [woundId] lets tests build history and revisions.
Map<String, dynamic> sampleEvent({
  String deviceId = 'dev-test',
  String facilityId = 'fac-001',
  String? assessmentId,
  String? woundId,
  int revision = 1,
  double areaMm2 = 412.6,
}) =>
    {
      'schemaVersion': '1.0',
      'eventId': const Uuid().v7(),
      'assessmentId': assessmentId ?? const Uuid().v7(),
      'revision': revision,
      'woundId': woundId ?? const Uuid().v7(),
      'patientRef': 'p-7f3a9c',
      'deviceId': deviceId,
      'facilityId': facilityId,
      'capturedAt': '2026-10-03T09:41:12+05:30',
      'analytics': {
        'areaMm2': areaMm2,
        'colourRegions': [
          {'cluster': 1, 'percent': 61.2},
          {'cluster': 2, 'percent': 27.9},
        ],
        'fitzpatrickClass': 'V',
        'pipeline': {'calibration': '2.1.0', 'segmentation': 'yolo11n-seg-0.4'},
      },
      'clinicalAssessment': {'pedalPulses': 'not_recorded', 'protectiveSensation': 'absent'},
    };

/// What a single push should suffer, for scripted or random fault injection (§7.1, §11).
enum PushFault {
  none,

  /// The server stores the batch but the answer never arrives (§11: "response lost after the server accepted").
  dropResponseAfterAccept,

  /// The request never reaches the server.
  unreachable,

  serverBusy503,
  tooManyRequests429,
  expiredToken401,
  tooLarge413,
}

/// In-memory fake gateway that dedups, records changes and pages pulls like the real one.
class FakeGateway implements SyncApi {
  FakeGateway({this.autoRecommend = true, this.maxBatchBeforeSplit = 50, Random? random, this.faultRate = 0,
      this.requireMfa = false, this.recommendationFigures = const []})
      : _random = random ?? Random(1);

  /// When set, login needs the code 123456 (§7.3: MFA_REQUIRED, then INVALID_TOTP for a wrong one).
  final bool requireMfa;

  final bool autoRecommend;

  /// Figure references every fake recommendation cites ({corpusVersion, figureId}), as §10.2's `figures` array.
  final List<Map<String, String>> recommendationFigures;
  final int maxBatchBeforeSplit;
  final double faultRate;
  final Random _random;

  /// Faults applied to the next pushes, in order; then [faultRate] picks random ones.
  final List<PushFault> scripted = [];

  bool reachable = true;

  /// eventId → how many times the server stored it (must never exceed 1).
  final Map<String, int> stored = {};

  /// eventId → the stored event, as the server received it.
  final Map<String, Map<String, dynamic>> storedEvents = {};
  final List<Map<String, dynamic>> _changes = [];
  int _seq = 0;
  int pushes = 0;
  int pulls = 0;
  int refreshes = 0;
  String _validAccess = 'access-0';
  String _validRefresh = 'refresh-0';
  bool sessionRevoked = false;

  /// Thrown once by the next refresh instead of answering, e.g. ApiException(503): an error outside §7.1's 401 rule.
  Object? refreshError;

  /// Thrown once by the next push instead of answering, e.g. the FormatException of a Wi-Fi login page.
  Object? pushError;

  /// Runs at the start of every push (e.g. another run taking the sync lease meanwhile).
  Future<void> Function()? onPush;

  @override
  Future<bool> health() async => reachable;

  @override
  Future<TokenPair> login({required String username, required String password, required String deviceId, String? totp}) async {
    if (!reachable) throw TransportException('unreachable');
    if (password != 'correct-password') throw ApiException(401, 'INVALID_CREDENTIALS');
    if (requireMfa && (totp == null || totp.isEmpty)) throw ApiException(401, 'MFA_REQUIRED');
    if (requireMfa && totp != '123456') throw ApiException(401, 'INVALID_TOTP');
    sessionRevoked = false;
    return _issue();
  }

  @override
  Future<TokenPair> refresh(String refreshToken) async {
    if (!reachable) throw TransportException('unreachable');
    refreshes++;
    if (refreshError case final e?) {
      refreshError = null;
      throw e;
    }
    if (sessionRevoked || refreshToken != _validRefresh) throw ApiException(401, 'INVALID_REFRESH_TOKEN');
    return _issue();
  }

  /// JWT-shaped like the backend's (§7.3 claims); the signature is not checked on the device.
  static String _jwt(Map<String, Object> claims) {
    String part(Object o) => base64Url.encode(utf8.encode(jsonEncode(o))).replaceAll('=', '');
    return '${part({'alg': 'RS256', 'typ': 'JWT'})}.${part(claims)}.${const Uuid().v4()}';
  }

  TokenPair _issue() {
    _validAccess = _jwt({
      'sub': 'c0ffee00-0000-4000-8000-000000000001',
      'device_id': 'dev-test',
      'facility_id': 'fac-001',
      'role': 'nurse',
      'client_id': 'mobile',
    });
    _validRefresh = 'refresh-${const Uuid().v4()}';
    return TokenPair(_validAccess, _validRefresh, const Duration(minutes: 15));
  }

  /// The next request with the current access token is refused, as when it expired.
  void expireAccessToken() => _validAccess = 'expired';

  @override
  Future<void> logout(String refreshToken) async {}

  @override
  Future<PushResponse> push(String accessToken, String deviceId, List<String> eventJson) async {
    pushes++;
    await onPush?.call();
    if (pushError case final e?) {
      pushError = null;
      throw e;
    }
    final fault = scripted.isNotEmpty
        ? scripted.removeAt(0)
        : (_random.nextDouble() < faultRate
            ? PushFault.values[1 + _random.nextInt(PushFault.values.length - 2)] // any fault except 413 by chance
            : PushFault.none);

    if (!reachable || fault == PushFault.unreachable) throw TransportException('connection reset');
    if (fault == PushFault.serverBusy503) return const PushResponse(503, [], retryAfter: Duration(seconds: 10));
    if (fault == PushFault.tooManyRequests429) return const PushResponse(429, [], retryAfter: Duration(seconds: 7));
    if (fault == PushFault.expiredToken401 || accessToken != _validAccess) return const PushResponse(401, []);
    if (fault == PushFault.tooLarge413 || eventJson.length > maxBatchBeforeSplit) {
      return const PushResponse(413, [], code: 'BATCH_TOO_LARGE');
    }

    final results = <PushEventResult>[];
    for (final json in eventJson) {
      final event = jsonDecode(json) as Map<String, dynamic>;
      final id = event['eventId'] as String;
      if (stored.containsKey(id)) {
        results.add(PushEventResult(id, 'DUPLICATE'));
        continue;
      }
      stored[id] = 1;
      storedEvents[id] = event;
      results.add(PushEventResult(id, 'ACCEPTED'));
      _change('PERSISTED', event);
      if (autoRecommend) _change('RECOMMENDATION_READY', event);
    }
    if (fault == PushFault.dropResponseAfterAccept) throw TransportException('response lost');
    return PushResponse(200, results);
  }

  void _change(String type, Map<String, dynamic> event) => _changes.add({
        'seq': ++_seq,
        'type': type,
        'assessmentId': event['assessmentId'],
        'revision': event['revision'],
        if (type == 'RECOMMENDATION_READY') 'mode': 'extractive',
        if (type == 'RECOMMENDATION_READY')
          'recommendation': {
            'mode': 'extractive',
            'caseId': event['assessmentId'],
            'sections': [
              {'heading': 'Fake', 'text': 'Fake advice [S1].', 'citationTags': ['S1']}
            ],
            if (recommendationFigures.isNotEmpty) 'figures': recommendationFigures,
          },
      });

  /// Adds a change as the orchestrator would (ADVICE_DEFERRED, SUPERSEDED, a late RECOMMENDATION_READY).
  void addChange(String type, String assessmentId, int revision) =>
      _change(type, {'assessmentId': assessmentId, 'revision': revision});

  @override
  Future<PullPage> pull(String accessToken, int cursor, {int limit = 200}) async {
    pulls++;
    if (!reachable) throw TransportException('unreachable');
    if (accessToken != _validAccess) throw ApiException(401);
    final after = _changes.where((c) => (c['seq'] as int) > cursor).toList();
    final page = after.take(limit).toList();
    final next = page.isEmpty ? cursor : page.last['seq'] as int;
    return PullPage.fromJson({'changes': page, 'nextCursor': next, 'hasMore': after.length > limit});
  }

  /// Figures the fake serves, by 'corpusVersion/figureId' (like the rag-stub's F1-F3).
  final Map<String, List<int>> figures = {};
  int figureRequests = 0;

  /// The next figure request answers 502 FIGURE_UNAVAILABLE.
  bool figureServiceDown = false;

  @override
  Future<FigureResponse> figure(String accessToken, String corpusVersion, String figureId, {String? etag}) async {
    if (!reachable) throw TransportException('unreachable');
    figureRequests++;
    if (accessToken != _validAccess) throw ApiException(401);
    if (figureServiceDown) throw ApiException(502, 'FIGURE_UNAVAILABLE');
    final bytes = figures['$corpusVersion/$figureId'];
    if (bytes == null) return const FigureResponse(404, [], {});
    return FigureResponse(200, bytes, {
      'content-type': 'image/png',
      'etag': '"$corpusVersion-$figureId"',
      'x-figure-licence': 'CC BY-NC 4.0 (test)',
      'x-figure-attribution': 'Test corpus',
      'x-figure-tier': 'A',
    });
  }

  /// Patient aliases stored by setPatientAlias, by patientRef (the server's clinical.patient.display_alias).
  final Map<String, String> patientAliases = {};

  @override
  Future<void> setPatientAlias(String accessToken, String patientRef, String alias) async {
    if (!reachable) throw TransportException('unreachable');
    if (accessToken != _validAccess) throw ApiException(401);
    if (!RegExp(r'^p-[0-9a-f]{6,32}$').hasMatch(patientRef)) throw ApiException(400, 'INVALID_PATIENT_REF');
    patientAliases[patientRef] = alias;
  }
}
