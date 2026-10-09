import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/queue_repository.dart';
import '../sync/data/tables.dart';
import '../sync/engine/sync_engine.dart';
import '../sync/ui/sample_assessment.dart';
import 'assessment_detail_screen.dart';
import 'assessment_draft.dart';
import 'assessment_view.dart';

enum StepState { pending, active, done, attention }

class _Step {
  _Step(this.label);

  final String label;
  StepState state = StepState.pending;
  String? note;
}

/// Runs the assessment: save to the encrypted queue first, then sync and wait for advice (or just queue if offline).
class ProcessingScreen extends StatefulWidget {
  const ProcessingScreen({
    super.key,
    required this.services,
    required this.draft,
    this.stepDuration = const Duration(milliseconds: 900),
    this.adviceTimeout = const Duration(seconds: 45),
    this.adviceRetry = const Duration(milliseconds: 2500),
  });

  final AppServices services;
  final AssessmentDraft draft;
  final Duration stepDuration;
  final Duration adviceTimeout;
  final Duration adviceRetry;

  @override
  State<ProcessingScreen> createState() => _ProcessingScreenState();
}

class _ProcessingScreenState extends State<ProcessingScreen> {
  // The first four are stand-ins until Members 1–2's pipeline is plugged in.
  final _steps = [
    _Step('Skin tone calibration'),
    _Step('Wound segmentation'),
    _Step('Area measurement'),
    _Step('Color distribution analysis'),
    _Step('Saved on this phone'),
    _Step('Evidence retrieval'),
  ];
  bool _finished = false;
  bool _saved = false;

  @override
  void initState() {
    super.initState();
    _run();
  }

  void _set(int i, StepState state, [String? note]) {
    if (!mounted) return;
    setState(() {
      _steps[i].state = state;
      _steps[i].note = note;
    });
  }

  Future<void> _run() async {
    for (var i = 0; i < 4; i++) {
      _set(i, StepState.active);
      await Future<void>.delayed(widget.stepDuration);
      if (!mounted) return;
      _set(i, StepState.done);
    }
    widget.draft.analytics ??= sampleAnalytics();

    _set(4, StepState.active);
    final saveError = await _save();
    if (saveError != null) {
      _set(4, StepState.attention, saveError);
      setState(() => _finished = true);
      return;
    }
    _saved = true;
    _set(4, StepState.done);

    _set(5, StepState.active);
    StepState state;
    String? note;
    try {
      (state, note) = await _waitForAdvice();
    } catch (_) {
      // The assessment is already committed to the queue; whatever failed here, it syncs later.
      (state, note) = (StepState.attention, 'Saved — guidance will arrive once it syncs');
    }
    _set(5, state, note);
    if (mounted) setState(() => _finished = true);
  }

  /// Null when the event is committed to the queue; otherwise why not, in words.
  Future<String?> _save() async {
    final s = widget.services;
    final who = await s.cachedClinician();
    if (who == null || who.facilityId.isEmpty) return 'Sign in once on this phone before saving assessments.';
    final draft = widget.draft;
    try {
      await s.queue.enqueue(
        draft.toEvent(
          deviceId: await s.queue.deviceId(),
          facilityId: who.facilityId,
          patientRef: draft.patient?.patientRef ?? s.patients.newPatientRef(),
        ),
      );
      return null;
    } on EnqueueRejected catch (e) {
      return 'Not saved: ${e.errors.first}';
    } catch (_) {
      return 'Could not save on this phone. Go back and try again.';
    }
  }

  Future<(StepState, String?)> _waitForAdvice() async {
    final s = widget.services;
    final deadline = DateTime.now().add(widget.adviceTimeout);
    while (mounted) {
      final report = await s.scheduler.syncNow();
      final status = await _status();
      if (status == QueueStatus.complete || status == QueueStatus.superseded) return (StepState.done, null);
      if (status == QueueStatus.rejected) return (StepState.attention, 'The server did not accept this assessment.');
      switch (report.outcome) {
        case SyncOutcome.offline || SyncOutcome.serverBusy || SyncOutcome.backingOff:
          return (StepState.attention, 'Offline — saved, and guidance will arrive once it syncs');
        case SyncOutcome.needsSignIn:
          return (StepState.attention, 'Sign in again to sync — the assessment is saved');
        default:
      }
      if (DateTime.now().isAfter(deadline)) {
        return (StepState.attention, 'Synced — guidance is taking longer and will appear when ready');
      }
      await Future<void>.delayed(widget.adviceRetry);
    }
    return (StepState.pending, null);
  }

