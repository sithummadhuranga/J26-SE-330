import 'package:drift/drift.dart';

/// The stages a record goes through on the phone:
/// pending → inFlight → accepted/rejected → adviceDeferred → complete (or superseded).
enum QueueStatus { pending, inFlight, accepted, rejected, adviceDeferred, complete, superseded }

/// The offline queue. There's one row per event revision, and only QueueRepository writes to it.
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

/// Holds the latest revision of each assessment, so clinicians can look through history offline.
class AssessmentLocal extends Table {
  TextColumn get assessmentId => text()();
  IntColumn get latestRevision => integer()();
  TextColumn get summaryJson => text()();

  @override
  Set<Column> get primaryKey => {assessmentId};
}

/// Advice from the server (RECOMMENDATION_READY, §7.2), stored exactly as it arrived.
class RecommendationLocal extends Table {
  TextColumn get assessmentId => text()();
  IntColumn get revision => integer()();
  TextColumn get payloadJson => text()();
  TextColumn get mode => text()();
  DateTimeColumn get receivedAt => dateTime()();

  @override
  Set<Column> get primaryKey => {assessmentId, revision};
}

/// Cached guideline figures. They survive restarts and never go out of date.
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

/// Patients on this phone, each with a pseudonymous ref and a label chosen on the phone.
/// [aliasSyncedAt] stays null until the label has reached the server.
class PatientLocal extends Table {
  TextColumn get patientRef => text()();
  TextColumn get displayAlias => text()();
  DateTimeColumn get createdAt => dateTime()();
  DateTimeColumn get aliasSyncedAt => dateTime().nullable()();

  @override
  Set<Column> get primaryKey => {patientRef};
}

/// A small key/value table for things like server_cursor, last_success_at, device_id and the sync lease
/// (see SyncStateKeys).
class SyncState extends Table {
  TextColumn get key => text()();
  TextColumn get value => text()();

  @override
  Set<Column> get primaryKey => {key};
}

/// Remembers who is signed in. The refresh token itself is never stored here; it lives in the keystore (§6, §12).
class AuthLocal extends Table {
  TextColumn get clinicianId => text()();
  TextColumn get username => text()();
  TextColumn get role => text()();
  TextColumn get facilityId => text()();
  DateTimeColumn get accessTokenExpiresAt => dateTime()();

  @override
  Set<Column> get primaryKey => {clinicianId};
}
