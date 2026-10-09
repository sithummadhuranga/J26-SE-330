import 'dart:isolate';
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/app/background_sync.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/queue_repository.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';

import 'support/fakes.dart';

/// Background sync task, with in-memory services instead of real ones.
void main() {
  late FakeGateway gateway;
  late MemorySecretStore secrets;
  late AppDatabase db;

  setUp(() {
    gateway = FakeGateway();
    secrets = MemorySecretStore();
    db = AppDatabase.inMemory();
  });

  Future<AppServices> open() =>
      AppServices.open(db: db, secrets: secrets, api: gateway, online: const Stream.empty());

  test('a background run sends what is waiting', () async {
    final app = await open();
    await app.signIn(username: 'n.silva', password: 'correct-password');
    final e = await app.queue.enqueue(sampleEvent());
    // The task opens its own services: same keystore (session) and database, as on the phone.
    var opened = 0;
    expect(await BackgroundSync.run(open: () async {
      opened++;
      return AppServices.open(db: db, secrets: secrets, api: gateway, online: const Stream.empty());
    }), isTrue);
    expect(opened, 1);
    expect(gateway.storedEvents.keys, [e.eventId]);
  });

  test('nobody signed in: the task does nothing and still reports done', () async {
    final app = await open();
    await app.queue.enqueue(sampleEvent());
    expect(await BackgroundSync.run(open: open), isTrue);
    expect(gateway.pushes, 0);
  });

  test('the app holding the sync lease: the background run leaves it to the app', () async {
    final app = await open();
    await app.signIn(username: 'n.silva', password: 'correct-password');
    await app.queue.enqueue(sampleEvent());
    await app.queue.tryAcquireSyncLease(SyncEngine.syncLease, owner: 'the-open-app');
    expect(await BackgroundSync.run(open: open), isTrue);
    expect(gateway.pushes, 0);
  });

  test('a failure opening the services never escapes to WorkManager', () async {
    expect(await BackgroundSync.run(open: () async => throw StateError('keystore unavailable')), isTrue);
  });

  test('a finished background run tells the open app', () async {
    final app = await open();
    await app.signIn(username: 'n.silva', password: 'correct-password');
    final port = ReceivePort();
    IsolateNameServer.registerPortWithName(port.sendPort, BackgroundSync.portName);
    addTearDown(() {
      IsolateNameServer.removePortNameMapping(BackgroundSync.portName);
      port.close();
    });
    expect(await BackgroundSync.run(open: open), isTrue);
    expect(await port.first, 'success');
  });

  test('the open app screens show what a background run wrote through its own connection', () async {
    final app = await open();
    await app.queue.enqueue(sampleEvent());
    final seen = <int>[];
    final sub = app.queue.watchCounts().listen((c) => seen.add(c.of(QueueStatus.complete)));
    final stop = BackgroundSync.listen(app.db);
    addTearDown(() async {
      stop();
      await sub.cancel();
    });
    await Future<void>.delayed(QueueRepository.uiRefresh);
    expect(seen.last, 0);

    // A write this connection never hears about, as from the background isolate's own connection.
    await app.db.customStatement("UPDATE wound_event_queue SET status = 'complete'");
    await Future<void>.delayed(QueueRepository.uiRefresh * 2);
    expect(seen.last, 0, reason: 'Drift streams do not see it on their own');

    IsolateNameServer.lookupPortByName(BackgroundSync.portName)!.send('success');
    await Future<void>.delayed(QueueRepository.uiRefresh * 2);
    expect(seen.last, 1);
  });
}
