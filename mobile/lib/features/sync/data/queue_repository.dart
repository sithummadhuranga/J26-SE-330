import 'dart:async';
import 'dart:convert';

import 'package:drift/drift.dart';
import 'package:uuid/uuid.dart';

import '../api/sync_models.dart';
import '../domain/wound_event_validator.dart';
import 'app_database.dart';
import 'tables.dart';

/// Thrown by [QueueRepository.enqueue] when an event doesn't match the contract. Nothing gets stored.
class EnqueueRejected implements Exception {
  EnqueueRejected(this.errors);

  final List<String> errors;

  @override
  String toString() => 'EnqueueRejected: ${errors.join('; ')}';
}

/// How many events are in each status, shown on the sync screen.
class QueueCounts {
  const QueueCounts(this.byStatus);

  final Map<QueueStatus, int> byStatus;

  int of(QueueStatus s) => byStatus[s] ?? 0;
  int get waitingToSend => of(QueueStatus.pending) + of(QueueStatus.inFlight);
  int get total => byStatus.values.fold(0, (a, b) => a + b);
}

/// This is the only way an assessment leaves the phone. Every method runs as one transaction.
class QueueRepository {
  QueueRepository(this._db, {DateTime Function()? clock}) : _now = clock ?? DateTime.now;

  final AppDatabase _db;
  final DateTime Function() _now;

  /// Rows being sent are leased for about two minutes. If the lease runs out, they go back to pending
  /// (architecture §6.2).
  static const lease = Duration(minutes: 2);

  /// We send at most 50 events or 256 KB in one request (architecture §6.2).
  static const maxBatchEvents = 50;
  static const maxBatchBytes = 256 * 1024;

  // ---- writing the queue ------------------------------------------------------------------------------------------

  /// Checks the event against the contract (§5) and stores it as pending. It's committed before this returns,
  /// so the UI can safely tell the clinician it was saved.
  Future<QueuedEvent> enqueue(Map<String, dynamic> event) async {
    final errors = WoundEventValidator.validate(event);
    if (errors.isNotEmpty) throw EnqueueRejected(errors);

    final json = jsonEncode(event);
    final row = WoundEventQueueCompanion.insert(
      eventId: event['eventId'] as String,
      assessmentId: event['assessmentId'] as String,
      revision: event['revision'] as int,
      payloadJson: json,
      sizeBytes: utf8.encode(json).length,
      status: QueueStatus.pending,
      createdAt: _now(),
    );
    return _db.transaction(() async {
      await _db.into(_db.woundEventQueue).insert(row);
      await _upsertAssessment(event);
      return (_db.select(_db.woundEventQueue)..where((q) => q.eventId.equals(event['eventId'] as String))).getSingle();
    });
  }

  /// Keeps the latest revision of each assessment so history can be browsed offline (architecture §6).
  Future<void> _upsertAssessment(Map<String, dynamic> event) async {
    final assessmentId = event['assessmentId'] as String;
    final revision = event['revision'] as int;
    final existing = await (_db.select(_db.assessmentLocal)..where((a) => a.assessmentId.equals(assessmentId)))
        .getSingleOrNull();
    if (existing != null && existing.latestRevision >= revision) return;
    final analytics = event['analytics'] as Map<String, dynamic>;
    final summary = jsonEncode({
      'woundId': event['woundId'],
      'patientRef': event['patientRef'],
      'capturedAt': event['capturedAt'],
      'areaMm2': analytics['areaMm2'],
      'clinicalAssessment': event['clinicalAssessment'],
    });
    await _db.into(_db.assessmentLocal).insertOnConflictUpdate(
        AssessmentLocalCompanion.insert(assessmentId: assessmentId, latestRevision: revision, summaryJson: summary));
  }

