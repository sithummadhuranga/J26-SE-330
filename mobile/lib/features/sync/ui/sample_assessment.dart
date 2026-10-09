import 'dart:math';

import 'package:uuid/uuid.dart';

/// Fake analytics in the wound-event shape until the real capture and measurement pipeline exists.
Map<String, dynamic> sampleAnalytics({Random? random}) {
  final rng = random ?? Random();
  final first = double.parse((40 + rng.nextDouble() * 40).toStringAsFixed(1));
  final second = double.parse((min(95 - first, 20 + rng.nextDouble() * 25)).toStringAsFixed(1));
  final third = double.parse((100 - first - second).toStringAsFixed(1));
  return {
    'areaMm2': double.parse((200 + rng.nextDouble() * 400).toStringAsFixed(1)),
    'colourRegions': [
      {'cluster': 1, 'percent': first},
      {'cluster': 2, 'percent': second},
      if (third > 0) {'cluster': 3, 'percent': third},
    ],
    'fitzpatrickClass': ['IV', 'V', 'VI'][rng.nextInt(3)],
    'pipeline': {'calibration': '2.1.0', 'segmentation': 'yolo11n-seg-0.4'},
  };
}

/// A valid synthetic assessment for testing sync before the capture screens exist.
Map<String, dynamic> sampleAssessment({required String deviceId, required String facilityId, Random? random}) {
  final rng = random ?? Random();
  const triState = ['present', 'absent', 'not_recorded'];
  return {
    'schemaVersion': '1.0',
    'eventId': const Uuid().v7(),
    'assessmentId': const Uuid().v7(),
    'revision': 1,
    'woundId': const Uuid().v7(),
    'patientRef': 'p-${List.generate(6, (_) => rng.nextInt(16).toRadixString(16)).join()}',
    'deviceId': deviceId,
    'facilityId': facilityId,
    'capturedAt': offsetTimestamp(DateTime.now()),
    'analytics': sampleAnalytics(random: rng),
    'clinicalAssessment': {
      'pedalPulses': triState[rng.nextInt(3)],
      'protectiveSensation': triState[rng.nextInt(3)],
    },
  };
}

/// RFC 3339 with the device's offset, as §5 shows (e.g. 2026-10-03T09:41:12+05:30).
String offsetTimestamp(DateTime local) {
  final o = local.timeZoneOffset;
  final sign = o.isNegative ? '-' : '+';
  String two(int v) => v.abs().toString().padLeft(2, '0');
  final t = local.toIso8601String().split('.').first;
  return '$t$sign${two(o.inHours)}:${two(o.inMinutes % 60)}';
}
