import 'dart:convert';

import 'package:drift/drift.dart' show BooleanExpressionOperators, OrderingTerm;
import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../recommendations/guidance_screen.dart';
import '../sync/data/app_database.dart';
import '../sync/data/tables.dart';
import 'assessment_view.dart';

/// Assessment summary: measurements, answers, sync status and a link to guidance, all updating live.
class AssessmentDetailScreen extends StatefulWidget {
  const AssessmentDetailScreen({
    super.key,
    required this.services,
    required this.assessmentId,
    this.fromNewAssessment = false,
  });

  final AppServices services;
  final String assessmentId;

  /// Reached from "View results" at the end of a new assessment: offers "Done" back to Home.
  final bool fromNewAssessment;

  @override
  State<AssessmentDetailScreen> createState() => _AssessmentDetailScreenState();
}

class _AssessmentDetailScreenState extends State<AssessmentDetailScreen> {
  bool _segmentation = false;

  Stream<List<QueuedEvent>> _rows() {
    final db = widget.services.db;
    return (db.select(db.woundEventQueue)
          ..where((q) => q.assessmentId.equals(widget.assessmentId))
          ..orderBy([(q) => OrderingTerm.desc(q.revision)]))
        .watch();
  }

  Stream<RecommendationLocalData?> _advice(int revision) {
    final db = widget.services.db;
    return (db.select(
      db.recommendationLocal,
    )..where((r) => r.assessmentId.equals(widget.assessmentId) & r.revision.equals(revision))).watchSingleOrNull();
  }