  /// Puts rows whose lease has expired back to pending, for example after the app was killed mid-sync.
  Future<int> releaseExpiredLeases() => (_db.update(_db.woundEventQueue)
        ..where((q) => q.status.equalsValue(QueueStatus.inFlight) & q.leaseExpiresAt.isSmallerOrEqualValue(_now())))
      .write(const WoundEventQueueCompanion(status: Value(QueueStatus.pending), leaseExpiresAt: Value(null)));

  /// Picks the next batch, oldest first, and leases it. A batch stays within 50 events and 256 KB,
  /// but always holds at least one event.
  Future<List<QueuedEvent>> leaseBatch({int maxEvents = maxBatchEvents}) => _db.transaction(() async {
        final now = _now();
        final candidates = await (_db.select(_db.woundEventQueue)
              ..where((q) =>
                  q.status.equalsValue(QueueStatus.pending) |
                  (q.status.equalsValue(QueueStatus.inFlight) & q.leaseExpiresAt.isSmallerOrEqualValue(now)))
              ..orderBy([(q) => OrderingTerm.asc(q.createdAt), (q) => OrderingTerm.asc(q.eventId)])
              ..limit(maxEvents))
            .get();

        final batch = <QueuedEvent>[];
        var bytes = 0;
        for (final row in candidates) {
          if (batch.isNotEmpty && bytes + row.sizeBytes > maxBatchBytes) break;
          batch.add(row);
          bytes += row.sizeBytes;
        }
        final leased = <QueuedEvent>[];
        for (final row in batch) {
          final updated = row.copyWith(
            status: QueueStatus.inFlight,
            leaseExpiresAt: Value(now.add(lease)),
            attemptCount: row.attemptCount + 1,
          );
          await _db.update(_db.woundEventQueue).replace(updated);
          leased.add(updated);
        }
        return leased;
      });

  /// Called when a push failed or got no answer (429, 503, a timeout or silence). Its rows go back to pending (§7.1).
  Future<void> release(Iterable<String> eventIds, String error) => (_db.update(_db.woundEventQueue)
        ..where((q) => q.eventId.isIn(eventIds) & q.status.equalsValue(QueueStatus.inFlight)))
      .write(WoundEventQueueCompanion(
          status: const Value(QueueStatus.pending), leaseExpiresAt: const Value(null), lastError: Value(error)));

  /// Records what the server said about each event in a push (§7.1). A DUPLICATE counts as accepted,
  /// and a REJECTED event stays rejected.
  Future<void> applyPushResults(List<PushEventResult> results) => _db.transaction(() async {
        final now = _now();
        for (final r in results) {
          if (r.status == 'ACCEPTED' || r.status == 'DUPLICATE') {
            // Don't move a record backwards. A pulled change may already have moved it further along.
            await (_db.update(_db.woundEventQueue)
                  ..where((q) =>
                      q.eventId.equals(r.eventId) &
                      q.status.isInValues([QueueStatus.inFlight, QueueStatus.pending])))
                .write(WoundEventQueueCompanion(
                    status: const Value(QueueStatus.accepted), leaseExpiresAt: const Value(null), ackedAt: Value(now)));
          } else {
            await reject([r.eventId], r.code ?? 'REJECTED');
          }
        }
      });

  /// Marks events as rejected. The clinician sees them, and we never retry them automatically (§6.1).
  Future<void> reject(Iterable<String> eventIds, String code) => (_db.update(_db.woundEventQueue)
        ..where((q) => q.eventId.isIn(eventIds)))
      .write(WoundEventQueueCompanion(
          status: const Value(QueueStatus.rejected), leaseExpiresAt: const Value(null), lastError: Value(code)));

