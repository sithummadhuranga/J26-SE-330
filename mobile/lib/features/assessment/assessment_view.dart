import 'dart:typed_data';

import 'dart:convert';

import 'package:flutter/material.dart';

import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/app_database.dart';
import '../sync/data/tables.dart';
import 'media_store.dart';

/// One saved assessment, the way the screens show it. It's read from the queued wound event
/// (contracts/wound-event.schema.json).
class AssessmentView {
  AssessmentView(this.row); 

  final QueuedEvent row;

  /// Decoded only when needed, so a list only decodes the cards it actually builds.
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

  /// A short reference that's easy to read, like "WA-1A2B3C4D". The full id stays on the record.
  String get displayId => displayIdOf(assessmentId);

  static String displayIdOf(String assessmentId) {
    final hex = assessmentId.replaceAll('-', '');
    return 'WA-${hex.substring(hex.length - 8).toUpperCase()}';
  }

  /// The latest revision of each assessment, newest first. This is what History and Home list.
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

/// Where the record is in the sync (§6.1), in words a clinician would use.
(String, BadgeTone) syncLabel(QueueStatus s) => switch (s) {
  QueueStatus.pending || QueueStatus.inFlight => ('Saved on phone', BadgeTone.warning),
  QueueStatus.accepted => ('Synced', BadgeTone.success),
  QueueStatus.adviceDeferred => ('Advice delayed', BadgeTone.warning),
  QueueStatus.complete => ('Advice ready', BadgeTone.accent),
  QueueStatus.superseded => ('Replaced by edit', BadgeTone.neutral),
  QueueStatus.rejected => ('Not accepted', BadgeTone.error),
};

/// Clinical answers with three options (§3). "Not recorded" is its own answer and never means "no".
String triStateLabel(String v) => switch (v) {
  'present' => 'Present',
  'absent' => 'Absent',
  _ => 'Not recorded',
};

const regionColours = [WoundColors.accent, WoundColors.amber, WoundColors.borderStrong, WoundColors.accent2];

/// The assessment's own photo: [bytes] when the photo is at hand (during the analysis), else the one kept on the phone for
/// [assessmentId]. With nothing kept (the prototype's sample capture, or an assessment made on another phone) it shows the stand-in
/// illustration. [showOutline] draws the wound outline the model found, when one was kept.
class AssessmentPhoto extends StatelessWidget {
  const AssessmentPhoto({
    super.key,
    required this.store,
    required this.assessmentId,
    this.bytes,
    this.borderRadius = WoundRadii.m,
    this.showOutline = false,
    this.cacheWidth = 900,
  });

  final MediaStore store;
  final String assessmentId;
  final Uint8List? bytes;
  final double borderRadius;
  final bool showOutline;
  final int cacheWidth;

  @override
  Widget build(BuildContext context) {
    final given = bytes;
    if (given != null) return _photo(given, null);
    return FutureBuilder<AssessmentMedia?>(
      future: store.load(assessmentId),
      builder: (context, snap) {
        final media = snap.data;
        if (media == null) return WoundIllustration(seed: assessmentId.hashCode, borderRadius: borderRadius);
        return _photo(media.photo, showOutline ? media.outline : null);
      },
    );
  }

  Widget _photo(Uint8List photo, WoundOutline? outline) => ClipRRect(
    borderRadius: BorderRadius.circular(borderRadius),
    child: Stack(
      fit: StackFit.expand,
      children: [
        // With an outline the whole photo must show, so the outline lands on the wound; without one a thumbnail may be cropped.
        Image.memory(photo, fit: outline == null ? BoxFit.cover : BoxFit.contain, cacheWidth: cacheWidth, gaplessPlayback: true),
        if (outline != null)
          CustomPaint(
            key: const Key('woundOutline'),
            painter: _RealOutlinePainter(outline.boundaryFractions(), outline.originalWidth, outline.originalHeight),
          ),
      ],
    ),
  );
}

/// The model's outline, as small dots on the boundary pixels. [points] are fractions of the width and height.
class _RealOutlinePainter extends CustomPainter {
  _RealOutlinePainter(this.points, this.photoWidth, this.photoHeight);

  final List<(double, double)> points;
  final int photoWidth, photoHeight;

  @override
  void paint(Canvas canvas, Size size) {
    // The photo is drawn as large as fits and centred (BoxFit.contain); the outline goes on that rectangle only.
    final k = size.width / photoWidth < size.height / photoHeight ? size.width / photoWidth : size.height / photoHeight;
    final w = photoWidth * k, h = photoHeight * k;
    final left = (size.width - w) / 2, top = (size.height - h) / 2;
    final halo = Paint()..color = Colors.white.withValues(alpha: 0.8);
    final dot = Paint()..color = const Color(0xFF0C6D5E);
    for (final (x, y) in points) {
      canvas.drawCircle(Offset(left + x * w, top + y * h), 2.6, halo);
    }
    for (final (x, y) in points) {
      canvas.drawCircle(Offset(left + x * w, top + y * h), 1.5, dot);
    }
  }

  @override
  bool shouldRepaint(_RealOutlinePainter old) => old.points != points || old.photoWidth != photoWidth || old.photoHeight != photoHeight;
}

/// A stand-in wound picture, used until the capture module (Member 1) provides real photos.
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

/// The card used in the History and Home lists. It shows the thumbnail, reference, date, patient,
/// sync state and area.
class AssessmentCard extends StatelessWidget {
  const AssessmentCard({super.key, required this.view, required this.patientLabel, required this.onTap, this.media});

  final AssessmentView view;

  /// Where the photos are kept; without it the card shows the stand-in illustration.
  final MediaStore? media;
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
          SizedBox(
            width: 48,
            height: 48,
            child: media == null
                ? WoundIllustration(seed: view.assessmentId.hashCode, borderRadius: 11)
                : AssessmentPhoto(store: media!, assessmentId: view.assessmentId, borderRadius: 11, cacheWidth: 150),
          ),
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