  @override
  Widget build(BuildContext context) => StreamBuilder<List<QueuedEvent>>(
    stream: _rows(),
    builder: (context, snap) {
      final rows = snap.data;
      if (rows == null) return const Scaffold(body: Center(child: CircularProgressIndicator()));
      if (rows.isEmpty) {
        return Scaffold(
          appBar: AppBar(title: const Text('Assessment')),
          body: const Center(child: Text('Not found.')),
        );
      }
      final v = AssessmentView(rows.first);
      return Scaffold(
        appBar: AppBar(
          title: Text(widget.fromNewAssessment ? 'Assessment Summary' : v.displayId),
          automaticallyImplyLeading: !widget.fromNewAssessment,
          actions: [
            if (widget.fromNewAssessment)
              TextButton(
                key: const Key('done'),
                onPressed: () => Navigator.of(context).popUntil((r) => r.isFirst),
                child: const Text('Done'),
              ),
          ],
        ),
        body: ListView(
          padding: const EdgeInsets.fromLTRB(20, 16, 20, 28),
          children: [
            FutureBuilder<PatientLocalData?>(
              future: widget.services.patients.byRef(v.patientRef),
              builder: (context, p) => WoundCard(
                padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 10),
                child: Row(
                  children: [
                    InitialsAvatar(p.data?.displayAlias ?? '–', size: 30),
                    const SizedBox(width: 9),
                    Expanded(
                      child: Text(
                        p.data?.displayAlias ?? 'Unlinked case',
                        style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13.5),
                      ),
                    ),
                    WoundBadge(v.patientRef),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 14),
            _ImageTabs(segmentation: _segmentation, onChanged: (s) => setState(() => _segmentation = s)),
            const SizedBox(height: 10),
            MediaFrame(
              aspectRatio: 4 / 3,
              child: Stack(
                fit: StackFit.expand,
                children: [
                  WoundIllustration(seed: v.assessmentId.hashCode),
                  if (_segmentation) CustomPaint(painter: _OutlinePainter(v.assessmentId.hashCode)),
                  if (_segmentation)
                    Positioned(left: 10, bottom: 10, child: _OverlayChip('Wound boundary — ${v.segmentationModel}')),
                ],
              ),
            ),
            const SizedBox(height: 16),
            WoundCard(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text(
                    'Wound area',
                    style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: WoundColors.textSecondary),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    formatArea(v.areaMm2),
                    key: const Key('woundArea'),
                    style: const TextStyle(fontSize: 30, fontWeight: FontWeight.w800, letterSpacing: -0.8),
                  ),
                  Text('${v.areaMm2} mm²', style: Theme.of(context).textTheme.bodySmall),
                ],
              ),
            ),
            const SizedBox(height: 14),
            WoundCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const SectionTitle('Color distribution'),
                  _ColourBar(v.regionPercents),
                  const SizedBox(height: 10),
                  for (var i = 0; i < v.regionPercents.length; i++)
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 3),
                      child: Row(
                        children: [
                          Container(
                            width: 10,
                            height: 10,
                            decoration: BoxDecoration(
                              color: regionColours[i % regionColours.length],
                              borderRadius: BorderRadius.circular(3),
                            ),
                          ),
                          const SizedBox(width: 8),
                          Expanded(child: Text('Color region ${String.fromCharCode(65 + i)}')),
                          Text('${v.regionPercents[i]}%', style: const TextStyle(fontWeight: FontWeight.w700)),
                        ],
                      ),
                    ),
                  const SizedBox(height: 6),
                  const Text(
                    'Color clusters represent image appearance and are not clinical tissue diagnoses.',
                    style: TextStyle(fontSize: 11.5, color: WoundColors.textTertiary),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 14),
            WoundCard(
              child: Column(
                children: [
                  const KeyValueRow(
                    'Segmentation',
                    WoundBadge('Completed', tone: BadgeTone.success, icon: Icons.check_rounded),
                  ),
                  const KeyValueRow(
                    'Reference marker',
                    WoundBadge('Detected', tone: BadgeTone.success, icon: Icons.check_rounded),
                  ),
                  const KeyValueRow(
                    'Skin-tone calibration',
                    WoundBadge('Applied', tone: BadgeTone.success, icon: Icons.check_rounded),
                  ),
                  KeyValueRow('Sync', _syncBadge(v.status)),
                ],
              ),
            ),
            const SizedBox(height: 14),
            WoundCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const SectionTitle('Clinical assessment'),
                  KeyValueRow('Pedal pulses', Text(triStateLabel(v.pedalPulses), style: _value)),
                  KeyValueRow('Protective sensation', Text(triStateLabel(v.protectiveSensation), style: _value)),
                ],
              ),
            ),
            const SizedBox(height: 14),
            WoundCard(
              padding: EdgeInsets.zero,
              child: Theme(
                data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
                child: ExpansionTile(
                  title: const Text('Analysis details', style: TextStyle(fontWeight: FontWeight.w700, fontSize: 14)),
                  childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 14),
                  children: [
                    KeyValueRow('Segmentation model', Text(v.segmentationModel, style: _value)),
                    KeyValueRow('Calibration', Text('v${v.calibrationVersion}', style: _value)),
                    KeyValueRow('Fitzpatrick category', Text(v.fitzpatrickClass, style: _value)),
                    KeyValueRow(
                      'Captured',
                      Text('${formatDate(v.capturedAt)}, ${formatTime(v.capturedAt)}', style: _value),
                    ),
                    KeyValueRow('Revision', Text('${v.revision}', style: _value)),
                    const SizedBox(height: 6),
                    const Text(
                      'Measurement values are demonstration data until the on-device pipeline is added.',
                      style: TextStyle(fontSize: 11.5, color: WoundColors.textTertiary),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 14),
            const NoticeStrip(
              'Decision-support information only. Clinical assessment and treatment decisions remain with the clinician.',
              icon: Icons.info_outline_rounded,
            ),
            const SizedBox(height: 18),
            StreamBuilder<RecommendationLocalData?>(
              stream: _advice(v.revision),
              builder: (context, adviceSnap) => _GuidanceButton(
                advice: adviceSnap.data,
                status: v.status,
                onOpen: (advice) => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (_) => GuidanceScreen(
                      services: widget.services,
                      payload: jsonDecode(advice.payloadJson) as Map<String, dynamic>,
                      receivedAt: advice.receivedAt,
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      );
    },
  );

  static const _value = TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5);

  Widget _syncBadge(QueueStatus s) {
    final (label, tone) = syncLabel(s);
    return WoundBadge(label, tone: tone);
  }
}

class _GuidanceButton extends StatelessWidget {
  const _GuidanceButton({required this.advice, required this.status, required this.onOpen});

  final RecommendationLocalData? advice;
  final QueueStatus status;
  final void Function(RecommendationLocalData) onOpen;

  @override
  Widget build(BuildContext context) {
    final ready = advice != null;
    final why = switch (status) {
      QueueStatus.pending || QueueStatus.inFlight => 'Guidance arrives after this assessment syncs.',
      QueueStatus.rejected => 'The server did not accept this assessment, so there is no guidance.',
      QueueStatus.superseded => 'A newer revision of this assessment received the guidance.',
      _ => 'Waiting for guidance from the server. It appears here automatically.',
    };
    return Column(
      children: [
        DecoratedBox(
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(WoundRadii.m),
            boxShadow: ready ? WoundShadows.accent : null,
          ),
          child: FilledButton.icon(
            key: const Key('viewGuidance'),
            onPressed: ready ? () => onOpen(advice!) : null,
            icon: const Icon(Icons.fact_check_outlined, size: 20),
            label: const Text('View Evidence-Based Guidance'),
          ),
        ),
        if (!ready) ...[
          const SizedBox(height: 8),
          Text(
            why,
            key: const Key('guidancePending'),
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 12, color: WoundColors.textTertiary),
          ),
        ],
      ],
    );
  }
}