  /// Applies one page from a pull and saves the cursor in the same transaction.
  Future<void> applyPullPage(PullPage page) => _db.transaction(() async {
        final now = _now();
        for (final c in page.changes) {
          final where = (_db.update(_db.woundEventQueue)
            ..where((q) => q.assessmentId.equals(c.assessmentId) & q.revision.equals(c.revision)));
          switch (c.type) {
            case 'PERSISTED':
              // The server has stored it, so the push got through even if we never saw its response.
              await (_db.update(_db.woundEventQueue)
                    ..where((q) =>
                        q.assessmentId.equals(c.assessmentId) &
                        q.revision.equals(c.revision) &
                        q.status.isInValues([QueueStatus.pending, QueueStatus.inFlight])))
                  .write(WoundEventQueueCompanion(
                      status: const Value(QueueStatus.accepted),
                      leaseExpiresAt: const Value(null),
                      ackedAt: Value(now)));
            case 'ADVICE_DEFERRED':
              await (_db.update(_db.woundEventQueue)
                    ..where((q) =>
                        q.assessmentId.equals(c.assessmentId) &
                        q.revision.equals(c.revision) &
                        q.status.isInValues([QueueStatus.pending, QueueStatus.inFlight, QueueStatus.accepted])))
                  .write(WoundEventQueueCompanion(
                      status: const Value(QueueStatus.adviceDeferred),
                      leaseExpiresAt: const Value(null),
                      ackedAt: Value(now)));
            case 'RECOMMENDATION_READY':
              // Changes are shared across the facility, so we also keep advice for other phones' assessments.
              // That gives everyone on the ward the same view.
              if (c.recommendationJson case final payload?) {
                await _db.into(_db.recommendationLocal).insertOnConflictUpdate(RecommendationLocalCompanion.insert(
                    assessmentId: c.assessmentId,
                    revision: c.revision,
                    payloadJson: payload,
                    mode: c.mode ?? 'unknown',
                    receivedAt: now));
              }
              await where.write(WoundEventQueueCompanion(
                  status: const Value(QueueStatus.complete),
                  leaseExpiresAt: const Value(null),
                  completedAt: Value(now)));
            case 'SUPERSEDED':
              await (_db.update(_db.woundEventQueue)
                    ..where((q) =>
                        q.assessmentId.equals(c.assessmentId) &
                        q.revision.equals(c.revision) &
                        q.status.equalsValue(QueueStatus.complete).not()))
                  .write(WoundEventQueueCompanion(
                      status: const Value(QueueStatus.superseded),
                      leaseExpiresAt: const Value(null),
                      completedAt: Value(now)));
          }
        }
        await setState(SyncStateKeys.serverCursor, '${page.nextCursor}');
      });

  // ---- reading -----------------------------------------------------------------------------------------------------

  Future<bool> hasWorkToSend() async {
    final now = _now();
    final row = await (_db.select(_db.woundEventQueue)
          ..where((q) =>
              q.status.equalsValue(QueueStatus.pending) |
              (q.status.equalsValue(QueueStatus.inFlight) & q.leaseExpiresAt.isSmallerOrEqualValue(now)))
          ..limit(1))
        .getSingleOrNull();
    return row != null;
  }

  Future<QueueCounts> counts() async => QueueCounts(await _countRows().get().then(_toMap));

  Stream<QueueCounts> watchCounts() => _watchQueue(counts);

  Selectable<TypedResult> _countRows() {
    final count = _db.woundEventQueue.eventId.count();
    return (_db.selectOnly(_db.woundEventQueue)
          ..addColumns([_db.woundEventQueue.status, count])
          ..groupBy([_db.woundEventQueue.status]));
  }

  Map<QueueStatus, int> _toMap(List<TypedResult> rows) => {
        for (final r in rows)
          QueueStatus.values.byName(r.read(_db.woundEventQueue.status)!): r.read(_db.woundEventQueue.eventId.count())!,
      };

  Future<List<QueuedEvent>> all() =>
      (_db.select(_db.woundEventQueue)..orderBy([(q) => OrderingTerm.asc(q.createdAt)])).get();

  /// Newest first, for the sync screen.
  Stream<List<QueuedEvent>> watchRecent({int limit = 50}) => _watchQueue((_db.select(_db.woundEventQueue)
        ..orderBy([(q) => OrderingTerm.desc(q.createdAt), (q) => OrderingTerm.desc(q.eventId)])
        ..limit(limit))
      .get);

