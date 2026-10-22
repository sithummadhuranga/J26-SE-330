import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../measurement/demo/colour_bar.dart';
import 'assessment_view.dart';
import 'local_analysis.dart';

/// The result for a photo with no sticker: the outline, the colour regions and the skin type, with the area marked "Not measured".
/// It says plainly that nothing was corrected, saved or synced, so nobody mistakes it for a full assessment.
class LocalResultScreen extends StatelessWidget {
  const LocalResultScreen({super.key, required this.photoBytes, required this.analysis, required this.onDone});

  final Uint8List photoBytes;
  final LocalAnalysis analysis;
  final VoidCallback onDone;

  @override
  Widget build(BuildContext context) {
    final skin = analysis.fitzpatrickClass;
    return Scaffold(
      appBar: AppBar(title: const Text('Result without a sticker')),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 28),
        children: [
          MediaFrame(
            aspectRatio: 4 / 3,
            child: DecoratedBox(
              decoration: BoxDecoration(color: WoundColors.surfaceSunken, borderRadius: BorderRadius.circular(WoundRadii.m)),
              child: OutlinedPhoto(photo: photoBytes, outline: analysis.outline),
            ),
          ),
          const SizedBox(height: 16),
          WoundCard(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: const [
                SectionTitle('Wound area'),
                Text('Not measured', style: TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
                SizedBox(height: 6),
                Text('The area in mm² needs the sticker in the photo, because the sticker is the only scale.',
                    style: TextStyle(fontSize: 13, color: WoundColors.textSecondary, height: 1.4)),
              ],
            ),
          ),
          const SizedBox(height: 14),
          WoundCard(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const SectionTitle('Color distribution'),
                ColourBar(regions: analysis.colourRegions),
                const SizedBox(height: 8),
                const Text(
                  'These colours are not colour-corrected: without the sticker there is no grey reference. Color clusters represent '
                  'image appearance and are not clinical tissue diagnoses.',
                  style: TextStyle(fontSize: 11.5, color: WoundColors.textTertiary, height: 1.4),
                ),
              ],
            ),
          ),
          const SizedBox(height: 14),
          WoundCard(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                const Expanded(child: Text('Skin tone (estimate)', style: TextStyle(fontSize: 14))),
                Text(skin == null ? 'not estimated' : 'Fitzpatrick $skin', style: const TextStyle(fontWeight: FontWeight.w700)),
              ],
            ),
          ),
          const SizedBox(height: 14),
          const NoticeStrip(
            'This result is not saved and not synced, and no guideline advice is made from it: the advice needs an area. '
            'Take the photo again with the sticker beside the wound to save a full assessment.',
            icon: Icons.info_outline_rounded,
          ),
          const SizedBox(height: 18),
          FilledButton(onPressed: onDone, child: const Text('Done')),
        ],
      ),
    );
  }
}
