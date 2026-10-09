import 'dart:async';

import 'package:flutter/foundation.dart';

import 'sync_engine.dart';

/// What the sync screen shows.
@immutable
class SyncStatus {
  const SyncStatus({this.running = false, this.last, this.lastRunAt});

  final bool running;
  final SyncReport? last;
  final DateTime? lastRunAt;

  SyncStatus copyWith({bool? running, SyncReport? last, DateTime? lastRunAt}) =>
      SyncStatus(running: running ?? this.running, last: last ?? this.last, lastRunAt: lastRunAt ?? this.lastRunAt);
}

/// Decides when to sync: on reconnect, resume, after a save, pull-to-refresh, a periodic timer and after backoff.
class SyncScheduler {
  SyncScheduler(this._sync, {Stream<bool>? online, this.periodic = const Duration(minutes: 5), DateTime Function()? clock})
      : _online = online,
        _now = clock ?? DateTime.now;

  static const saveDebounce = Duration(seconds: 2);

  final Future<SyncReport> Function({bool force}) _sync;
  final Stream<bool>? _online;
  final Duration periodic;
  final DateTime Function() _now;

  final ValueNotifier<SyncStatus> status = ValueNotifier(const SyncStatus());

  StreamSubscription<bool>? _onlineSub;
  Timer? _debounce;
  Timer? _periodic;
  Timer? _retry;
  bool _wasOnline = true;

  void start() {
    _onlineSub = _online?.listen((online) {
      if (online && !_wasOnline) _run('connectivity regained');
      _wasOnline = online;
    });
    _periodic = Timer.periodic(periodic, (_) => _run('periodic'));
    _run('start');
  }

  /// Called after every enqueue: one run two seconds after the last save.
  void onSaved() {
    _debounce?.cancel();
    _debounce = Timer(saveDebounce, () => _run('saved'));
  }

  void onForeground() => _run('foreground');

  /// Pull-to-refresh and the "sync now" button: runs now, even inside a backoff window.
  Future<SyncReport> syncNow() => _run('manual', force: true);

  /// Never throws, since triggers are timers and streams; a failed run just shows as offline.
  Future<SyncReport> _run(String reason, {bool force = false}) async {
    status.value = status.value.copyWith(running: true);
    SyncReport report;
    try {
      report = await _sync(force: force);
    } catch (e) {
      report = SyncReport(SyncOutcome.offline, detail: 'sync failed: $e');
    }
    status.value = SyncStatus(running: false, last: report, lastRunAt: _now());
    _scheduleRetry(report);
    return report;
  }

  void _scheduleRetry(SyncReport report) {
    _retry?.cancel();
    final at = report.retryAt;
    if (at == null) return;
    final wait = at.difference(_now());
    _retry = Timer(wait.isNegative ? Duration.zero : wait, () => _run('retry after backoff'));
  }

  void dispose() {
    _onlineSub?.cancel();
    _debounce?.cancel();
    _periodic?.cancel();
    _retry?.cancel();
    status.dispose();
  }
}
