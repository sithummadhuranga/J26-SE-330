import 'dart:convert';

/// Checks a wound event against the shared schema and 16 KB limit before it's queued.
class WoundEventValidator {
  static const maxSizeBytes = 16 * 1024;
  static const triState = {'present', 'absent', 'not_recorded'};
  static const fitzpatrickClasses = {'I', 'II', 'III', 'IV', 'V', 'VI'};

  static final _uuidv7 = RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$');
  static final _patientRef = RegExp(r'^p-[0-9a-f]{6,32}$');

  static const _topLevel = {
    'schemaVersion', 'eventId', 'assessmentId', 'revision', 'woundId', 'patientRef',
    'deviceId', 'facilityId', 'capturedAt', 'analytics', 'clinicalAssessment',
  };

  /// Returns the problems found; empty means valid.
  static List<String> validate(Map<String, dynamic> event) {
    final errors = <String>[];
    void check(bool ok, String message) {
      if (!ok) errors.add(message);
    }

    _exactKeys(event, _topLevel, '', errors);
    check(event['schemaVersion'] == '1.0', 'schemaVersion must be "1.0"');
    for (final id in ['eventId', 'assessmentId', 'woundId']) {
      check(event[id] is String && _uuidv7.hasMatch(event[id] as String), '$id must be a lower-case UUIDv7');
    }
    check(event['revision'] is int && (event['revision'] as int) >= 1, 'revision must be an integer ≥ 1');
    check(event['patientRef'] is String && _patientRef.hasMatch(event['patientRef'] as String),
        'patientRef must be a pseudonym like p-7f3a9c (never a name or MRN)');
    for (final id in ['deviceId', 'facilityId']) {
      final v = event[id];
      check(v is String && v.isNotEmpty && v.length <= 64, '$id must be 1–64 characters');
    }
    check(event['capturedAt'] is String && _isDateTime(event['capturedAt'] as String),
        'capturedAt must be an RFC 3339 date-time with an offset');

    final analytics = event['analytics'];
    if (analytics is Map<String, dynamic>) {
      _exactKeys(analytics, {'areaMm2', 'colourRegions', 'fitzpatrickClass', 'pipeline'}, 'analytics.', errors);
      check(analytics['areaMm2'] is num && (analytics['areaMm2'] as num) >= 0, 'analytics.areaMm2 must be ≥ 0');
      final regions = analytics['colourRegions'];
      if (regions is List && regions.length <= 16) {
        for (final (i, r) in regions.indexed) {
          if (r is! Map<String, dynamic>) {
            errors.add('analytics.colourRegions[$i] must be an object');
            continue;
          }
          _exactKeys(r, {'cluster', 'percent'}, 'analytics.colourRegions[$i].', errors);
          check(r['cluster'] is int && (r['cluster'] as int) >= 1, 'analytics.colourRegions[$i].cluster must be ≥ 1');
          check(r['percent'] is num && (r['percent'] as num) >= 0 && (r['percent'] as num) <= 100,
              'analytics.colourRegions[$i].percent must be 0–100');
        }
      } else {
        errors.add('analytics.colourRegions must be a list of at most 16 regions');
      }
      check(fitzpatrickClasses.contains(analytics['fitzpatrickClass']), 'analytics.fitzpatrickClass must be I–VI');
      final pipeline = analytics['pipeline'];
      if (pipeline is Map<String, dynamic>) {
        _exactKeys(pipeline, {'calibration', 'segmentation'}, 'analytics.pipeline.', errors);
        check(pipeline['calibration'] is String, 'analytics.pipeline.calibration must be a string');
        check(pipeline['segmentation'] is String, 'analytics.pipeline.segmentation must be a string');
      } else {
        errors.add('analytics.pipeline is required');
      }
    } else {
      errors.add('analytics is required');
    }

    // Tri-state is strict (§3, §5): a missing key is an error, never read as not_recorded.
    final clinical = event['clinicalAssessment'];
    if (clinical is Map<String, dynamic>) {
      _exactKeys(clinical, {'pedalPulses', 'protectiveSensation'}, 'clinicalAssessment.', errors);
      for (final field in ['pedalPulses', 'protectiveSensation']) {
        check(triState.contains(clinical[field]), 'clinicalAssessment.$field must be present, absent or not_recorded');
      }
    } else {
      errors.add('clinicalAssessment is required');
    }

    final size = utf8.encode(jsonEncode(event)).length;
    check(size <= maxSizeBytes, 'event is $size bytes; the limit is $maxSizeBytes (analytics only, no images, §5)');
    return errors;
  }

  /// additionalProperties: false, and every required key present.
  static void _exactKeys(Map<String, dynamic> map, Set<String> allowed, String path, List<String> errors) {
    for (final key in allowed.difference(map.keys.toSet())) {
      errors.add('$path$key is required');
    }
    for (final key in map.keys.toSet().difference(allowed)) {
      errors.add('$path$key is not allowed (the contract has no such field)');
    }
  }

  static bool _isDateTime(String s) =>
      RegExp(r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$').hasMatch(s) &&
      DateTime.tryParse(s) != null;
}
