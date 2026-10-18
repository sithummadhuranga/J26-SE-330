import 'package:flutter/material.dart';

import '../../../core/theme.dart';
import 'demo_home_screen.dart';

/// A separate start-up for the wound measurement demo; it needs no sign-in and no backend.
///
///     flutter run -t lib/features/measurement/demo/measurement_demo_main.dart --release -d <phone>
///
/// The model file is not part of the app (it comes from DFUC2022 training); copy it to the phone with
/// research/IT23294066/on-device-export/push_demo_files.sh.
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const MeasurementDemoApp());
}

class MeasurementDemoApp extends StatelessWidget {
  const MeasurementDemoApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'Wound measurement demo',
        debugShowCheckedModeBanner: false,
        theme: woundTheme(),
        home: const DemoHomeScreen(),
      );
}
