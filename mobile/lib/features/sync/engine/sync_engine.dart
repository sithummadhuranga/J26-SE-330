import 'dart:math';

import 'package:uuid/uuid.dart';

import '../api/sync_api.dart';
import '../auth/auth_session.dart';
import '../data/app_database.dart';
import '../data/figure_repository.dart';
import '../data/patient_repository.dart';
import '../data/queue_repository.dart';
import 'backoff.dart';

enum SyncOutcome {
  /// Pushed what was pending and pulled every change.
  success,

  /// Another run holds the sync (this isolate or another one).
  alreadyRunning,

  /// Still inside a backoff window; not tried. [SyncReport.retryAt] says when.
  backingOff,

  /// The gateway is unreachable (or stopped answering mid-run); backing off.
  offline,

  /// The server asked to slow down (429/503); backing off, honouring Retry-After.
  serverBusy,

  /// No usable session: the app asks the clinician to sign in. The queue is untouched (§7.3).
  needsSignIn,
}

class SyncReport {
  SyncReport(this.outcome,
      {this.pushed = 0, this.accepted = 0, this.rejected = 0, this.changes = 0, this.figures = 0, this.retryAt, this.detail});

  final SyncOutcome outcome;
  final int pushed;
  final int accepted;
  final int rejected;
  final int changes;

  /// Guideline figures cited by received advice and cached in this run (§10.4).
  final int figures;
  final DateTime? retryAt;
  final String? detail;

  @override
  String toString() => 'SyncReport($outcome, pushed $pushed, accepted $accepted, rejected $rejected, changes $changes'
      '${retryAt == null ? '' : ', retry at $retryAt'}${detail == null ? '' : ', $detail'})';
}

/// Why a run stopped early.
class _Stop implements Exception {
  _Stop(this.outcome, [this.retryAfter, this.detail]);

  final SyncOutcome outcome;
  final Duration? retryAfter;
  final String? detail;
}

/// The sync engine: one [sync] call pushes the queue, pulls changes and backs off on failure; never throws.
class SyncEngine {
  SyncEngine({
    required QueueRepository queue,
    required SyncApi api,
    required AuthSession auth,
    FigureRepository? figures,
    PatientRepository? patients,
    DateTime Function()? clock,
    Random? random,
  })  : _queue = queue,
        _api = api,
        _auth = auth,
        _figures = figures,
        _patients = patients,
        _now = clock ?? DateTime.now,
        _backoff = Backoff(random);

  final QueueRepository _queue;
  final SyncApi _api;
  final AuthSession _auth;
  final FigureRepository? _figures;
  final PatientRepository? _patients;
  final DateTime Function() _now;
  final Backoff _backoff;

  /// Longer than any run, so a run killed mid-way frees the lease on its own.
  static const syncLease = Duration(minutes: 3);

  /// Names this engine's runs in the database lease, so a run renews and releases only its own.
  final String _leaseOwner = const Uuid().v4();

  Future<SyncReport>? _running;

  /// Runs one sync or joins the one in progress; [force] skips the backoff wait.
  Future<SyncReport> sync({bool force = false}) =>
      _running ??= _run(force).whenComplete(() => _running = null);

  Future<SyncReport> _run(bool force) async {
    final retryAt = DateTime.tryParse(await _queue.getState(SyncStateKeys.nextAttemptAt) ?? '');
    if (!force && retryAt != null && retryAt.isAfter(_now())) {
      return SyncReport(SyncOutcome.backingOff, retryAt: retryAt);
    }
    if (!await _queue.tryAcquireSyncLease(syncLease, owner: _leaseOwner)) return SyncReport(SyncOutcome.alreadyRunning);

    var pushed = 0, accepted = 0, rejected = 0, changes = 0;
    Future<SyncReport> backOff(SyncOutcome outcome, String detail, [Duration? retryAfter]) async {
      final at = _now().add(_backoff.next(retryAfter: retryAfter));
      await _queue.setState(SyncStateKeys.nextAttemptAt, at.toIso8601String());
      return SyncReport(outcome,
          pushed: pushed, accepted: accepted, rejected: rejected, changes: changes, retryAt: at, detail: detail);
    }

    try {
      await _queue.releaseExpiredLeases();
      var token = await _auth.validAccessToken();
      final deviceId = await _queue.deviceId();

      if (await _queue.hasWorkToSend()) {
        if (!await _api.health()) throw _Stop(SyncOutcome.offline, null, 'gateway unreachable');
        while (true) {
          await _keepLease();
          final batch = await _queue.leaseBatch();
          if (batch.isEmpty) break;
          final result = await _push(batch, token, deviceId);
          token = result.token;
          pushed += result.pushed;
          accepted += result.accepted;
          rejected += result.rejected;
        }
      }

      changes = await _pullAll(token, (t) => token = t);

      _backoff.reset();
      await _queue.setState(SyncStateKeys.nextAttemptAt, '');
      await _queue.setState(SyncStateKeys.lastSuccessAt, _now().toIso8601String());

      // Never affects the outcome: the advice itself is already stored, a figure can follow on the next sync.
      var figures = 0;
      try {
        figures = await _figures?.prefetchMissing(token) ?? 0;
      } catch (_) {}
      // Also best effort: patient labels reach the server's record; unsent ones go on the next sync.
      try {
        await _patients?.pushAliases(token);
      } catch (_) {}
      return SyncReport(SyncOutcome.success,
          pushed: pushed, accepted: accepted, rejected: rejected, changes: changes, figures: figures);
    } on NeedsSignIn catch (e) {
      return SyncReport(SyncOutcome.needsSignIn,
          pushed: pushed, accepted: accepted, rejected: rejected, changes: changes, detail: e.reason);
    } on _Stop catch (stop) {
      if (stop.outcome == SyncOutcome.needsSignIn) {
        return SyncReport(SyncOutcome.needsSignIn,
            pushed: pushed, accepted: accepted, rejected: rejected, changes: changes, detail: stop.detail);
      }
      if (stop.outcome == SyncOutcome.alreadyRunning) {
        return SyncReport(SyncOutcome.alreadyRunning,
            pushed: pushed, accepted: accepted, rejected: rejected, changes: changes, detail: stop.detail);
      }
      return backOff(stop.outcome, stop.detail ?? '', stop.retryAfter);
    } on TransportException catch (e) {
      return backOff(SyncOutcome.offline, e.message);
    } on ApiException catch (e) {
      // An error answer outside the push and pull handling, e.g. the token refresh answered 503 or 500.
      final busy = e.status == 429 || e.status == 503;
      return backOff(busy ? SyncOutcome.serverBusy : SyncOutcome.offline,
          'HTTP ${e.status}${e.code == null ? '' : ' ${e.code}'}', e.retryAfter);
    } catch (e) {
      // A malformed answer (a Wi-Fi login page instead of JSON) or anything unforeseen: back off and try again.
      return backOff(SyncOutcome.offline, 'unexpected: $e');
    } finally {
      await _queue.releaseSyncLease(owner: _leaseOwner);
    }
  }

