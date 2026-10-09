import 'package:drift/drift.dart';

/// Device record lifecycle: pending → inFlight → accepted/rejected → adviceDeferred → complete (or superseded).
enum QueueStatus { pending, inFlight, accepted, rejected, adviceDeferred, complete, superseded }

/// The offline queue: one row per event revision, written only through QueueRepository.
@DataClassName('QueuedEvent')
@TableIndex(name: 'ix_queue_status_next', columns: {#status, #nextAttemptAt})
@TableIndex(name: 'ix_queue_assessment', columns: {#assessmentId, #revision})
class WoundEventQueue extends Table {
  @override
  String get tableName => 'wound_event_queue';

  TextColumn get eventId => text()();
  TextColumn get assessmentId => text()();
  IntColumn get revision => integer()();
  TextColumn get payloadJson => text()();
  IntColumn get sizeBytes => integer()();
  TextColumn get status => textEnum<QueueStatus>()();
  IntColumn get attemptCount => integer().withDefault(const Constant(0))();
  DateTimeColumn get nextAttemptAt => dateTime().nullable()();
  DateTimeColumn get leaseExpiresAt => dateTime().nullable()();
  TextColumn get lastError => text().nullable()();
  DateTimeColumn get createdAt => dateTime()();
  DateTimeColumn get ackedAt => dateTime().nullable()();
  DateTimeColumn get completedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {eventId};
}

/// Read model so clinicians can review history offline. Latest revision per assessment.
class AssessmentLocal extends Table {
  TextColumn get assessmentId => text()();
  IntColumn get latestRevision => integer()();
  TextColumn get summaryJson => text()();

  @override
  Set<Column> get primaryKey => {assessmentId};
}

/// Advice received from the server (§7.2 RECOMMENDATION_READY), stored as delivered.
class RecommendationLocal extends Table {
  TextColumn get assessmentId => text()();
  IntColumn get revision => integer()();
  TextColumn get payloadJson => text()();
  TextColumn get mode => text()();
  DateTimeColumn get receivedAt => dateTime()();

  @override
  Set<Column> get primaryKey => {assessmentId, revision};
}

/// Cached guideline figures, kept across restarts; they never go stale.
class FiguresLocal extends Table {
  TextColumn get corpusVersion => text()();
  TextColumn get figureId => text()();
  BlobColumn get bytes => blob()();
  TextColumn get contentType => text()();
  TextColumn get licence => text()();
  TextColumn get attribution => text()();
  TextColumn get etag => text().nullable()();
  DateTimeColumn get cachedAt => dateTime()();

  @override
  Set<Column> get primaryKey => {corpusVersion, figureId};
}

/// Patients on this phone: pseudonymous ref plus a local label; [aliasSyncedAt] is null until the label reaches the server.
class PatientLocal extends Table {
  TextColumn get patientRef => text()();
  TextColumn get displayAlias => text()();
  DateTimeColumn get createdAt => dateTime()();
  DateTimeColumn get aliasSyncedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {patientRef};
}

/// Small key/value table: server_cursor, last_success_at, device_id, the sync lease (see SyncStateKeys).
class SyncState extends Table {
  TextColumn get key => text()();
  TextColumn get value => text()();

  @override
  Set<Column> get primaryKey => {key};
}

/// Cached clinician session state. Never the raw refresh token: that lives in the platform keystore (§6, §12).
class AuthLocal extends Table {
  TextColumn get clinicianId => text()();
  TextColumn get username => text()();
  TextColumn get role => text()();
  TextColumn get facilityId => text()();
  DateTimeColumn get accessTokenExpiresAt => dateTime()();

  @override
  Set<Column> get primaryKey => {clinicianId};
}
