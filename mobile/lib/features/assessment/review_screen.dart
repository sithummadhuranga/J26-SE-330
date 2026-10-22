import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../measurement/capture/capture_runner.dart';
import 'analysis/wound_analyzer.dart';
import 'local_analysis.dart';
import 'local_result_screen.dart';
import '../measurement/capture/photo_check_screen.dart';
import 'assessment_draft.dart';
import 'assessment_view.dart';
import 'processing_screen.dart';

/// The review step. With a real photo it checks it on the phone (is there a usable sticker?) and shows the verdict; without one (the
/// prototype's sample capture) it shows the sample rows it always did.
class ReviewScreen extends StatefulWidget {
  const ReviewScreen({super.key, required this.services, required this.draft, this.checker});

  final AppServices services;
  final AssessmentDraft draft;

  /// Checks the photo's bytes; the real decoder and background check when null (tests pass a fake).
  final Future<CheckedPhoto> Function(Uint8List bytes)? checker;

  @override
  State<ReviewScreen> createState() => _ReviewScreenState();
}

class _ReviewScreenState extends State<ReviewScreen> {
  Future<CheckedPhoto>? _checked;

  @override
  void initState() {
    super.initState();
    final bytes = widget.draft.capture.imageBytes;
    if (bytes != null) _checked = (widget.checker ?? checkEncodedPhoto)(bytes);
  }

  /// No usable sticker: outline the wound and show its colours and skin type, with no area. Nothing is saved or synced.
  Future<void> _withoutArea() async {
    final analyzer = widget.services.analyzer;
    final bytes = widget.draft.capture.imageBytes;
    if (analyzer is! LocalAnalyzer || bytes == null) return;
    final messenger = ScaffoldMessenger.of(context);
    final nav = Navigator.of(context);
    showDialog<void>(context: context, barrierDismissible: false, builder: (_) => const Center(child: CircularProgressIndicator()));
    LocalAnalysis? result;
    String? problem;
    try {
      result = await (analyzer as LocalAnalyzer).analyzeWithoutScale(bytes);
    } on AnalysisFailed catch (e) {
      problem = e.message;
    } catch (e) {
      problem = 'The photo could not be analysed: ${e.runtimeType}: $e';
    }
    if (!mounted) return;
    nav.pop(); // the progress circle
    if (result == null) {
      messenger.showSnackBar(SnackBar(content: Text(problem ?? 'The photo could not be analysed.')));
      return;
    }
    final shown = result;
    await nav.push(
      MaterialPageRoute<void>(
        builder: (_) => LocalResultScreen(photoBytes: bytes, analysis: shown, onDone: () => nav.popUntil((r) => r.isFirst)),
      ),
    );
  }

  void _use() => Navigator.of(context).pushAndRemoveUntil(
    MaterialPageRoute<void>(
      builder: (_) => ProcessingScreen(services: widget.services, draft: widget.draft),
    ),
    (route) => route.isFirst,
  );

  @override
  Widget build(BuildContext context) {
    final future = _checked;
    if (future == null) return _sampleReview(context);
    return FutureBuilder<CheckedPhoto>(
      future: future,
      builder: (context, snap) {
        if (snap.hasError) return _couldNotOpen(context);
        final checked = snap.data;
        if (checked == null) {
          return Scaffold(
            appBar: AppBar(title: const Text('Review Image')),
            body: const Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  CircularProgressIndicator(),
                  SizedBox(height: 14),
                  Text('Checking the photo on this phone…', style: TextStyle(color: WoundColors.textSecondary)),
                ],
              ),
            ),
          );
        }
        return PhotoCheckScreen(
          photoBytes: widget.draft.capture.imageBytes!,
          photoWidth: checked.width,
          photoHeight: checked.height,
          check: checked.check,
          title: 'Review Image',
          useLabel: 'Use this image',
          useAnywayLabel: 'Use anyway',
          anotherLabel: 'Retake',
          useKey: const Key('useImage'),
          allowMeasureWithoutSticker: widget.services.analyzer is LocalAnalyzer,
          withoutLabel: 'Continue without area',
          withoutNote: 'Without a sticker the area is not measured. The outline, colours and skin type still are. Nothing is saved or synced.',
          onWithoutSticker: _withoutArea,
          onMeasure: _use,
          onRetake: () => Navigator.pop(context),
        );
      },
    );
  }

  Widget _couldNotOpen(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Review Image')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        const WoundCard(
          child: Text('This photo could not be opened. Take it again or choose another one.', style: TextStyle(fontSize: 15, height: 1.4)),
        ),
        const SizedBox(height: 22),
        FilledButton(onPressed: () => Navigator.pop(context), child: const Text('Retake')),
      ],
    ),
  );

  Widget _sampleReview(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Review Image')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        MediaFrame(
          aspectRatio: 4 / 3,
          child: WoundIllustration(seed: widget.draft.assessmentId.hashCode),
        ),
        const SizedBox(height: 14),
        WoundCard(
          child: Column(
            children: const [
              KeyValueRow('Image quality', WoundBadge('Good', tone: BadgeTone.success, icon: Icons.check_rounded)),
              KeyValueRow(
                'Reference marker',
                WoundBadge('Detected', tone: BadgeTone.success, icon: Icons.check_rounded),
              ),
            ],
          ),
        ),
        const SizedBox(height: 22),
        DecoratedBox(
          decoration: BoxDecoration(borderRadius: BorderRadius.circular(WoundRadii.m), boxShadow: WoundShadows.accent),
          child: FilledButton(key: const Key('useImage'), onPressed: _use, child: const Text('Use this image')),
        ),
        const SizedBox(height: 10),
        OutlinedButton.icon(
          onPressed: () => Navigator.pop(context),
          icon: const Icon(Icons.replay_rounded, size: 18),
          label: const Text('Retake'),
        ),
      ],
    ),
  );
}
