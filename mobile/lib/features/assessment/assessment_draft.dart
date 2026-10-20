import 'package:uuid/uuid.dart';

import '../sync/data/app_database.dart';
import '../sync/ui/sample_assessment.dart';

/// An assessment being filled in. Its UUIDv7 ids are made on the phone and don't change,
/// so saving it again produces the same event.
class AssessmentDraft {
  AssessmentDraft({this.patient})
    : eventId = const Uuid().v7(),
      assessmentId = const Uuid().v7(),
      woundId = const Uuid().v7();

  final String eventId;
  final String assessmentId;
  final String woundId;

  /// Null means the case isn't linked to a patient. The event still gets a new pseudonymous patientRef,
  /// because the contract requires one.
  PatientLocalData? patient;

  /// One of three answers (§3). It stays "not recorded" until the clinician answers, and we never assume "absent".
  String pedalPulses = 'not_recorded';
  String protectiveSensation = 'not_recorded';

  /// Filled in by the analysis step. For now that's a stand-in ([sampleAnalytics]) until the pipeline
  /// from Members 1–2 replaces it.
  Map<String, dynamic>? analytics;

  final DateTime startedAt = DateTime.now();

  /// Builds the wound event that goes into the queue (contracts/wound-event.schema.json).
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
