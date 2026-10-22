import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../../core/theme.dart';
import '../../../core/widgets.dart';
import 'capture_check.dart';
import 'capture_messages.dart';
import 'capture_verdict.dart';

/// Shown right after a photo is taken or picked, before it is measured: says in plain words whether the photo can be used and, if
/// not, what to change. One verdict, one sentence (what happened, then what to do), and the sticker drawn on the photo so the nurse
/// sees what the app saw.
///
/// ok: measuring is the main button. warn: Retake is the main button and measuring anyway is quieter. retake: Retake is the only
/// way on, except in the demo, which also has photos without a sticker and may measure them without a scale.
class PhotoCheckScreen extends StatelessWidget {
  const PhotoCheckScreen({
    super.key,
    required this.photoBytes,
    required this.photoWidth,
    required this.photoHeight,
    required this.check,
    required this.onMeasure,
    required this.onRetake,
    this.allowMeasureWithoutSticker = false,
    this.title = 'Check the photo',
    this.useLabel = 'Measure this photo',
    this.useAnywayLabel = 'Measure anyway',
    this.anotherLabel = 'Choose another photo',
    this.useKey = const Key('measure'),
    this.withoutLabel = 'Measure without a sticker',
    this.withoutNote = 'Without a sticker there is no area in mm. Demo only.',
    this.onWithoutSticker,
  });

  /// The photo as the camera or gallery gave it (encoded), shown at the size of the screen, not full size.
  final Uint8List photoBytes;
  final int photoWidth, photoHeight;
  final CaptureCheck check;
  final VoidCallback onMeasure, onRetake;

  /// Demo only: lets a photo with no usable sticker still be measured (pixel area only, no mm).
  final bool allowMeasureWithoutSticker;

  /// The words and key of the buttons, so the real app can call them what its other screens do.
  final String title, useLabel, useAnywayLabel, anotherLabel, withoutLabel, withoutNote;
  final Key useKey;

  /// What the "without a sticker" button does; [onMeasure] when null (the demo).
  final VoidCallback? onWithoutSticker;

  @override
  Widget build(BuildContext context) {
    final verdict = check.verdict;
    final (badge, tone, icon, colour) = switch (verdict.verdict) {
      Verdict.ok => ('Ready', BadgeTone.success, Icons.check_rounded, WoundColors.success),
      Verdict.warn => ('Check', BadgeTone.warning, Icons.error_outline_rounded, WoundColors.warning),
      Verdict.retake => ('Retake', BadgeTone.error, Icons.replay_rounded, WoundColors.error),
    };
    final sentence = captureMessage(verdict.code);
    final split = sentence.indexOf('. ');
    final what = split < 0 ? sentence : sentence.substring(0, split + 1);
    final todo = split < 0 ? null : sentence.substring(split + 2);
    final sticker = check.scan.stickers.isEmpty ? null : check.scan.stickers.first;

    return Scaffold(
      appBar: AppBar(title: Text(title)),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.fromLTRB(20, 18, 20, 28),
          children: [
            MediaFrame(
              aspectRatio: photoWidth / photoHeight,
              maxHeightFactor: 0.42,
              child: ClipRRect(
                borderRadius: BorderRadius.circular(WoundRadii.m),
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    Image.memory(photoBytes, fit: BoxFit.fill, cacheWidth: 1200, gaplessPlayback: true),
                    if (sticker != null)
                      CustomPaint(
                        key: const Key('stickerOutline'),
                        painter: _OutlinePainter([for (final c in sticker.corners) Offset(c.$1 / photoWidth, c.$2 / photoHeight)], colour),
                      ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 14),
            Semantics(
              liveRegion: true,
              label: '$badge. $sentence',
              excludeSemantics: true,
              child: WoundCard(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    WoundBadge(badge, tone: tone, icon: icon),
                    const SizedBox(height: 10),
                    Text(what, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700, color: WoundColors.text, height: 1.35)),
                    if (todo != null) ...[
                      const SizedBox(height: 2),
                      Text(todo, style: const TextStyle(fontSize: 15, color: WoundColors.text, height: 1.4)),
                    ],
                    const SizedBox(height: 10),
                    const Text('Checked on this phone. No network used.', style: TextStyle(fontSize: 12, color: WoundColors.textTertiary)),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 22),
            ..._actions(verdict.verdict),
          ],
        ),
      ),
    );
  }

  List<Widget> _actions(Verdict v) => switch (v) {
        Verdict.ok => [
            FilledButton(key: useKey, onPressed: onMeasure, child: Text(useLabel)),
            const SizedBox(height: 10),
            OutlinedButton(onPressed: onRetake, child: Text(anotherLabel)),
          ],
        Verdict.warn => [
            FilledButton(key: const Key('retake'), onPressed: onRetake, child: const Text('Retake')),
            const SizedBox(height: 10),
            OutlinedButton(key: useKey, onPressed: onMeasure, child: Text(useAnywayLabel)),
          ],
        Verdict.retake => [
            FilledButton(key: const Key('retake'), onPressed: onRetake, child: const Text('Retake')),
            if (allowMeasureWithoutSticker) ...[
              const SizedBox(height: 10),
              TextButton(key: const Key('withoutSticker'), onPressed: onWithoutSticker ?? onMeasure, child: Text(withoutLabel)),
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  withoutNote,
                  textAlign: TextAlign.center,
                  style: const TextStyle(fontSize: 12, color: WoundColors.textTertiary),
                ),
              ),
            ],
          ],
      };
}

/// The sticker's outline, with a pale halo so it shows on any photo. [points] are fractions of the photo's width and height.
class _OutlinePainter extends CustomPainter {
  _OutlinePainter(this.points, this.colour);

  final List<Offset> points;
  final Color colour;

  @override
  void paint(Canvas canvas, Size size) {
    final path = Path()..addPolygon([for (final p in points) Offset(p.dx * size.width, p.dy * size.height)], true);
    canvas.drawPath(path, Paint()..style = PaintingStyle.stroke..strokeWidth = 6..color = Colors.white.withValues(alpha: 0.7)..strokeJoin = StrokeJoin.round);
    canvas.drawPath(path, Paint()..style = PaintingStyle.stroke..strokeWidth = 3..color = colour..strokeJoin = StrokeJoin.round);
  }

  @override
  bool shouldRepaint(_OutlinePainter old) => old.colour != colour || old.points != points;
}
