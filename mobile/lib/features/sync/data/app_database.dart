import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:sqlite3/sqlite3.dart' as sqlite;

import 'tables.dart';

part 'app_database.g.dart';

/// Keys used in the sync_state table.
abstract final class SyncStateKeys {
  static const serverCursor = 'server_cursor';
  static const lastSuccessAt = 'last_success_at';
  static const deviceId = 'device_id';
  static const nextAttemptAt = 'next_attempt_at';

  /// Single flight across isolates (§6.2): the time until which a sync run holds the lease.
  static const syncLeaseUntil = 'sync_lease_until';

  /// Which run holds the lease, so a run only renews or releases its own.
  static const syncLeaseOwner = 'sync_lease_owner';
}

/// The SQLCipher-encrypted device database; its key lives in the platform keystore.
@DriftDatabase(tables: [
  WoundEventQueue,
  AssessmentLocal,
  RecommendationLocal,
  FiguresLocal,
  SyncState,
  AuthLocal,
  PatientLocal,
])
class AppDatabase extends _$AppDatabase {
  AppDatabase(super.executor);

  /// Opens or creates the encrypted database on a background isolate so the UI never blocks.
  factory AppDatabase.encrypted(File file, String hexKey) => AppDatabase(
        NativeDatabase.createInBackground(file, setup: (raw) => applyKey(raw, hexKey)),
      );

  /// For tests: an in-memory database (not encrypted).
  factory AppDatabase.inMemory() => AppDatabase(NativeDatabase.memory());

  /// Use the raw key, read once to fail fast, and set a busy timeout so two connections can share the file.
  static void applyKey(sqlite.Database raw, String hexKey) {
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(hexKey)) {
      throw ArgumentError('The database key must be 64 lower-case hex characters (32 bytes).');
    }
    raw.execute("PRAGMA key = \"x'$hexKey'\"");
    raw.execute('SELECT count(*) FROM sqlite_master');
    raw.execute('PRAGMA busy_timeout = 5000');
  }

  /// 2: patient_local (the Patients tab).
  @override
  int get schemaVersion => 2;

  @override
  MigrationStrategy get migration => MigrationStrategy(
        onUpgrade: (m, from, to) async {
          if (from < 2) await m.createTable(patientLocal);
        },
      );
}
