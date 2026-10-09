import 'dart:io';
import 'dart:isolate';
import 'dart:ui';

import 'package:drift/drift.dart';
import 'package:flutter/widgets.dart';
import 'package:workmanager/workmanager.dart';

import '../features/sync/data/app_database.dart';
import 'app_services.dart';

/// Android background sync via WorkManager about every 15 minutes; leases keep it from clashing with the open app.
abstract final class BackgroundSync {
  static const task = 'wound-sync';
  static const frequency = Duration(minutes: 15); // WorkManager's minimum

  /// Where the open app listens for finished background runs (process-wide, see [listen]).
  static const portName = 'wound-sync-ran';

  /// Refreshes the open app after a background run (Drift can't see other connections' writes). Returns a canceller.
  static VoidCallback listen(AppDatabase db) {
    final port = ReceivePort();
    IsolateNameServer.removePortNameMapping(portName); // left over from a previous engine (hot restart)
    IsolateNameServer.registerPortWithName(port.sendPort, portName);
    port.listen((_) => db.notifyUpdates({for (final t in db.allTables) TableUpdate.onTable(t)}));
    return () {
      IsolateNameServer.removePortNameMapping(portName);
      port.close();
    };
  }

  /// Called once at start-up; keeps an already registered task as it is.
  static Future<void> schedule() async {
    if (!Platform.isAndroid) return;
    await Workmanager().initialize(backgroundSyncDispatcher);
    await Workmanager().registerPeriodicTask(
      task,
      task,
      frequency: frequency,
      constraints: Constraints(networkType: NetworkType.connected),
      existingWorkPolicy: ExistingPeriodicWorkPolicy.keep,
    );
  }

  /// One sync run; always returns true because the run already stored its own backoff. [open] is for tests.
  static Future<bool> run({Future<AppServices> Function()? open}) async {
    AppServices? services;
    try {
      services = await (open ?? () => AppServices.open(online: const Stream.empty()))();
      // Nobody signed in: nothing can be sent. The queue waits for the next sign-in (§7.3).
      if (!await services.auth.hasSession()) {
        debugPrint('background sync: not signed in');
      } else {
        final report = await services.engine.sync();
        debugPrint('background sync: ${report.outcome.name}, pushed ${report.pushed}, changes ${report.changes}');
        IsolateNameServer.lookupPortByName(portName)?.send(report.outcome.name);
      }
    } catch (e) {
      // Opening the keystore or the database failed; the next period tries again.
      debugPrint('background sync: could not run ($e)');
    } finally {
      await services?.close();
    }
    return true;
  }
}

/// WorkManager's entry point, in a fresh isolate.
@pragma('vm:entry-point')
void backgroundSyncDispatcher() {
  Workmanager().executeTask((task, input) async {
    WidgetsFlutterBinding.ensureInitialized();
    return BackgroundSync.run();
  });
}
