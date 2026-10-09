import 'dart:async';
import 'dart:io';
import 'dart:isolate';

import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/queue_repository.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';

import 'support/fakes.dart';

/// Saves 1,000 events and kills the isolate mid-sync to simulate the app dying.
Future<void> _saveThenDie((String path, String key, SendPort done) args) async {
  final (path, key, done) = args;
  final db = AppDatabase(NativeDatabase(File(path), setup: (raw) => AppDatabase.applyKey(raw, key)));
  final queue = QueueRepository(db);
  for (var i = 0; i < 1000; i++) {
    await queue.enqueue(sampleEvent());
  }
  await queue.leaseBatch(); // 50 rows in flight when the "app" dies
  done.send('saved');
  await Completer<void>().future; // never closes the database: killed from outside
}

void main() {
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('cdss-queue'));
  tearDown(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('§14.1 step 3: 1,000 offline saves survive the app being killed mid-sync', () async {
    final path = '${dir.path}/queue.db';
    final key = await DatabaseKeyStore.getOrCreate(MemorySecretStore());
    final done = ReceivePort();
    final isolate = await Isolate.spawn(_saveThenDie, (path, key, done.sendPort));
    expect(await done.first, 'saved');
    isolate.kill(priority: Isolate.immediate);

    // Next app start: same file, same key from the keystore.
    var now = DateTime.now();
    final db = AppDatabase.encrypted(File(path), key);
    final queue = QueueRepository(db, clock: () => now);
    expect((await queue.counts()).total, 1000);
    expect((await queue.counts()).of(QueueStatus.inFlight), 50, reason: 'the batch the dead app had leased');

    now = now.add(QueueRepository.lease + const Duration(seconds: 1));
    await queue.releaseExpiredLeases();
    expect((await queue.counts()).of(QueueStatus.pending), 1000, reason: 'nothing lost, nothing stuck');
    await db.close();
  });

  test('§12: the queue file is encrypted at rest', () async {
    final file = File('${dir.path}/enc.db');
    final secrets = MemorySecretStore();
    final key = await DatabaseKeyStore.getOrCreate(secrets);
    expect(key, matches(RegExp(r'^[0-9a-f]{64}$')));
    expect(await DatabaseKeyStore.getOrCreate(secrets), key, reason: 'one key per install');

    final db = AppDatabase.encrypted(file, key);
    await QueueRepository(db).enqueue(sampleEvent()..['patientRef'] = 'p-5ec7e70a');
    await db.close();

    final bytes = file.readAsBytesSync();
    expect(String.fromCharCodes(bytes).contains('p-5ec7e70a'), isFalse, reason: 'no plaintext in the file');
    expect(String.fromCharCodes(bytes.take(15)), isNot('SQLite format 3'), reason: 'not a plain SQLite file');

    final wrong = AppDatabase.encrypted(file, 'f' * 64);
    await expectLater(QueueRepository(wrong).counts(), throwsA(anything), reason: 'a wrong key cannot open it');
    await wrong.close();

    final right = AppDatabase.encrypted(file, key);
    expect((await QueueRepository(right).counts()).total, 1, reason: 'the right key reopens it');
    await right.close();
  });
}
