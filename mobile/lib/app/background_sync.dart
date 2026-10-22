import 'dart:io';
import 'dart:isolate';
import 'dart:ui';

import 'package:drift/drift.dart';
import 'package:flutter/widgets.dart';
import 'package:workmanager/workmanager.dart';

import '../features/sync/data/app_database.dart';
import 'app_services.dart';

/// Syncs in the background on Android through WorkManager, roughly every 15 minutes.
/// Leases stop it from clashing with the app when it's open.
abstract final class BackgroundSync {
  static const task = 'wound-sync';
  static const frequency = Duration(minutes: 15); // WorkManager's minimum

  /// Where the open app listens for background runs that have finished.
  /// It's shared by the whole process (see [listen]).
  static const portName = 'wound-sync-ran';

  /// Refreshes the open app after a background run, because Drift can't see writes from other connections.
  /// Returns a function that stops listening.
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

  /// Called once when the app starts. If the task is already registered, it's left as it is.
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

  /// Runs one sync. It always returns true, because the run has already saved its own backoff.
  /// [open] is only there for tests.
  static Future<bool> run({Future<AppServices> Function()? open}) async {
    AppServices? services;
    try {
      services = await (open ?? () => AppServices.open(online: const Stream.empty()))();
      // Nobody is signed in, so nothing can be sent. The queue waits until someone signs in (§7.3).
      if (!await services.auth.hasSession()) {
        debugPrint('background sync: not signed in');
      } else {
        final report = await services.engine.sync();
        debugPrint('background sync: ${report.outcome.name}, pushed ${report.pushed}, changes ${report.changes}');
        IsolateNameServer.lookupPortByName(portName)?.send(report.outcome.name);
      }
    } catch (e) {
      // We couldn't open the keystore or the database. The next scheduled run will try again.
      debugPrint('background sync: could not run ($e)');
    } finally {
      await services?.close();
    }
    return true;
  }
}

/// Where WorkManager starts us, in a brand new isolate.
@pragma('vm:entry-point')
void backgroundSyncDispatcher() {
  Workmanager().executeTask((task, input) async {
    WidgetsFlutterBinding.ensureInitialized();
    return BackgroundSync.run();
  });
}
