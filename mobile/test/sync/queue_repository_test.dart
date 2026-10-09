import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_models.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/queue_repository.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';

import 'support/fakes.dart';

/// The queue rules of architecture §6, §6.1 and §7.2.
void main() {
  late AppDatabase db;
  late QueueRepository queue;
  var now = DateTime.utc(2026, 10, 1, 8);

  setUp(() {
    now = DateTime.utc(2026, 10, 1, 8);
    db = AppDatabase.inMemory();
    queue = QueueRepository(db, clock: () => now);
  });
  tearDown(() => db.close());

  test('an invalid event never enters the queue', () async {
    final bad = sampleEvent()..['patientRef'] = 'Kamal Perera';
    await expectLater(queue.enqueue(bad), throwsA(isA<EnqueueRejected>()));
    expect((await queue.counts()).total, 0);
  });

  test('enqueue stores the event as pending and updates the local read model', () async {
    final e = sampleEvent();
    final row = await queue.enqueue(e);
    expect(row.status, QueueStatus.pending);
    final local = await db.select(db.assessmentLocal).getSingle();
    expect(local.assessmentId, e['assessmentId']);
  });

  test('the read model keeps the latest revision', () async {
    final first = sampleEvent();
    await queue.enqueue(first);
    await queue.enqueue(sampleEvent(assessmentId: first['assessmentId'] as String, woundId: first['woundId'] as String, revision: 2));
    expect((await db.select(db.assessmentLocal).getSingle()).latestRevision, 2);
  });

  test('batches are oldest first and at most 50 events', () async {
    for (var i = 0; i < 60; i++) {
      now = now.add(const Duration(seconds: 1));
      await queue.enqueue(sampleEvent());
    }
    final batch = await queue.leaseBatch();
    expect(batch, hasLength(50));
    final all = await queue.all();
    expect(batch.map((r) => r.eventId), all.take(50).map((r) => r.eventId));
    expect(batch.every((r) => r.status == QueueStatus.inFlight && r.attemptCount == 1), isTrue);
    expect((await queue.counts()).of(QueueStatus.inFlight), 50);
  });

  test('batches stay under 256 KB', () async {
    for (var i = 0; i < 30; i++) {
      final e = sampleEvent();
      ((e['analytics'] as Map)['pipeline'] as Map)['segmentation'] = 'x' * 15000; // ~15.5 KB each
      await queue.enqueue(e);
    }
    final batch = await queue.leaseBatch();
    expect(batch.fold<int>(0, (s, r) => s + r.sizeBytes), lessThanOrEqualTo(QueueRepository.maxBatchBytes));
    expect(batch.length, lessThan(30));
  });

  test('a leased row is not sent twice until its lease lapses (a killed app is safe, §6.2)', () async {
    await queue.enqueue(sampleEvent());
    expect(await queue.leaseBatch(), hasLength(1));
    expect(await queue.leaseBatch(), isEmpty);
    now = now.add(QueueRepository.lease + const Duration(seconds: 1));
    expect(await queue.releaseExpiredLeases(), 1);
    final again = await queue.leaseBatch();
    expect(again.single.attemptCount, 2);
  });

  test('push results: DUPLICATE counts as ACCEPTED, REJECTED is final (§7.1)', () async {
    final a = await queue.enqueue(sampleEvent());
    final b = await queue.enqueue(sampleEvent());
    final c = await queue.enqueue(sampleEvent());
    await queue.leaseBatch();
    await queue.applyPushResults([
      PushEventResult(a.eventId, 'ACCEPTED'),
      PushEventResult(b.eventId, 'DUPLICATE'),
      PushEventResult(c.eventId, 'REJECTED', 'SCHEMA_INVALID'),
    ]);
    final rows = {for (final r in await queue.all()) r.eventId: r};
    expect(rows[a.eventId]!.status, QueueStatus.accepted);
    expect(rows[b.eventId]!.status, QueueStatus.accepted);
    expect(rows[c.eventId]!.status, QueueStatus.rejected);
    expect(rows[c.eventId]!.lastError, 'SCHEMA_INVALID');
    expect(await queue.hasWorkToSend(), isFalse, reason: 'rejected rows are never retried automatically');
  });

  test('a failed push returns its rows to pending', () async {
    await queue.enqueue(sampleEvent());
    final batch = await queue.leaseBatch();
    await queue.release(batch.map((r) => r.eventId), 'HTTP 503');
    expect((await queue.all()).single.status, QueueStatus.pending);
    expect(await queue.hasWorkToSend(), isTrue);
  });

  test('pulled changes take a record through adviceDeferred to complete, and store the advice', () async {
    final e = await queue.enqueue(sampleEvent());
    await queue.leaseBatch();
    await queue.applyPushResults([PushEventResult(e.eventId, 'ACCEPTED')]);
    await queue.applyPullPage(PullPage([
      SyncChange(seq: 1, type: 'PERSISTED', assessmentId: e.assessmentId, revision: 1),
      SyncChange(seq: 2, type: 'ADVICE_DEFERRED', assessmentId: e.assessmentId, revision: 1),
    ], 2, false));
    expect((await queue.all()).single.status, QueueStatus.adviceDeferred);

    final advice = {'mode': 'generated', 'sections': []};
    final page = PullPage([
      SyncChange(seq: 3, type: 'RECOMMENDATION_READY', assessmentId: e.assessmentId, revision: 1, mode: 'generated', recommendationJson: jsonEncode(advice)),
    ], 3, false);
    await queue.applyPullPage(page);
    await queue.applyPullPage(page); // re-sent within the window: harmless
    expect((await queue.all()).single.status, QueueStatus.complete);
    expect((await queue.recommendationFor(e.assessmentId, 1))!.mode, 'generated');
    expect(await queue.cursor(), 3);
  });

  test('a PERSISTED change marks a row accepted even when its push answer was lost', () async {
    final e = await queue.enqueue(sampleEvent());
    await queue.leaseBatch();
    await queue.applyPullPage(PullPage([SyncChange(seq: 1, type: 'PERSISTED', assessmentId: e.assessmentId, revision: 1)], 1, false));
    expect((await queue.all()).single.status, QueueStatus.accepted);
  });

  test('a superseded revision is closed, the newer one is not', () async {
    final r1 = await queue.enqueue(sampleEvent());
    final r2 = await queue.enqueue(sampleEvent(assessmentId: r1.assessmentId, revision: 2));
    await queue.applyPullPage(PullPage([SyncChange(seq: 1, type: 'SUPERSEDED', assessmentId: r1.assessmentId, revision: 1)], 1, false));
    final rows = {for (final r in await queue.all()) r.revision: r};
    expect(rows[1]!.status, QueueStatus.superseded);
    expect(rows[2]!.status, QueueStatus.pending);
    expect(r2.revision, 2);
  });

  test('advice for another phone\'s assessment (same facility) is kept for the ward view', () async {
    await queue.applyPullPage(PullPage([
      const SyncChange(seq: 9, type: 'RECOMMENDATION_READY', assessmentId: 'other-assessment', revision: 1, mode: 'extractive', recommendationJson: '{"mode":"extractive"}'),
    ], 9, false));
    expect(await queue.recommendationFor('other-assessment', 1), isNotNull);
    expect(await queue.cursor(), 9);
  });

  test('the sync lease gives single flight across isolates (§6.2)', () async {
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3)), isTrue);
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3)), isFalse);
    now = now.add(const Duration(minutes: 4)); // the holder was killed: the lease lapses on its own
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3)), isTrue);
    await queue.releaseSyncLease();
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3)), isTrue);
  });

  test('a run renews and releases only its own sync lease', () async {
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3), owner: 'a'), isTrue);
    now = now.add(const Duration(minutes: 2));
    expect(await queue.renewSyncLease(const Duration(minutes: 3), owner: 'a'), isTrue);
    now = now.add(const Duration(minutes: 2));
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3), owner: 'b'), isFalse, reason: 'renewed');

    now = now.add(const Duration(minutes: 4)); // a stalls, its lease lapses, b takes it
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3), owner: 'b'), isTrue);
    expect(await queue.renewSyncLease(const Duration(minutes: 3), owner: 'a'), isFalse);
    await queue.releaseSyncLease(owner: 'a');
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3), owner: 'c'), isFalse, reason: 'b still holds it');
    await queue.releaseSyncLease(owner: 'b');
    expect(await queue.tryAcquireSyncLease(const Duration(minutes: 3), owner: 'c'), isTrue);
  });

  test('the device id is created once and kept', () async {
    final id = await queue.deviceId();
    expect(id, startsWith('dev-'));
    expect(await queue.deviceId(), id);
  });

  test('screens see a burst of queue writes as a few updates, ending on the final state (§13 frame budget)', () async {
    final seen = <int>[];
    final sub = queue.watchCounts().listen((c) => seen.add(c.total));
    await Future<void>.delayed(const Duration(milliseconds: 50));
    for (var i = 0; i < 40; i++) {
      await queue.enqueue(sampleEvent());
    }
    await Future<void>.delayed(QueueRepository.uiRefresh * 3);
    await sub.cancel();
    expect(seen.first, 0);
    expect(seen.last, 40, reason: 'the last change is always shown');
    expect(seen.length, lessThan(8), reason: '40 writes, not 40 rebuilds: $seen');
  });
}
