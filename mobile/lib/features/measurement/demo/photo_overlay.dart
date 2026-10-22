import 'dart:async';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';

import '../measurement_pipeline.dart';
import '../yolo_seg_postprocess.dart';

/// A transparent picture of the wound mask: a red tint inside and a white edge, same size as the model input.
Uint8List buildOverlayRgba(Uint8List mask, {int width = kInputW, int height = kInputH}) {
  final out = Uint8List(width * height * 4);
  bool on(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && mask[y * width + x] != 0;
  for (var y = 0; y < height; y++) {
    for (var x = 0; x < width; x++) {
      if (!on(x, y)) continue;
      final edge = !on(x - 1, y) || !on(x + 1, y) || !on(x, y - 1) || !on(x, y + 1);
      final o = 4 * (y * width + x);
      if (edge) {
        out.setRange(o, o + 4, const [255, 255, 255, 255]);
      } else {
        out.setRange(o, o + 4, const [255, 64, 56, 105]);
      }
    }
  }
  return out;
}

Future<ui.Image> _toImage(Uint8List rgba, int width, int height) {
  final done = Completer<ui.Image>();
  ui.decodeImageFromPixels(rgba, width, height, ui.PixelFormat.rgba8888, done.complete);
  return done.future;
}

/// The photo with the wound outline on top. When [onTap] is given, taps are reported in model-input pixels (used to mark
/// a known length for the scale) and the points in [scalePoints] are drawn.
class PhotoOverlay extends StatefulWidget {
  const PhotoOverlay({
    super.key,
    required this.result,
    required this.showOutline,
    this.scalePoints = const [],
    this.onTap,
  });

  final MeasurementResult result;
  final bool showOutline;
  final List<Offset> scalePoints;
  final void Function(Offset canvasPoint)? onTap;

  @override
  State<PhotoOverlay> createState() => _PhotoOverlayState();
}

class _PhotoOverlayState extends State<PhotoOverlay> {
  ui.Image? _photo, _overlay;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(PhotoOverlay old) {
    super.didUpdateWidget(old);
    if (!identical(old.result, widget.result)) _load();
  }

  Future<void> _load() async {
    final r = widget.result;
    final photo = await _toImage(r.image.rgba, kInputW, kInputH);
    final overlay = await _toImage(buildOverlayRgba(r.segmentation.mask), kInputW, kInputH);
    if (!mounted) return;
    setState(() {
      _photo = photo;
      _overlay = overlay;
    });
  }

  @override
  Widget build(BuildContext context) {
    final img = widget.result.image;
    final contentW = (kInputW - 2 * img.padLeft).toDouble(), contentH = (kInputH - 2 * img.padTop).toDouble();
    final src = Rect.fromLTWH(img.padLeft.toDouble(), img.padTop.toDouble(), contentW, contentH);
    return AspectRatio(
      aspectRatio: contentW / contentH,
      child: LayoutBuilder(
        builder: (context, box) => GestureDetector(
          onTapDown: widget.onTap == null
              ? null
              : (d) => widget.onTap!(Offset(
                    src.left + d.localPosition.dx / box.maxWidth * src.width,
                    src.top + d.localPosition.dy / box.maxHeight * src.height,
                  )),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(14),
            child: CustomPaint(
              painter: _OverlayPainter(_photo, _overlay, src, widget.scalePoints, widget.showOutline),
              child: const SizedBox.expand(),
            ),
          ),
        ),
      ),
    );
  }
}

class _OverlayPainter extends CustomPainter {
  _OverlayPainter(this.photo, this.overlay, this.src, this.points, this.showOutline);

  final ui.Image? photo, overlay;
  final Rect src;
  final List<Offset> points;
  final bool showOutline;

  @override
  void paint(Canvas canvas, Size size) {
    final dst = Offset.zero & size;
    canvas.drawRect(dst, Paint()..color = const Color(0xFF1D1C18));
    if (photo != null) canvas.drawImageRect(photo!, src, dst, Paint()..filterQuality = FilterQuality.medium);
    if (showOutline && overlay != null) canvas.drawImageRect(overlay!, src, dst, Paint());

    Offset map(Offset p) => Offset((p.dx - src.left) / src.width * size.width, (p.dy - src.top) / src.height * size.height);
    final line = Paint()
      ..color = const Color(0xFFFFD54F)
      ..strokeWidth = 2.5;
    if (points.length == 2) canvas.drawLine(map(points[0]), map(points[1]), line);
    for (final p in points) {
      final c = map(p);
      canvas.drawCircle(c, 7, Paint()..color = const Color(0xFFFFD54F));
      canvas.drawCircle(c, 7, Paint()
        ..color = Colors.black
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5);
    }
  }

  @override
  bool shouldRepaint(_OverlayPainter old) =>
      photo != old.photo || overlay != old.overlay || points != old.points || showOutline != old.showOutline || src != old.src;
}
