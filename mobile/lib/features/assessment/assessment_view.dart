import 'dart:convert';

import 'package:flutter/material.dart';

import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/app_database.dart';
import '../sync/data/tables.dart';

/// One saved assessment as the screens show it, read from its queued wound event (contracts/wound-event.schema.json).
class AssessmentView {
  AssessmentView(this.row); 

  final QueuedEvent row;

  /// Decoded lazily so lists only decode the cards they actually build.
  late final Map<String, dynamic> event = jsonDecode(row.payloadJson) as Map<String, dynamic>;

  String get assessmentId => row.assessmentId;
  int get revision => row.revision;
  QueueStatus get status => row.status;
  String get patientRef => event['patientRef'] as String;
  DateTime get capturedAt => DateTime.tryParse(event['capturedAt'] as String? ?? '') ?? row.createdAt;

  Map<String, dynamic> get _analytics => event['analytics'] as Map<String, dynamic>;
  num get areaMm2 => _analytics['areaMm2'] as num;
  String get fitzpatrickClass => _analytics['fitzpatrickClass'] as String;
  String get calibrationVersion => (_analytics['pipeline'] as Map<String, dynamic>)['calibration'] as String;
  String get segmentationModel => (_analytics['pipeline'] as Map<String, dynamic>)['segmentation'] as String;
  List<double> get regionPercents => [
    for (final r in (_analytics['colourRegions'] as List).cast<Map<String, dynamic>>())
      (r['percent'] as num).toDouble(),
  ];

  Map<String, dynamic> get _clinical => event['clinicalAssessment'] as Map<String, dynamic>;
  String get pedalPulses => _clinical['pedalPulses'] as String;
  String get protectiveSensation => _clinical['protectiveSensation'] as String;

  /// A short, readable reference for the assessment ("WA-1A2B3C4D"); the full id stays on the record.
  String get displayId => displayIdOf(assessmentId);

  static String displayIdOf(String assessmentId) {
    final hex = assessmentId.replaceAll('-', '');
    return 'WA-${hex.substring(hex.length - 8).toUpperCase()}';
  }

  /// The latest revision of each assessment, newest first: what History and Home list.
  static List<AssessmentView> latestPerAssessment(List<QueuedEvent> rows) {
    final byId = <String, QueuedEvent>{};
    for (final r in rows) {
      final seen = byId[r.assessmentId];
      if (seen == null || r.revision > seen.revision) byId[r.assessmentId] = r;
    }
    final list = byId.values.map(AssessmentView.new).toList()
      ..sort((a, b) => b.row.createdAt.compareTo(a.row.createdAt));
    return list;
  }
}

/// The record's place in the sync lifecycle (§6.1), in a clinician's words.
(String, BadgeTone) syncLabel(QueueStatus s) => switch (s) {
  QueueStatus.pending || QueueStatus.inFlight => ('Saved on phone', BadgeTone.warning),
  QueueStatus.accepted => ('Synced', BadgeTone.success),
  QueueStatus.adviceDeferred => ('Advice delayed', BadgeTone.warning),
  QueueStatus.complete => ('Advice ready', BadgeTone.accent),
  QueueStatus.superseded => ('Replaced by edit', BadgeTone.neutral),
  QueueStatus.rejected => ('Not accepted', BadgeTone.error),
};

/// Tri-state clinical values (§3): "not recorded" is its own answer, never read as "no".
String triStateLabel(String v) => switch (v) {
  'present' => 'Present',
  'absent' => 'Absent',
  _ => 'Not recorded',
};

const regionColours = [WoundColors.accent, WoundColors.amber, WoundColors.borderStrong, WoundColors.accent2];

/// Placeholder wound illustration until the capture module (Member 1) provides real photos.
class WoundIllustration extends StatelessWidget {
  const WoundIllustration({super.key, this.seed = 0, this.borderRadius = WoundRadii.m, this.showMarker = true});

  final int seed;
  final double borderRadius;
  final bool showMarker;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(borderRadius),
    child: CustomPaint(painter: _WoundPainter(seed, showMarker), child: const SizedBox.expand()),
  );
}

class _WoundPainter extends CustomPainter {
  _WoundPainter(this.seed, this.showMarker);

  final int seed;
  final bool showMarker;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;
    canvas.drawRect(
      rect,
      Paint()
        ..shader = const RadialGradient(
          center: Alignment(-0.3, -0.4),
          radius: 1.2,
          colors: [Color(0xFF8A5A3C), Color(0xFF6B4129), Color(0xFF4E2F1D)],
        ).createShader(rect),
    );
    final c = Offset(size.width * (0.48 + (seed % 5) * 0.01), size.height * 0.52);
    final w = size.width * (0.36 + (seed % 3) * 0.04), h = size.height * (0.32 + (seed % 4) * 0.03);
    final wound = Rect.fromCenter(center: c, width: w, height: h);
    canvas.drawOval(wound.inflate(size.shortestSide * 0.03), Paint()..color = const Color(0xFF5A2A1E));
    canvas.drawOval(
      wound,
      Paint()
        ..shader = const RadialGradient(
          colors: [Color(0xFFB8463A), Color(0xFF9C3A30), Color(0xFF7E2E26)],
        ).createShader(wound),
    );
    canvas.drawOval(
      Rect.fromCenter(center: c.translate(w * 0.12, -h * 0.08), width: w * 0.38, height: h * 0.32),
      Paint()..color = const Color(0xCCD9B45A),
    );
    if (showMarker) {
      final m = Rect.fromLTWH(
        size.width * 0.08,
        size.height * 0.68,
        size.shortestSide * 0.16,
        size.shortestSide * 0.16,
      );
      canvas.drawRRect(RRect.fromRectAndRadius(m, const Radius.circular(3)), Paint()..color = Colors.white);
      final q = m.deflate(m.width * 0.18);
      canvas.drawRect(
        Rect.fromLTWH(q.left, q.top, q.width / 2, q.height / 2),
        Paint()..color = const Color(0xFF1D1C18),
      );
      canvas.drawRect(
        Rect.fromLTWH(q.center.dx, q.center.dy, q.width / 2, q.height / 2),
        Paint()..color = const Color(0xFF1D1C18),
      );
    }
  }

  @override
  bool shouldRepaint(_WoundPainter old) => old.seed != seed || old.showMarker != showMarker;
}

/// History / Home list card: thumbnail, reference, date, patient, sync state, area.
class AssessmentCard extends StatelessWidget {
  const AssessmentCard({super.key, required this.view, required this.patientLabel, required this.onTap});

  final AssessmentView view;
  final String patientLabel;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final (label, tone) = syncLabel(view.status);
    return WoundCard(
      key: Key('assessment-${view.assessmentId}'),
      padding: const EdgeInsets.all(12),
      onTap: onTap,
      child: Row(
        children: [
          SizedBox(width: 48, height: 48, child: WoundIllustration(seed: view.assessmentId.hashCode, borderRadius: 11)),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(view.displayId, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14)),
                const SizedBox(height: 3),
                Text(
                  '${formatDate(view.capturedAt)} · $patientLabel',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
                const SizedBox(height: 6),
                WoundBadge(label, tone: tone),
              ],
            ),
          ),
          Text(formatArea(view.areaMm2), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5)),
        ],
      ),
    );
  }
}
