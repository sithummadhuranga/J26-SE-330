import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../app/connection.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/app_database.dart';
import '../sync/data/queue_repository.dart';
import '../sync/engine/sync_scheduler.dart';
import '../sync/ui/sign_in_screen.dart';

class ProfileScreen extends StatelessWidget {
  const ProfileScreen({super.key, required this.services, required this.onSignedOut});

  final AppServices services;
  final VoidCallback onSignedOut;

  static String roleLabel(String role) => switch (role) {
    'nurse' => 'Nurse',
    'wound_specialist' => 'Wound specialist',
    'admin' => 'Admin',
    _ => role,
  };

  Future<void> _info(BuildContext context, String title, String body) => showDialog<void>(
    context: context,
    builder: (context) => AlertDialog(
      backgroundColor: WoundColors.surface,
      title: Text(title),
      content: Text(body, style: const TextStyle(height: 1.5)),
      actions: [TextButton(onPressed: () => Navigator.pop(context), child: const Text('Close'))],
    ),
  );

  @override
  Widget build(BuildContext context) => SafeArea(
    child: ListView(
      padding: const EdgeInsets.fromLTRB(20, 22, 20, 24),
      children: [
        FutureBuilder<AuthLocalData?>(
          future: services.cachedClinician(),
          builder: (context, snap) {
            final who = snap.data;
            return Row(
              children: [
                Container(
                  width: 54,
                  height: 54,
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    gradient: const LinearGradient(colors: [WoundColors.accentSoft, Colors.white]),
                    borderRadius: BorderRadius.circular(16),
                    boxShadow: WoundShadows.card,
                  ),
                  child: Text(
                    InitialsAvatar.initialsOf(who?.username ?? '?'),
                    style: const TextStyle(color: WoundColors.accentDark, fontWeight: FontWeight.w800, fontSize: 18),
                  ),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        who?.username ?? 'Not signed in',
                        key: const Key('profileName'),
                        style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        who == null ? '' : '${roleLabel(who.role)} · Facility ${who.facilityId}',
                        style: Theme.of(context).textTheme.bodySmall,
                      ),
                    ],
                  ),
                ),
              ],
            );
          },
        ),
        const SizedBox(height: 26),
        const SectionTitle('Settings'),
        WoundCard(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 4),
          child: Column(
            children: [
              ValueListenableBuilder<SyncStatus>(
                valueListenable: services.scheduler.status,
                builder: (context, status, _) => StreamBuilder<QueueCounts>(
                  stream: services.queue.watchCounts(),
                  builder: (context, counts) {
                    final waiting = counts.data?.waitingToSend ?? 0;
                    return _SettingRow(
                      icon: Icons.storage_rounded,
                      label: 'Data synchronization',
                      sub:
                          '${syncSummary(status)}${waiting > 0 ? ' · $waiting waiting on this phone' : ''}'
                          '${status.lastRunAt == null ? '' : ' · checked ${formatTime(status.lastRunAt!)}'}',
                      trailing: status.running
                          ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
                          : connectionOf(status) == Connection.signInNeeded
                          ? TextButton(
                              key: const Key('signInAgain'),
                              onPressed: () => SignInScreen.again(context, services),
                              child: const Text('Sign in'),
                            )
                          : TextButton(
                              key: const Key('syncNow'),
                              onPressed: services.scheduler.syncNow,
                              child: const Text('Sync now'),
                            ),
                    );
                  },
                ),
              ),
              const Divider(),
              _SettingRow(
                icon: Icons.shield_outlined,
                label: 'Privacy',
                onTap: () => _info(
                  context,
                  'Privacy',
                  'Wound images stay on this phone. Only measurements and your clinical answers are synced, under a '
                      'pseudonymous patient ID; no names or images reach the guidance service. Saved assessments are '
                      'encrypted on the phone until the server confirms them.',
                ),
              ),
              const Divider(),
              _SettingRow(
                icon: Icons.info_outline_rounded,
                label: 'About',
                onTap: () => _info(
                  context,
                  'About WoundAI',
                  'Clinical wound assessment support for melanin-rich skin (project J26-SE-330). '
                      'Research prototype — decision support only; clinical decisions remain with the clinician.\n\n'
                      'Server: $syncBaseUrl',
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 22),
        OutlinedButton.icon(
          key: const Key('signOut'),
          onPressed: () async {
            if (!await _confirmSignOut(context, services)) return;
            await services.signOut();
            onSignedOut();
          },
          icon: const Icon(Icons.logout_rounded, size: 18),
          label: const Text('Sign out'),
        ),
      ],
    ),
  );
}

/// Warns before signing out with unsynced assessments; true if the sign-out should go ahead.
Future<bool> _confirmSignOut(BuildContext context, AppServices services) async {
  final waiting = (await services.queue.counts()).waitingToSend;
  if (waiting == 0 || !context.mounted) return true;
  final choice = await showDialog<String>(
    context: context,
    builder: (dialog) => AlertDialog(
      title: const Text('Not synced yet'),
      content: Text(
        '$waiting ${waiting == 1 ? 'assessment has' : 'assessments have'} not reached the server yet. '
        '${waiting == 1 ? 'It stays' : 'They stay'} saved on this phone and ${waiting == 1 ? 'is' : 'are'} sent after '
        'the next sign-in.',
        key: const Key('signOutWaiting'),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(dialog).pop('cancel'), child: const Text('Cancel')),
        TextButton(
            key: const Key('signOutSyncFirst'),
            onPressed: () => Navigator.of(dialog).pop('sync'),
            child: const Text('Sync now')),
        TextButton(
            key: const Key('signOutAnyway'),
            onPressed: () => Navigator.of(dialog).pop('signOut'),
            child: const Text('Sign out anyway')),
      ],
    ),
  );
  if (choice == 'sync') await services.scheduler.syncNow();
  return choice == 'signOut';
}

class _SettingRow extends StatelessWidget {
  const _SettingRow({required this.icon, required this.label, this.sub, this.trailing, this.onTap});

  final IconData icon;
  final String label;
  final String? sub;
  final Widget? trailing;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => InkWell(
    onTap: onTap,
    child: Padding(
      padding: const EdgeInsets.symmetric(vertical: 12),
      child: Row(
        children: [
          Icon(icon, size: 20, color: WoundColors.textSecondary),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(label, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14)),
                if (sub != null) ...[
                  const SizedBox(height: 2),
                  Text(sub!, key: Key('setting-$label'), style: Theme.of(context).textTheme.bodySmall),
                ],
              ],
            ),
          ),
          trailing ??
              (onTap == null
                  ? const SizedBox.shrink()
                  : const Icon(Icons.chevron_right_rounded, color: WoundColors.textTertiary)),
        ],
      ),
    ),
  );
}