  /// Limits how often screens hear about queue changes, so a busy sync doesn't make the UI stutter.
  static const uiRefresh = Duration(milliseconds: 300);

  /// Runs [load] now and again whenever the queue changes. It runs at most once per [uiRefresh],
  /// and never two queries at the same time.
  Stream<T> _watchQueue<T>(Future<T> Function() load) {
    late final StreamController<T> controller;
    StreamSubscription<void>? updates;
    Timer? timer;
    var loading = false, again = false;

    Future<void> emit() async {
      if (loading) {
        again = true;
        return;
      }
      loading = true;
      try {
        final value = await load();
        if (!controller.isClosed) controller.add(value);
      } catch (e, st) {
        if (!controller.isClosed) controller.addError(e, st);
      } finally {
        loading = false;
      }
      if (again) {
        again = false;
        unawaited(emit());
      }
    }

    controller = StreamController<T>(
      onListen: () {
        unawaited(emit());
        updates = _db.tableUpdates(TableUpdateQuery.onTable(_db.woundEventQueue)).listen((_) {
          timer ??= Timer(uiRefresh, () {
            timer = null;
            unawaited(emit());
          });
        });
      },
      onCancel: () async {
        timer?.cancel();
        await updates?.cancel();
      },
    );
    return controller.stream;
  }

  Future<RecommendationLocalData?> recommendationFor(String assessmentId, int revision) =>
      (_db.select(_db.recommendationLocal)
            ..where((r) => r.assessmentId.equals(assessmentId) & r.revision.equals(revision)))
          .getSingleOrNull();

  // ---- sync_state --------------------------------------------------------------------------------------------------

  Future<int> cursor() async => int.tryParse(await getState(SyncStateKeys.serverCursor) ?? '') ?? 0;

  /// This phone's id. It's created the first time it's needed and kept until the app is uninstalled.
  /// The gateway ties each token to it (§7.1).
  Future<String> deviceId() async {
    final existing = await getState(SyncStateKeys.deviceId);
    if (existing != null) return existing;
    final id = 'dev-${const Uuid().v4()}';
    await setState(SyncStateKeys.deviceId, id);
    return id;
  }

  Future<String?> getState(String key) async =>
      (await (_db.select(_db.syncState)..where((s) => s.key.equals(key))).getSingleOrNull())?.value;

  Future<void> setState(String key, String value) =>
      _db.into(_db.syncState).insertOnConflictUpdate(SyncStateCompanion.insert(key: key, value: value));

  /// Takes the sync lease in the database for [owner], unless another run still holds it.
  Future<bool> tryAcquireSyncLease(Duration duration, {String owner = ''}) => _db.transaction(() async {
        final now = _now();
        final until = DateTime.tryParse(await getState(SyncStateKeys.syncLeaseUntil) ?? '');
        if (until != null && until.isAfter(now)) return false;
        await setState(SyncStateKeys.syncLeaseUntil, now.add(duration).toIso8601String());
        await setState(SyncStateKeys.syncLeaseOwner, owner);
        return true;
      });

  /// Extends [owner]'s lease. Returns false if another run has taken it over, and then the caller must stop.
  Future<bool> renewSyncLease(Duration duration, {String owner = ''}) => _db.transaction(() async {
        if (await getState(SyncStateKeys.syncLeaseOwner) != owner) return false;
        await setState(SyncStateKeys.syncLeaseUntil, _now().add(duration).toIso8601String());
        return true;
      });

  /// Lets go of the lease, unless another run has taken it in the meantime.
  Future<void> releaseSyncLease({String owner = ''}) => _db.transaction(() async {
        if (await getState(SyncStateKeys.syncLeaseOwner) != owner) return;
        await setState(SyncStateKeys.syncLeaseUntil, '');
      });
}
