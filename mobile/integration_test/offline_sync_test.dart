import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:integration_test/integration_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/app/app_shell.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';
import 'package:path_provider/path_provider.dart';

import '../test/sync/support/fakes.dart';

/// End-to-end offline sync test on an emulator via Toxiproxy; start the stack with `docker compose up -d` first.
const proxyUrl = String.fromEnvironment('SYNC_BASE_URL', defaultValue: 'http://10.0.2.2:18080');
const toxiproxyUrl = String.fromEnvironment('TOXIPROXY_URL', defaultValue: 'http://10.0.2.2:8474');

/// Takes the phone's route to the gateway down or up, as walking out of Wi-Fi range would.
Future<void> network({required bool up}) async {
  // PATCH: updating a proxy with POST is deprecated in Toxiproxy.
  final r = await http.patch(Uri.parse('$toxiproxyUrl/proxies/sync-gateway'), body: jsonEncode({'enabled': up}));
  if (r.statusCode != 200) throw StateError('Toxiproxy answered ${r.statusCode}: ${r.body}');
}

/// The real client, cutting the network right after the first push has its answer.
class _CutAfterFirstPush extends HttpSyncApi {
  _CutAfterFirstPush(super.baseUri);

  int pushes = 0;

  @override
  Future<PushResponse> push(String accessToken, String deviceId, List<String> eventJson) async {
    final r = await super.push(accessToken, deviceId, eventJson);
    if (++pushes == 1) await network(up: false);
    return r;
  }
}

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  const secrets = KeystoreSecretStore();
  late File dbFile;

  /// Opens the app's services as start-up does, on a test database file next to the app's own.
  Future<AppServices> openApp({SyncApi? api}) async => AppServices.open(
        db: AppDatabase.encrypted(dbFile, await DatabaseKeyStore.getOrCreate(secrets)),
        secrets: secrets,
        api: api ?? HttpSyncApi(Uri.parse(proxyUrl)),
        online: const Stream.empty(),
      );

  Future<void> enqueue(AppServices app, int n) async {
    final deviceId = await app.queue.deviceId();
    for (var i = 0; i < n; i++) {
      await app.queue.enqueue(sampleEvent(deviceId: deviceId));
    }
  }

  /// Syncs until every row has its advice (or was superseded); the orchestrator takes a few seconds per batch.
  Future<void> syncUntilDone(AppServices app, int expected, {Duration timeout = const Duration(minutes: 3)}) async {
    final deadline = DateTime.now().add(timeout);
    while (true) {
      final c = await app.queue.counts();
      if (c.of(QueueStatus.complete) + c.of(QueueStatus.superseded) == expected) return;
      if (DateTime.now().isAfter(deadline)) fail('not done in time: ${c.byStatus}');
      await Future<void>.delayed(const Duration(seconds: 2));
      await app.engine.sync(force: true);
    }
  }

  setUp(() async {
    await network(up: true);
    dbFile = File('${(await getApplicationSupportDirectory()).path}/it_offline_sync.db');
    if (dbFile.existsSync()) dbFile.deleteSync();
  });
  tearDown(() => network(up: true));

  testWidgets('saved offline, app killed, back online: every assessment arrives once, with its advice', (_) async {
    var app = await openApp();
    await app.signIn(username: 'n.silva', password: 'Demo-Pass-2026!');

    await network(up: false);
    await enqueue(app, 3);
    final offline = await app.engine.sync();
    expect(offline.outcome, SyncOutcome.offline, reason: '$offline');
    expect((await app.queue.counts()).of(QueueStatus.pending), 3);

    // Killed: the access token (memory only) is gone; the queue file and the Keystore session remain.
    await app.close();
    app = await openApp();
    expect((await app.queue.counts()).of(QueueStatus.pending), 3, reason: 'the encrypted queue survived');

    await network(up: true);
    final back = await app.engine.sync(force: true);
    expect(back.outcome, SyncOutcome.success, reason: '$back');
    expect(back.accepted, 3);
    await syncUntilDone(app, 3);
    for (final row in await app.queue.all()) {
      expect(await app.queue.recommendationFor(row.assessmentId, row.revision), isNotNull);
    }
    await app.close();
  });

  testWidgets('the network drops in the middle of a sync: nothing lost, nothing stuck, nothing rejected', (_) async {
    final api = _CutAfterFirstPush(Uri.parse(proxyUrl));
    final app = await openApp(api: api);
    await app.signIn(username: 'n.silva', password: 'Demo-Pass-2026!');
    await enqueue(app, 120);

    final cut = await app.engine.sync();
    expect(cut.outcome, SyncOutcome.offline, reason: '$cut');
    expect(cut.accepted, 50, reason: 'the first batch made it');
    final c = await app.queue.counts();
    expect(c.of(QueueStatus.pending), 70, reason: 'the rest went back to pending, none left leased: ${c.byStatus}');

    await network(up: true);
    expect((await app.engine.sync(force: true)).outcome, SyncOutcome.success);
    await syncUntilDone(app, 120);
    expect((await app.queue.counts()).of(QueueStatus.rejected), 0);
    await app.close();
  });

  testWidgets('§13 mobile responsiveness: no frame over 200 ms while 1,000 assessments sync', (tester) async {
    final app = await openApp();
    await app.signIn(username: 'n.silva', password: 'Demo-Pass-2026!');
    await enqueue(app, 1000);

    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: AppShell(services: app, onSignedOut: () {})));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Profile')); // the sync row's spinner animates for the whole run
    await tester.pumpAndSettle();

    binding.framePolicy = LiveTestWidgetsFlutterBindingFramePolicy.fullyLive;
    final timings = <FrameTiming>[];
    void collect(List<FrameTiming> t) => timings.addAll(t);
    SchedulerBinding.instance.addTimingsCallback(collect);

    final watch = Stopwatch()..start();
    var done = false;
    late SyncReport report;
    unawaited(app.scheduler.syncNow().then((r) {
      report = r;
      done = true;
    }));
    while (!done) {
      await tester.pump(const Duration(milliseconds: 16));
    }
    watch.stop();
    await tester.pump(const Duration(milliseconds: 500)); // the last timings arrive in batches
    SchedulerBinding.instance.removeTimingsCallback(collect);
    binding.framePolicy = LiveTestWidgetsFlutterBindingFramePolicy.fadePointers;

    expect(report.outcome, SyncOutcome.success, reason: '$report');
    expect(report.accepted, 1000);
    final spans = [for (final t in timings) t.totalSpan.inMicroseconds / 1000]..sort();
    expect(spans, isNotEmpty);
    final worst = spans.last;
    final p99 = spans[(spans.length * 0.99).floor().clamp(0, spans.length - 1)];
    binding.reportData = {
      'sync_frames': {
        'frames': spans.length,
        'syncSeconds': watch.elapsedMilliseconds / 1000,
        'worstFrameMs': worst,
        'p99FrameMs': p99,
        'framesOver16ms': spans.where((s) => s > 16.7).length,
        'framesOver200ms': spans.where((s) => s > 200).length,
        // Frame time split into the UI thread (ours) and the raster thread (GPU, slow on emulators).
        'worstBuildMs': timings.map((t) => t.buildDuration.inMicroseconds / 1000).reduce((a, b) => a > b ? a : b),
        'worstRasterMs': timings.map((t) => t.rasterDuration.inMicroseconds / 1000).reduce((a, b) => a > b ? a : b),
      },
    };
    debugPrint('SYNC_FRAMES ${jsonEncode(binding.reportData)}');
    expect(worst, lessThanOrEqualTo(200), reason: 'worst frame $worst ms (p99 $p99 ms, ${spans.length} frames)');

    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(milliseconds: 200));
    await app.close();
  });
}