  /// Renews the database lease before each batch and page. If it lapsed and another run took it, this run stops.
  Future<void> _keepLease() async {
    if (!await _queue.renewSyncLease(syncLease, owner: _leaseOwner)) {
      throw _Stop(SyncOutcome.alreadyRunning, null, 'lease taken over by another run');
    }
  }

  /// Pushes one leased batch. Every path leaves each row accepted, rejected or back to pending.
  Future<({String token, int pushed, int accepted, int rejected})> _push(
      List<QueuedEvent> batch, String token, String deviceId,
      {bool refreshed = false}) async {
    try {
      return await _pushLeased(batch, token, deviceId, refreshed: refreshed);
    } catch (_) {
      // Release any still-leased rows back to pending right away instead of waiting for the lease to lapse.
      await _queue.release([for (final r in batch) r.eventId], 'sync stopped');
      rethrow;
    }
  }

  Future<({String token, int pushed, int accepted, int rejected})> _pushLeased(
      List<QueuedEvent> batch, String token, String deviceId,
      {bool refreshed = false}) async {
    final ids = [for (final r in batch) r.eventId];
    PushResponse response;
    try {
      response = await _api.push(token, deviceId, [for (final r in batch) r.payloadJson]);
    } on TransportException catch (e) {
      // No answer: the server may or may not have the events. Resending is safe (DUPLICATE).
      await _queue.release(ids, 'no response: ${e.message}');
      throw _Stop(SyncOutcome.offline, null, e.message);
    }

    switch (response.status) {
      case 200:
        await _queue.applyPushResults(response.results);
        final answered = {for (final r in response.results) r.eventId};
        final missing = ids.where((id) => !answered.contains(id)).toList();
        if (missing.isNotEmpty) await _queue.release(missing, 'no result in the batch answer');
        return (
          token: token,
          pushed: ids.length,
          accepted: response.results.where((r) => r.status != 'REJECTED').length,
          rejected: response.results.where((r) => r.status == 'REJECTED').length,
        );
      case 401 when !refreshed:
        // §7.1: refresh the token, retry the batch (still leased).
        final fresh = await _auth.refresh();
        return _pushLeased(batch, fresh, deviceId, refreshed: true);
      case 413 when batch.length > 1:
        // §7.1: split the batch. The second half goes back to pending and follows in the next batch.
        final half = batch.length ~/ 2;
        await _queue.release(ids.sublist(half), 'split after 413');
        return _pushLeased(batch.sublist(0, half), token, deviceId, refreshed: refreshed);
      case 413:
        await _queue.reject(ids, 'PAYLOAD_TOO_LARGE');
        return (token: token, pushed: 1, accepted: 0, rejected: 1);
      case 403:
        // DEVICE_MISMATCH: the session belongs to another device id; a new sign-in binds this one.
        await _queue.release(ids, 'HTTP 403 ${response.code ?? ''}');
        throw _Stop(SyncOutcome.needsSignIn, null, response.code ?? 'FORBIDDEN');
      case 401:
        await _queue.release(ids, 'HTTP 401 after refresh');
        throw _Stop(SyncOutcome.needsSignIn, null, 'unauthorized after refresh');
      case 429:
      case 503:
        await _queue.release(ids, 'HTTP ${response.status}');
        throw _Stop(SyncOutcome.serverBusy, response.retryAfter, 'HTTP ${response.status}');
      default:
        await _queue.release(ids, 'HTTP ${response.status}');
        throw _Stop(SyncOutcome.offline, response.retryAfter, 'HTTP ${response.status}');
    }
  }

  /// Pulls until hasMore is false; each page and its cursor are stored together.
  Future<int> _pullAll(String token, void Function(String) onToken) async {
    var count = 0;
    var refreshed = false;
    while (true) {
      await _keepLease();
      try {
        final page = await _api.pull(token, await _queue.cursor());
        await _queue.applyPullPage(page);
        count += page.changes.length;
        if (!page.hasMore) return count;
      } on ApiException catch (e) {
        if (e.status == 401 && !refreshed) {
          token = await _auth.refresh();
          onToken(token);
          refreshed = true;
          continue;
        }
        if (e.status == 401) throw _Stop(SyncOutcome.needsSignIn, null, 'unauthorized after refresh');
        throw _Stop(e.status == 429 || e.status == 503 ? SyncOutcome.serverBusy : SyncOutcome.offline,
            e.retryAfter, 'pull HTTP ${e.status}');
      }
    }
  }
}
