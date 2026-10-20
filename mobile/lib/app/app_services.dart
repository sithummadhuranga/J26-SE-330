import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';

import '../features/sync/api/sync_api.dart';
import '../features/sync/auth/auth_session.dart';
import '../features/sync/auth/browser_sign_in.dart';
import '../features/sync/data/app_database.dart';
import '../features/sync/data/figure_repository.dart';
import '../features/sync/data/patient_repository.dart';
import '../features/sync/data/queue_repository.dart';
import '../features/sync/engine/sync_engine.dart';
import '../features/sync/engine/sync_scheduler.dart';

/// The backend address. Set it with --dart-define=SYNC_BASE_URL=... ; by default it points at the
/// emulator's view of your computer.
const syncBaseUrl = String.fromEnvironment('SYNC_BASE_URL', defaultValue: 'http://10.0.2.2:8080');

/// The sync services, set up once when the app starts. To save, write to [queue] and then call
/// [scheduler].onSaved(). Use [figures] to show guideline figures.
class AppServices {
  AppServices({required this.db, required this.queue, required this.figures, required this.patients, required this.api,
      required this.auth, required this.engine, required this.scheduler});

  final AppDatabase db;
  final QueueRepository queue;
  final FigureRepository figures;
  final PatientRepository patients;
  final SyncApi api;
  final AuthSession auth;
  final SyncEngine engine;
  final SyncScheduler scheduler;

  /// The setup for a real phone: encrypted database, Keystore secrets and real connectivity. Tests pass fakes instead.
  static Future<AppServices> open({
    AppDatabase? db,
    SecretStore? secrets,
    SyncApi? api,
    BrowserSignIn? browser,
    Stream<bool>? online,
    DateTime Function()? clock,
  }) async {
    final store = secrets ?? const KeystoreSecretStore();
    final database = db ??
        AppDatabase.encrypted(
          File(p.join((await getApplicationSupportDirectory()).path, 'wound_queue.db')),
          await DatabaseKeyStore.getOrCreate(store),
        );
    final queue = QueueRepository(database, clock: clock);
    // If the app was killed mid-sync, some rows are still leased. Put them back to pending now (§6.2).
    await queue.releaseExpiredLeases();

    final client = api ?? HttpSyncApi(Uri.parse(syncBaseUrl));
    final auth = AuthSession(client, store, browser: browser ?? AppAuthSignIn(Uri.parse(syncBaseUrl)), clock: clock);
    final figures = FigureRepository(database, client, auth, clock: clock);
    final patients = PatientRepository(database, client, clock: clock);
    final engine =
        SyncEngine(queue: queue, api: client, auth: auth, figures: figures, patients: patients, clock: clock);
    final scheduler = SyncScheduler(engine.sync,
        online: online ??
            Connectivity().onConnectivityChanged.map((r) => r.any((c) => c != ConnectivityResult.none)),
        clock: clock);
    return AppServices(
        db: database,
        queue: queue,
        figures: figures,
        patients: patients,
        api: client,
        auth: auth,
        engine: engine,
        scheduler: scheduler);
  }

  /// Signs in on the identity service's page with this phone's device id, and remembers who signed in
  /// so we can show it offline. Throws [SignInCancelled] if the clinician closes the page.
  Future<Clinician> signIn() async {
    final clinician = await auth.signIn(deviceId: await queue.deviceId());
    await db.delete(db.authLocal).go();
    await db.into(db.authLocal).insert(AuthLocalCompanion.insert(
          clinicianId: clinician.id,
          username: clinician.username,
          role: clinician.role,
          facilityId: clinician.facilityId,
          accessTokenExpiresAt: DateTime.now().add(const Duration(minutes: 15)),
        ));
    return clinician;
  }

  /// Who signed in last. This works offline.
  Future<AuthLocalData?> cachedClinician() => db.select(db.authLocal).getSingleOrNull();

  Future<void> signOut() async {
    await auth.signOut();
    await db.delete(db.authLocal).go();
  }

  Future<void> close() async {
    scheduler.dispose();
    await db.close();
  }
}
