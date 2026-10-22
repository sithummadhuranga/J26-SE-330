import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:sqlite3/sqlite3.dart' as sqlite;

import 'tables.dart';

part 'app_database.g.dart';

/// The keys we use in the sync_state table.
abstract final class SyncStateKeys {
  static const serverCursor = 'server_cursor';
  static const lastSuccessAt = 'last_success_at';
  static const deviceId = 'device_id';
  static const nextAttemptAt = 'next_attempt_at';

  /// When the current sync run's lease runs out. This keeps two isolates from syncing at once (§6.2).
  static const syncLeaseUntil = 'sync_lease_until';

  /// Which run holds the lease, so each run only renews or releases its own.
  static const syncLeaseOwner = 'sync_lease_owner';
}

/// The phone's database, encrypted with SQLCipher. Its key is kept in the keystore.
@DriftDatabase(tables: [
  WoundEventQueue,
  AssessmentLocal,
  RecommendationLocal,
  FiguresLocal,
  SyncState,
  AuthLocal,
  PatientLocal,
  ChatLocal, // DRAFT (Member 3)
])
class AppDatabase extends _$AppDatabase {
  AppDatabase(super.executor);

  /// Opens the encrypted database, creating it if needed, on a background isolate so the UI never waits.
  factory AppDatabase.encrypted(File file, String hexKey) => AppDatabase(
        NativeDatabase.createInBackground(file, setup: (raw) => applyKey(raw, hexKey)),
      );

  /// An in-memory database for tests. It isn't encrypted.
  factory AppDatabase.inMemory() => AppDatabase(NativeDatabase.memory());

  /// Sets the raw key and reads once, so a wrong key fails straight away. The busy timeout lets
  /// two connections share the file.
  static void applyKey(sqlite.Database raw, String hexKey) {
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(hexKey)) {
      throw ArgumentError('The database key must be 64 lower-case hex characters (32 bytes).');
    }
    raw.execute("PRAGMA key = \"x'$hexKey'\"");
    raw.execute('SELECT count(*) FROM sqlite_master');
    raw.execute('PRAGMA busy_timeout = 5000');
  }

  /// Version 2 added patient_local, for the Patients tab. Version 3 adds chat_local (DRAFT, Member 3's follow-up chat).
  @override
  int get schemaVersion => 3;

  @override
  MigrationStrategy get migration => MigrationStrategy(
        onUpgrade: (m, from, to) async {
          if (from < 2) await m.createTable(patientLocal);
          if (from < 3) await m.createTable(chatLocal);
        },
      );
}
