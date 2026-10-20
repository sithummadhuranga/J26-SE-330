import 'dart:async';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_scheduler.dart';

/// Checks when the sync runs (architecture §6.2), using a fake clock.
void main() {
  late List<bool> calls; // force flag of each run
  late SyncReport Function() next;
  late StreamController<bool> online;

  SyncScheduler build(FakeAsync async) {
    calls = [];
    next = () => SyncReport(SyncOutcome.success);
    online = StreamController<bool>();
    return SyncScheduler(
      ({bool force = false}) async {
        calls.add(force);
        return next();
      },
      online: online.stream,
      clock: () => DateTime.utc(2026).add(async.elapsed),
    );
  }

  test('start runs one sync', () => fakeAsync((async) {
        final s = build(async)..start();
        async.flushMicrotasks();
        expect(calls, [false]);
        s.dispose();
      }));

  test('a burst of saves is one sync, two seconds after the last save', () => fakeAsync((async) {
        final s = build(async);
        s.onSaved();
        async.elapse(const Duration(milliseconds: 900));
        s.onSaved();
        async.elapse(const Duration(milliseconds: 900));
        s.onSaved();
        async.elapse(const Duration(milliseconds: 1999));
        expect(calls, isEmpty);
        async.elapse(const Duration(milliseconds: 1));
        expect(calls, [false]);
        s.dispose();
      }));

  test('losing the network runs a quick check, so the app shows offline straight away', () => fakeAsync((async) {
        final s = build(async)..start();
        async.flushMicrotasks();
        expect(s.status.value.last!.outcome, SyncOutcome.success);
        calls.clear();
        next = () => SyncReport(SyncOutcome.offline);
        online.add(false);
        async.flushMicrotasks();
        expect(calls, [false], reason: 'one check, not forced past a backoff');
        expect(s.status.value.last!.outcome, SyncOutcome.offline);
        online.add(false);
        async.flushMicrotasks();
        expect(calls, [false], reason: 'staying offline does not check again');
        s.dispose();
      }));

  test('losing the network during a run checks again once that run is done, not with its old result', () =>
      fakeAsync((async) {
        build(async).dispose(); // only for the online stream; this test needs a run it can hold open
        calls = [];
        final slow = Completer<SyncReport>();
        final scheduler = SyncScheduler(
          ({bool force = false}) {
            calls.add(force);
            return calls.length == 1 ? slow.future : Future.value(SyncReport(SyncOutcome.offline));
          },
          online: online.stream,
          clock: () => DateTime.utc(2026).add(async.elapsed),
        )..start();
        async.flushMicrotasks();
        online.add(false);
        async.flushMicrotasks();
        expect(calls, [false], reason: 'nothing extra while the start-up run is still going');
        slow.complete(SyncReport(SyncOutcome.success));
        async.flushMicrotasks();
        expect(calls, [false, false], reason: 'the check runs right after it');
        expect(scheduler.status.value.last!.outcome, SyncOutcome.offline);
        scheduler.dispose();
      }));

  test('connectivity regained triggers a sync; staying online does not', () => fakeAsync((async) {
        final s = build(async)..start();
        async.flushMicrotasks();
        calls.clear();
        online.add(true);
        async.flushMicrotasks();
        expect(calls, isEmpty, reason: 'already online');
        online.add(false);
        async.flushMicrotasks();
        calls.clear();
        online.add(true);
        async.flushMicrotasks();
        expect(calls, [false]);
        s.dispose();
      }));

  test('after a backoff the next sync runs at the time the engine asked for', () => fakeAsync((async) {
        final s = build(async);
        final retryAt = DateTime.utc(2026).add(const Duration(seconds: 30));
        next = () => SyncReport(SyncOutcome.serverBusy, retryAt: retryAt);
        s.onForeground();
        async.flushMicrotasks();
        next = () => SyncReport(SyncOutcome.success);
        calls.clear();
        async.elapse(const Duration(seconds: 29));
        expect(calls, isEmpty);
        async.elapse(const Duration(seconds: 1));
        expect(calls, [false]);
        s.dispose();
      }));

  test('periodic sync while the app is open', () => fakeAsync((async) {
        final s = build(async)..start();
        async.flushMicrotasks();
        calls.clear();
        async.elapse(const Duration(minutes: 15));
        expect(calls, hasLength(3));
        s.dispose();
      }));

  test('sync now (pull-to-refresh) ignores the backoff window and updates the status', () => fakeAsync((async) {
        final s = build(async);
        expect(s.status.value.last, isNull);
        unawaited(s.syncNow());
        expect(s.status.value.running, isTrue);
        async.flushMicrotasks();
        expect(calls, [true]);
        expect(s.status.value.running, isFalse);
        expect(s.status.value.last!.outcome, SyncOutcome.success);
        s.dispose();
      }));

  test('a run that throws shows as offline and never escapes a trigger', () => fakeAsync((async) {
        final s = build(async);
        next = () => throw StateError('database closed');
        SyncReport? report;
        s.syncNow().then((r) => report = r);
        async.flushMicrotasks();
        expect(report!.outcome, SyncOutcome.offline);
        expect(s.status.value.running, isFalse);
        expect(s.status.value.last!.detail, contains('database closed'));
        s.onSaved();
        async.elapse(SyncScheduler.saveDebounce); // from a timer: would be an unhandled error if it threw
        expect(calls, [true, false]);
        s.dispose();
      }));
}
