import 'package:uuid/uuid.dart';

import '../sync/data/app_database.dart';
import 'analysis/wound_analyzer.dart';
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

  /// The photo from the capture step. It's empty until Member 1's camera module is plugged in.
  WoundCapture capture = const WoundCapture();

  /// Filled in by the analysis step ([WoundAnalyzer]), already checked against the contract.
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
