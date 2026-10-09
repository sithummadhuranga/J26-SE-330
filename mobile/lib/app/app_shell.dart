import 'package:flutter/material.dart';

import '../core/theme.dart';
import '../features/history/history_screen.dart';
import '../features/home/home_screen.dart';
import '../features/patients/patients_screen.dart';
import '../features/profile/profile_screen.dart';
import '../features/sync/engine/sync_scheduler.dart';
import '../features/sync/ui/sign_in_screen.dart';
import 'app_services.dart';
import 'connection.dart';

/// The signed-in app with Home, Patients, History and Profile tabs, plus an offline banner.
class AppShell extends StatefulWidget {
  const AppShell({super.key, required this.services, required this.onSignedOut});

  final AppServices services;
  final VoidCallback onSignedOut;

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  int _tab = 0;

  void _goTo(int tab) => setState(() => _tab = tab);

  @override
  Widget build(BuildContext context) {
    final s = widget.services;
    final pages = [
      HomeScreen(services: s, onViewAll: () => _goTo(2)),
      PatientsScreen(services: s),
      HistoryScreen(services: s),
      ProfileScreen(services: s, onSignedOut: widget.onSignedOut),
    ];
    return Scaffold(
      body: ValueListenableBuilder<SyncStatus>(
        valueListenable: s.scheduler.status,
        builder: (context, status, _) {
          final connection = connectionOf(status);
          final banner = _OfflineBanner.textFor(connection) != null;
          return Column(
            children: [
              _OfflineBanner(
                connection: connection,
                onTap: connection == Connection.signInNeeded ? () => SignInScreen.again(context, s) : null,
              ),
              // The banner already sits under the status bar; the page below must not pad for it again.
              Expanded(
                child: MediaQuery.removePadding(
                  context: context,
                  removeTop: banner,
                  child: IndexedStack(index: _tab, children: pages),
                ),
              ),
            ],
          );
        },
      ),
      bottomNavigationBar: DecoratedBox(
        decoration: const BoxDecoration(
          border: Border(top: BorderSide(color: WoundColors.border)),
        ),
        child: NavigationBar(
          selectedIndex: _tab,
          onDestinationSelected: _goTo,
          destinations: const [
            NavigationDestination(
              icon: Icon(Icons.home_outlined),
              selectedIcon: Icon(Icons.home_rounded),
              label: 'Home',
            ),
            NavigationDestination(
              icon: Icon(Icons.people_outline_rounded),
              selectedIcon: Icon(Icons.people_rounded),
              label: 'Patients',
            ),
            NavigationDestination(
              icon: Icon(Icons.history_rounded),
              selectedIcon: Icon(Icons.history_rounded),
              label: 'History',
            ),
            NavigationDestination(
              icon: Icon(Icons.person_outline_rounded),
              selectedIcon: Icon(Icons.person_rounded),
              label: 'Profile',
            ),
          ],
        ),
      ),
    );
  }
}

class _OfflineBanner extends StatelessWidget {
  const _OfflineBanner({required this.connection, this.onTap});

  final Connection connection;
  final VoidCallback? onTap;

  static String? textFor(Connection c) => switch (c) {
    Connection.offline => 'Offline — capture still works; saved assessments sync when you are back online',
    Connection.signInNeeded => 'Signed out of sync — your saved assessments are safe. Tap to sign in again',
    _ => null,
  };

  @override
  Widget build(BuildContext context) {
    final text = textFor(connection);
    return AnimatedSize(
      duration: const Duration(milliseconds: 200),
      child: text == null
          ? const SizedBox(width: double.infinity)
          : GestureDetector(
              onTap: onTap,
              child: Container(
                key: const Key('offlineBanner'),
                width: double.infinity,
                color: WoundColors.warningSoft,
                padding: EdgeInsets.fromLTRB(16, MediaQuery.paddingOf(context).top + 8, 16, 8),
                child: Row(
                  children: [
                    const Icon(Icons.wifi_off_rounded, size: 15, color: WoundColors.warning),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        text,
                        style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600, color: WoundColors.warning),
                      ),
                    ),
                  ],
                ),
              ),
            ),
    );
  }
}
