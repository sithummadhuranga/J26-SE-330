import 'package:flutter/material.dart';

import '../core/theme.dart';
import '../features/sync/engine/sync_engine.dart';
import '../features/sync/engine/sync_scheduler.dart';

enum Connection { connected, syncing, offline, signInNeeded, unknown }

/// What the last sync run says about the link to the server (§6.2: reachability, not just "on a network").
Connection connectionOf(SyncStatus s) {
  if (s.running) return Connection.syncing;
  return switch (s.last?.outcome) {
    null => Connection.unknown,
    SyncOutcome.success || SyncOutcome.alreadyRunning => Connection.connected,
    SyncOutcome.offline || SyncOutcome.serverBusy || SyncOutcome.backingOff => Connection.offline,
    SyncOutcome.needsSignIn => Connection.signInNeeded,
  };
}

String connectionLabel(Connection c) => switch (c) {
  Connection.connected => 'Connected',
  Connection.syncing => 'Syncing',
  Connection.offline => 'Offline',
  Connection.signInNeeded => 'Sign in to sync',
  Connection.unknown => 'Not synced yet',
};

/// "Up to date", "Waiting to sync", ...: the line under "Data synchronization".
String syncSummary(SyncStatus s) => switch (connectionOf(s)) {
  Connection.connected => 'Up to date',
  Connection.syncing => 'Syncing…',
  Connection.offline => 'Waiting to sync',
  Connection.signInNeeded => 'Sign in again to sync',
  Connection.unknown => 'Not synced yet',
};

/// The rounded pill in the Home header.
class ConnectionPill extends StatelessWidget {
  const ConnectionPill({super.key, required this.connection, this.onTap});

  final Connection connection;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final (icon, fg, bg) = switch (connection) {
      Connection.connected => (Icons.wifi_rounded, WoundColors.success, WoundColors.successSoft),
      Connection.syncing => (Icons.sync_rounded, WoundColors.accentDark, WoundColors.accentSoft),
      Connection.offline => (Icons.wifi_off_rounded, WoundColors.warning, WoundColors.warningSoft),
      Connection.signInNeeded => (Icons.lock_outline_rounded, WoundColors.error, WoundColors.errorSoft),
      Connection.unknown => (Icons.cloud_queue_rounded, WoundColors.textSecondary, WoundColors.surfaceAlt),
    };
    return Material(
      color: bg,
      borderRadius: BorderRadius.circular(20),
      child: InkWell(
        key: const Key('connectionPill'),
        borderRadius: BorderRadius.circular(20),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 6),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 14, color: fg),
              const SizedBox(width: 5),
              Text(
                connectionLabel(connection),
                style: TextStyle(color: fg, fontSize: 12, fontWeight: FontWeight.w700),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
