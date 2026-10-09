import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/queue_repository.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';
import 'package:melanin_wound_cdss/features/sync/engine/backoff.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';

import 'support/fakes.dart';

/// The sync engine against a fake gateway (architecture §6.2, §7.1, §11).
void main() {
  late AppDatabase db;
  late QueueRepository queue;
  late FakeGateway gateway;
  late AuthSession auth;
  late SyncEngine engine;
  late DateTime now;

  Future<void> signedIn() async =>
      auth.signIn(username: 'n.silva', password: 'correct-password', deviceId: await queue.deviceId());

  void build({FakeGateway? fake, int seed = 1}) {
    gateway = fake ?? FakeGateway();
    auth = AuthSession(gateway, MemorySecretStore(), clock: () => now);
    engine = SyncEngine(queue: queue, api: gateway, auth: auth, clock: () => now, random: Random(seed));
  }

  setUp(() {
    now = DateTime.utc(2026, 10, 1, 8);
    db = AppDatabase.inMemory();
    queue = QueueRepository(db, clock: () => now);
    build();
  });
  tearDown(() => db.close());

  test('happy path: push, then pull until every record is complete with its advice', () async {
    await signedIn();
    for (var i = 0; i < 5; i++) {
      await queue.enqueue(sampleEvent());
    }
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.success);
    expect(report.accepted, 5);
    expect((await queue.counts()).of(QueueStatus.complete), 5);
    final row = (await queue.all()).first;
    expect(await queue.recommendationFor(row.assessmentId, 1), isNotNull);
    expect(await queue.getState(SyncStateKeys.lastSuccessAt), isNotNull);
  });

  test('more than 50 waiting events go in several batches of at most 50', () async {
    await signedIn();
    for (var i = 0; i < 120; i++) {
      await queue.enqueue(sampleEvent());
    }
    await engine.sync();
    expect(gateway.pushes, 3);
    expect(gateway.stored.length, 120);
  });

  test('without a session the run pauses for sign-in and the queue is untouched (§7.3)', () async {
    await queue.enqueue(sampleEvent());
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.needsSignIn);
    expect((await queue.all()).single.status, QueueStatus.pending);
    expect(gateway.pushes, 0);
  });

  test('an expired access token is refreshed and the batch retried once (§7.1: 401)', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.expireAccessToken();
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.success);
    expect(gateway.refreshes, greaterThanOrEqualTo(1));
    expect(gateway.stored, hasLength(1));
  });

  test('a revoked session ends in needsSignIn with the rows back to pending', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.sessionRevoked = true;
    gateway.expireAccessToken();
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.needsSignIn);
    expect((await queue.all()).single.status, QueueStatus.pending);
    expect(await auth.hasSession(), isFalse);
  });

  test('413 splits the batch until it fits (§7.1)', () async {
    build(fake: FakeGateway(maxBatchBeforeSplit: 12));
    await signedIn();
    for (var i = 0; i < 40; i++) {
      await queue.enqueue(sampleEvent());
    }
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.success);
    expect(gateway.stored, hasLength(40));
    expect((await queue.counts()).of(QueueStatus.complete), 40);
  });

  test('429 / 503: rows back to pending, Retry-After honoured, then succeeds (§6.2)', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.scripted.add(PushFault.serverBusy503);
    final first = await engine.sync();
    expect(first.outcome, SyncOutcome.serverBusy);
    expect(first.retryAt!.difference(now), greaterThanOrEqualTo(const Duration(seconds: 10)));
    expect((await queue.all()).single.status, QueueStatus.pending);

    expect((await engine.sync()).outcome, SyncOutcome.backingOff, reason: 'inside the backoff window');
    now = first.retryAt!.add(const Duration(seconds: 1));
    expect((await engine.sync()).outcome, SyncOutcome.success);
  });

  test('unreachable gateway: probe fails, nothing is leased, back off', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.reachable = false;
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.offline);
    expect(gateway.pushes, 0);
    expect((await queue.all()).single.status, QueueStatus.pending);
  });

  test('response lost after the server accepted: resent, answered DUPLICATE, stored once (§11)', () async {
    await signedIn();
    final e = await queue.enqueue(sampleEvent());
    gateway.scripted.add(PushFault.dropResponseAfterAccept);
    expect((await engine.sync()).outcome, SyncOutcome.offline);
    expect(gateway.stored[e.eventId], 1);
    final second = await engine.sync(force: true);
    expect(second.outcome, SyncOutcome.success);
    expect(gateway.stored[e.eventId], 1, reason: 'never stored twice');
    expect((await queue.all()).single.status, QueueStatus.complete);
  });

  test('a refresh answered 503 backs off like a busy server, and the run never throws (§7.1)', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    now = now.add(const Duration(hours: 1)); // the access token expired: the run starts with a refresh
    gateway.refreshError = ApiException(503, null, const Duration(seconds: 30));
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.serverBusy);
    expect(report.retryAt!.difference(now), greaterThanOrEqualTo(const Duration(seconds: 30)));
    expect(await queue.getState(SyncStateKeys.nextAttemptAt), isNotEmpty);
    expect((await queue.all()).single.status, QueueStatus.pending);
    expect(await auth.hasSession(), isTrue, reason: 'a busy server is not a reason to sign in again');
    expect((await engine.sync(force: true)).outcome, SyncOutcome.success, reason: 'lease released, recovers');
  });

  test('a refresh failing while retrying a 401 push puts the batch straight back to pending', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.scripted.add(PushFault.expiredToken401);
    gateway.refreshError = ApiException(500);
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.offline);
    expect((await queue.all()).single.status, QueueStatus.pending, reason: 'not left leased for two minutes');
    expect(gateway.stored, isEmpty);
  });

  test('a malformed answer (a Wi-Fi login page) backs off and leaves no row leased', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    gateway.pushError = const FormatException('<html>Sign in to the hospital Wi-Fi</html>');
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.offline);
    expect(report.retryAt, isNotNull);
    expect((await queue.all()).single.status, QueueStatus.pending);
    expect((await engine.sync(force: true)).outcome, SyncOutcome.success);
  });

  test('a run whose lease was taken over stops before the next batch (§6.2 single flight)', () async {
    await signedIn();
    for (var i = 0; i < 60; i++) {
      await queue.enqueue(sampleEvent());
    }
    // During the first push this run is too slow: its lease lapses and another isolate's run takes it.
    gateway.onPush = () async {
      gateway.onPush = null;
      await queue.setState(SyncStateKeys.syncLeaseOwner, 'another-run');
    };
    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.alreadyRunning);
    expect(gateway.pushes, 1, reason: 'the second batch is left to the run that holds the lease');
    expect((await queue.counts()).of(QueueStatus.pending), 10);
    expect(await queue.getState(SyncStateKeys.syncLeaseOwner), 'another-run', reason: 'its lease is not released');
  });

  test('single flight: concurrent calls join one run', () async {
    await signedIn();
    for (var i = 0; i < 3; i++) {
      await queue.enqueue(sampleEvent());
    }
    final results = await Future.wait([engine.sync(), engine.sync(), engine.sync()]);
    expect(results.map((r) => r.outcome).toSet(), {SyncOutcome.success});
    expect(gateway.pushes, 1);
  });

  test('another isolate holding the sync lease: this run does nothing', () async {
    await signedIn();
    await queue.enqueue(sampleEvent());
    await queue.tryAcquireSyncLease(SyncEngine.syncLease);
    expect((await engine.sync()).outcome, SyncOutcome.alreadyRunning);
    expect(gateway.pushes, 0);
  });

  test('§14.1 step 4: a flaky gateway never causes a lost or stuck row', () async {
    // A third of pushes fail at random while new captures keep arriving.
    build(fake: FakeGateway(faultRate: 0.33, random: Random(7)), seed: 7);
    await signedIn();
    final ids = <String>{};
    for (var round = 0; round < 60; round++) {
      for (var i = 0; i < 5; i++) {
        ids.add((await queue.enqueue(sampleEvent())).eventId);
      }
      await engine.sync(force: true);
      now = now.add(const Duration(seconds: 30));
    }
    for (var i = 0; i < 50 && (await queue.counts()).of(QueueStatus.complete) < ids.length; i++) {
      await engine.sync(force: true);
      now = now.add(const Duration(minutes: 3)); // lets any lapsed lease return to pending
    }

    final rows = await queue.all();
    expect(rows, hasLength(300));
    expect(rows.where((r) => r.status != QueueStatus.complete), isEmpty, reason: 'no row lost or stuck');
    expect(gateway.stored.keys.toSet(), ids, reason: 'every event reached the server');
    expect(gateway.stored.values.every((n) => n == 1), isTrue, reason: 'and was stored exactly once');
    expect(rows.where((r) => r.attemptCount > 1), isNotEmpty, reason: 'the faults really forced resends');
  });

  test('backoff: 2 s doubling to 5 min, full jitter, reset on success (§6.2)', () {
    final always = Backoff(_MaxRandom());
    final ceilings = [for (var i = 0; i < 12; i++) always.next().inMilliseconds / 1000];
    expect(ceilings.take(3), [2, 4, 8]);
    expect(ceilings.last, 300);
    final b = Backoff(Random(1));
    for (var i = 0; i < 50; i++) {
      expect(b.next(), lessThanOrEqualTo(Backoff.max));
    }
    expect(b.next(retryAfter: const Duration(minutes: 10)), const Duration(minutes: 10));
    b.reset();
    expect(b.next(), lessThanOrEqualTo(Backoff.initial));
  });
}

class _MaxRandom implements Random {
  @override
  double nextDouble() => 1.0;
  @override
  bool nextBool() => true;
  @override
  int nextInt(int max) => max - 1;
}
