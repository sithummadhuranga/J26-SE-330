import 'package:integration_test/integration_test_driver.dart';

/// For `flutter drive`: writes each test's reportData (the frame timings) to build/integration_response_data.json.
Future<void> main() => integrationDriver();
