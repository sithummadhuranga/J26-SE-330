import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import 'assessment_draft.dart';
import 'assessment_view.dart';
import 'processing_screen.dart';

class ReviewScreen extends StatelessWidget {
  const ReviewScreen({super.key, required this.services, required this.draft});

  final AppServices services;
  final AssessmentDraft draft;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Review Image')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        MediaFrame(
          aspectRatio: 4 / 3,
          child: WoundIllustration(seed: draft.assessmentId.hashCode),
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
          child: FilledButton(
            key: const Key('useImage'),
            onPressed: () => Navigator.of(context).pushAndRemoveUntil(
              MaterialPageRoute<void>(
                builder: (_) => ProcessingScreen(services: services, draft: draft),
              ),
              (route) => route.isFirst,
            ),
            child: const Text('Use this image'),
          ),
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
