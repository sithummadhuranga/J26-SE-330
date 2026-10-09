import 'package:flutter/material.dart';

import 'app/app_services.dart';
import 'app/background_sync.dart';
import 'features/sync/ui/sign_in_screen.dart';
import 'app/app_shell.dart';
import 'core/theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final services = await AppServices.open();
  // The app lives as long as its process, so the listener is never cancelled.
  BackgroundSync.listen(services.db);
  runApp(WoundCdssApp(services: services));
  // Not awaited: the first frame must not wait on WorkManager.
  BackgroundSync.schedule().ignore();
}

class WoundCdssApp extends StatefulWidget {
  const WoundCdssApp({super.key, required this.services});

  final AppServices services;

  @override
  State<WoundCdssApp> createState() => _WoundCdssAppState();
}

class _WoundCdssAppState extends State<WoundCdssApp> {
  late final AppLifecycleListener _lifecycle;
  bool? _signedIn;

  @override
  void initState() {
    super.initState();
    // §6.2: the app coming back to the foreground is a sync trigger.
    _lifecycle = AppLifecycleListener(onResume: widget.services.scheduler.onForeground);
    widget.services.auth.hasSession().then((has) {
      if (!mounted) return;
      setState(() => _signedIn = has);
      widget.services.scheduler.start();
    });
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    widget.services.close();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'WoundAI',
        debugShowCheckedModeBanner: false,
        theme: woundTheme(),
        home: switch (_signedIn) {
          null => const Scaffold(body: Center(child: CircularProgressIndicator())),
          true => AppShell(services: widget.services, onSignedOut: () => setState(() => _signedIn = false)),
          false => SignInScreen(
              services: widget.services,
              onSignedIn: () {
                setState(() => _signedIn = true);
                widget.services.scheduler.syncNow();
              },
            ),
        },
      );
}
