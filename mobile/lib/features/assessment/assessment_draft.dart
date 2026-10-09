import 'package:uuid/uuid.dart';

import '../sync/data/app_database.dart';
import '../sync/ui/sample_assessment.dart';

/// A draft assessment with stable client-side UUIDv7 ids so a retried save is the same event.
class AssessmentDraft {
  AssessmentDraft({this.patient})
    : eventId = const Uuid().v7(),
      assessmentId = const Uuid().v7(),
      woundId = const Uuid().v7();

  final String eventId;
  final String assessmentId;
  final String woundId;

  /// Null means an unlinked case: the event still carries a fresh pseudonymous patientRef (the contract needs one).
  PatientLocalData? patient;

  /// Tri-state (§3). "Not recorded" until the clinician answers; never assumed to be "absent".
  String pedalPulses = 'not_recorded';
  String protectiveSensation = 'not_recorded';

  /// Set by the analysis step. Today a stand-in ([sampleAnalytics]); Members 1–2's pipeline replaces it.
  Map<String, dynamic>? analytics;

  final DateTime startedAt = DateTime.now();

  /// The wound event (contracts/wound-event.schema.json) for the queue.
  Map<String, dynamic> toEvent({required String deviceId, required String facilityId, required String patientRef}) => {
    'schemaVersion': '1.0',
    'eventId': eventId,
    'assessmentId': assessmentId,
    'revision': 1,
    'woundId': woundId,
    'patientRef': patientRef,
    'deviceId': deviceId,
    'facilityId': facilityId,
    'capturedAt': offsetTimestamp(startedAt),
    'analytics': analytics,
    'clinicalAssessment': {'pedalPulses': pedalPulses, 'protectiveSensation': protectiveSensation},
  };
}