class _ImageTabs extends StatelessWidget {
  const _ImageTabs({required this.segmentation, required this.onChanged});

  final bool segmentation;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(4),
    decoration: BoxDecoration(color: WoundColors.surfaceAlt, borderRadius: BorderRadius.circular(12)),
    child: Row(
      children: [
        for (final (seg, label) in const [(false, 'Original'), (true, 'Segmentation')])
          Expanded(
            child: GestureDetector(
              onTap: () => onChanged(seg),
              child: AnimatedContainer(
                duration: const Duration(milliseconds: 150),
                padding: const EdgeInsets.symmetric(vertical: 8),
                decoration: BoxDecoration(
                  color: seg == segmentation ? WoundColors.surface : Colors.transparent,
                  borderRadius: BorderRadius.circular(9),
                  boxShadow: seg == segmentation ? WoundShadows.card : null,
                ),
                child: Text(
                  label,
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    fontSize: 12.5,
                    fontWeight: FontWeight.w700,
                    color: seg == segmentation ? WoundColors.accentDark : WoundColors.textSecondary,
                  ),
                ),
              ),
            ),
          ),
      ],
    ),
  );
}

class _OverlayChip extends StatelessWidget {
  const _OverlayChip(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 5),
    decoration: BoxDecoration(color: Colors.black.withValues(alpha: 0.55), borderRadius: BorderRadius.circular(20)),
    child: Text(
      text,
      style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w600),
    ),
  );
}

/// The segmentation boundary drawn over the illustration (same geometry as its wound).
class _OutlinePainter extends CustomPainter {
  _OutlinePainter(this.seed);

  final int seed;

  @override
  void paint(Canvas canvas, Size size) {
    final c = Offset(size.width * (0.48 + (seed % 5) * 0.01), size.height * 0.52);
    final w = size.width * (0.36 + (seed % 3) * 0.04), h = size.height * (0.32 + (seed % 4) * 0.03);
    final r = Rect.fromCenter(center: c, width: w, height: h);
    canvas.drawOval(r, Paint()..color = const Color(0x405EE6C9));
    canvas.drawOval(
      r,
      Paint()
        ..color = const Color(0xFF5EE6C9)
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2.5,
    );
  }

  @override
  bool shouldRepaint(_OutlinePainter old) => old.seed != seed;
}

class _ColourBar extends StatelessWidget {
  const _ColourBar(this.percents);

  final List<double> percents;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(6),
    child: SizedBox(
      height: 12,
      child: Row(
        children: [
          for (var i = 0; i < percents.length; i++)
            if (percents[i] > 0)
              Expanded(
                flex: (percents[i] * 10).round(),
                child: Container(color: regionColours[i % regionColours.length]),
              ),
        ],
      ),
    ),
  );
}
