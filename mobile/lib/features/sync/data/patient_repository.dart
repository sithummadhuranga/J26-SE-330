import 'dart:math';

import 'package:drift/drift.dart';

import '../api/sync_api.dart';
import 'app_database.dart';

/// Patients on this phone: a pseudonymous ref plus a local label that never enters the sync pipeline.
class PatientRepository {
  PatientRepository(this._db, this._api, {DateTime Function()? clock, Random? random})
    : _now = clock ?? DateTime.now,
      _random = random ?? Random.secure();

  final AppDatabase _db;
  final SyncApi _api;
  final DateTime Function() _now;
  final Random _random;

  /// Server rule (PatientEndpoints.MaxAliasLength).
  static const maxAliasLength = 64;

  /// `p-` and 12 random hex characters: the contract's patientRef pattern (^p-[0-9a-f]{6,32}$), no identity in it.
  String newPatientRef() => 'p-${List.generate(12, (_) => _random.nextInt(16).toRadixString(16)).join()}';

  Stream<List<PatientLocalData>> watchAll() =>
      (_db.select(_db.patientLocal)..orderBy([(p) => OrderingTerm.asc(p.displayAlias)])).watch();

  Future<PatientLocalData?> byRef(String patientRef) =>
      (_db.select(_db.patientLocal)..where((p) => p.patientRef.equals(patientRef))).getSingleOrNull();

  /// Adds a patient and returns it. The alias is trimmed; empty or over-long aliases are refused.
  Future<PatientLocalData> create(String alias) async {
    final trimmed = alias.trim();
    if (trimmed.isEmpty) throw ArgumentError('Enter a label for the patient.');
    if (trimmed.length > maxAliasLength) throw ArgumentError('Keep the label to $maxAliasLength characters.');
    final row = PatientLocalCompanion.insert(patientRef: newPatientRef(), displayAlias: trimmed, createdAt: _now());
    await _db.into(_db.patientLocal).insert(row);
    return (await byRef(row.patientRef.value))!;
  }

  /// After a sync, uploads aliases the server doesn't have yet; stops on the first failure. Returns how many were stored.
  Future<int> pushAliases(String accessToken) async {
    final pending = await (_db.select(_db.patientLocal)..where((p) => p.aliasSyncedAt.isNull())).get();
    var stored = 0;
    for (final p in pending) {
      try {
        await _api.setPatientAlias(accessToken, p.patientRef, p.displayAlias);
      } on ApiException catch (e) {
        // 400/404: this record can never be stored (bad ref, another facility's patient); do not retry it forever.
        if (e.status != 400 && e.status != 404) break;
      } on TransportException {
        break;
      }
      await (_db.update(
        _db.patientLocal,
      )..where((r) => r.patientRef.equals(p.patientRef))).write(PatientLocalCompanion(aliasSyncedAt: Value(_now())));
      stored++;
    }
    return stored;
  }
}