  Future<QueueStatus?> _status() async {
    for (final r in await widget.services.queue.all()) {
      if (r.eventId == widget.draft.eventId) return r.status;
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final done = _steps.where((s) => s.state == StepState.done).length;
    final pct = (done / _steps.length * 100).round();
    return PopScope(
      canPop: _finished,
      child: Scaffold(
        appBar: AppBar(title: const Text('Analyzing Assessment'), automaticallyImplyLeading: _finished),
        body: ListView(
          padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
          children: [
            MediaFrame(
              aspectRatio: 16 / 9,
              child: WoundIllustration(seed: widget.draft.assessmentId.hashCode),
            ),
            const SizedBox(height: 16),
            ClipRRect(
              borderRadius: BorderRadius.circular(6),
              child: LinearProgressIndicator(
                value: done / _steps.length,
                minHeight: 6,
                backgroundColor: WoundColors.surfaceSunken,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              _finished ? (_saved ? 'Analysis complete' : 'Not saved') : '$pct% complete',
              key: const Key('progressLabel'),
              textAlign: TextAlign.center,
              style: const TextStyle(fontWeight: FontWeight.w700, color: WoundColors.textSecondary),
            ),
            const SizedBox(height: 18),
            for (var i = 0; i < _steps.length; i++) _TimelineItem(step: _steps[i], last: i == _steps.length - 1),
            if (_finished) ...[
              const SizedBox(height: 14),
              if (_saved)
                DecoratedBox(
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(WoundRadii.m),
                    boxShadow: WoundShadows.accent,
                  ),
                  child: FilledButton(
                    key: const Key('viewResults'),
                    onPressed: () => Navigator.of(context).pushReplacement(
                      MaterialPageRoute<void>(
                        builder: (_) => AssessmentDetailScreen(
                          services: widget.services,
                          assessmentId: widget.draft.assessmentId,
                          fromNewAssessment: true,
                        ),
                      ),
                    ),
                    child: const Text('View results'),
                  ),
                )
              else
                OutlinedButton(onPressed: () => Navigator.pop(context), child: const Text('Back')),
            ],
            const SizedBox(height: 16),
            const NoticeStrip(
              'Prototype: calibration, segmentation, measurement and color analysis use demonstration values until '
              'the on-device pipeline is added. Saving and evidence retrieval are real.',
              icon: Icons.info_outline_rounded,
            ),
          ],
        ),
      ),
    );
  }
}

class _TimelineItem extends StatelessWidget {
  const _TimelineItem({required this.step, required this.last});

  final _Step step;
  final bool last;

  @override
  Widget build(BuildContext context) {
    final (Widget dot, Color fg, String status) = switch (step.state) {
      StepState.pending => (
        const Icon(Icons.schedule_rounded, size: 16, color: WoundColors.textTertiary),
        WoundColors.textTertiary,
        'Pending',
      ),
      StepState.active => (
        const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)),
        WoundColors.accentDark,
        'Processing…',
      ),
      StepState.done => (
        const Icon(Icons.check_rounded, size: 16, color: WoundColors.success),
        WoundColors.success,
        'Completed',
      ),
      StepState.attention => (
        const Icon(Icons.priority_high_rounded, size: 16, color: WoundColors.warning),
        WoundColors.warning,
        'Needs attention — ${step.note}',
      ),
    };
    final bg = switch (step.state) {
      StepState.done => WoundColors.successSoft,
      StepState.attention => WoundColors.warningSoft,
      StepState.active => WoundColors.accentSoft,
      StepState.pending => WoundColors.surfaceAlt,
    };
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Column(
            children: [
              Container(
                width: 32,
                height: 32,
                decoration: BoxDecoration(color: bg, shape: BoxShape.circle),
                child: Center(child: dot),
              ),
              if (!last) Expanded(child: Container(width: 1.5, color: WoundColors.borderStrong)),
            ],
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(top: 5, bottom: 16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(step.label, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14)),
                  const SizedBox(height: 2),
                  Text(
                    status,
                    key: Key('step-${step.label}'),
                    style: TextStyle(fontSize: 12.5, color: fg, fontWeight: FontWeight.w600),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}
