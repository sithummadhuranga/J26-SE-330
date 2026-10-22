import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import 'assessment_draft.dart';
import 'photo_source.dart';
import 'review_screen.dart';

/// The capture step. It uses a sample capture until Member 1's camera module is plugged in.
class CaptureScreen extends StatelessWidget {
  const CaptureScreen({super.key, required this.services, required this.draft});

  final AppServices services;
  final AssessmentDraft draft;

  void _next(BuildContext context) => Navigator.of(context).push(
    MaterialPageRoute<void>(
      builder: (_) => ReviewScreen(services: services, draft: draft),
    ),
  );

  /// Gets a photo from the camera or the device and goes on to the review. A cancelled picker stays here; a camera that fails says why.
  Future<void> _take(BuildContext context, PhotoOrigin origin) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final capture = await services.photoSource.pick(origin);
      if (capture == null || !context.mounted) return;
      draft.capture = capture;
      _next(context);
    } on PhotoUnavailable catch (e) {
      messenger.showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Capture Image')),
    body: ListView(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
      children: [
        MediaFrame(
          aspectRatio: 3 / 4,
          maxHeightFactor: 0.5,
          child: Container(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(WoundRadii.l),
              gradient: const RadialGradient(colors: [Color(0xFF33454C), Color(0xFF122330), Color(0xFF050A0D)]),
            ),
            child: Stack(
              children: [
                const Positioned(top: 16, left: 0, right: 0, child: _Hint('Position the wound inside the frame')),
                Center(
                  child: FractionallySizedBox(
                    widthFactor: 0.68,
                    heightFactor: 0.5,
                    child: CustomPaint(painter: _CornersPainter()),
                  ),
                ),
                const Positioned(
                  bottom: 16,
                  left: 0,
                  right: 0,
                  child: _Hint('Place the reference marker beside the wound'),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 14),
        const Text(
          'Use consistent lighting and avoid shadows where possible.',
          textAlign: TextAlign.center,
          style: TextStyle(color: WoundColors.textSecondary, fontSize: 13),
        ),
        const SizedBox(height: 16),
        Center(
          // Screen readers announce this as a button, even though it just looks like a ring.
          child: Semantics(
            button: true,
            label: 'Take picture',
            excludeSemantics: true,
            child: InkWell(
              key: const Key('shutter'),
              customBorder: const CircleBorder(),
              onTap: () => _take(context, PhotoOrigin.camera),
              child: Container(
                width: 72,
                height: 72,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  border: Border.all(color: WoundColors.accent, width: 4),
                  color: WoundColors.surface,
                  boxShadow: WoundShadows.accent,
                ),
                child: Center(
                  child: Container(
                    width: 54,
                    height: 54,
                    decoration: const BoxDecoration(shape: BoxShape.circle, color: WoundColors.accent),
                  ),
                ),
              ),
            ),
          ),
        ),
        const SizedBox(height: 8),
        const Text(
          'Capture image',
          textAlign: TextAlign.center,
          style: TextStyle(fontWeight: FontWeight.w600, fontSize: 12.5, color: WoundColors.textSecondary),
        ),
        const SizedBox(height: 16),
        OutlinedButton.icon(
          onPressed: () => _take(context, PhotoOrigin.device),
          icon: const Icon(Icons.image_outlined, size: 20),
          label: const Text('Choose from device'),
        ),
        const SizedBox(height: 16),
        NoticeStrip(
          services.photoSource.isSample
              ? 'Prototype: the camera and reference-marker detection come with the calibration module. '
                  'Capturing here uses a sample image so the rest of the assessment can be completed.'
              : 'Put the sticker flat beside the wound. The photo is checked on this phone and not uploaded: '
                  'only the measurements are saved.',
          icon: Icons.info_outline_rounded,
        ),
      ],
    ),
  );
}

class _Hint extends StatelessWidget {
  const _Hint(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Center(
    child: Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(color: Colors.black.withValues(alpha: 0.45), borderRadius: BorderRadius.circular(20)),
      child: Text(
        text,
        style: const TextStyle(color: Colors.white, fontSize: 12, fontWeight: FontWeight.w600),
      ),
    ),
  );
}

class _CornersPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()
      ..color = const Color(0xFF5EE6C9)
      ..strokeWidth = 3.5
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round;
    const l = 26.0;
    for (final (x, y, dx, dy) in [
      (0.0, 0.0, 1.0, 1.0),
      (size.width, 0.0, -1.0, 1.0),
      (0.0, size.height, 1.0, -1.0),
      (size.width, size.height, -1.0, -1.0),
    ]) {
      canvas.drawLine(Offset(x, y), Offset(x + dx * l, y), p);
      canvas.drawLine(Offset(x, y), Offset(x, y + dy * l), p);
    }
    canvas.drawLine(
      Offset(8, size.height / 2),
      Offset(size.width - 8, size.height / 2),
      Paint()
        ..color = const Color(0x995EE6C9)
        ..strokeWidth = 1.5,
    );
  }

  @override
  bool shouldRepaint(_CornersPainter old) => false;
}
