import 'dart:io';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';

import 'app/app_services.dart';
import 'features/assessment/media_store.dart';
import 'features/assessment/photo_source.dart';
import 'features/measurement/cv_wound_analyzer.dart';
import 'app/background_sync.dart';
import 'features/sync/ui/sign_in_screen.dart';
import 'app/app_shell.dart';
import 'core/theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // The real wound analysis (model on the phone, sticker as the scale) and the phone's camera; tests and the prototype use the samples.
  // Photos stay in the app's own storage on this phone, one file per assessment; they are never uploaded.
  final photos = Directory('${(await getApplicationSupportDirectory()).path}/photos');
  final services = await AppServices.open(analyzer: CvWoundAnalyzer(), photoSource: DevicePhotoSource(), media: FileMediaStore(photos));
  // The app lives as long as its process, so the listener is never cancelled.
  BackgroundSync.listen(services.db);
  runApp(WoundCdssApp(services: services));
  // We don't await this, so the first frame doesn't have to wait for WorkManager.
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
    // When the app comes back to the foreground, that's a good moment to sync (§6.2).
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
    scrollBehavior: const WoundScrollBehavior(),
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
