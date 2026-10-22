import 'package:flutter/material.dart';

import '../../../core/theme.dart';
import '../measurement_pipeline.dart';
import '../wound_area.dart';
import 'analysis_session.dart';
import 'colour_bar.dart';
import 'photo_overlay.dart';

/// Shows what the measurement found in one photo: the outline, the colour regions, the area and how long every step took.
class DemoResultScreen extends StatefulWidget {
  const DemoResultScreen({super.key, required this.session});

  final AnalysisSession session;

  @override
  State<DemoResultScreen> createState() => _DemoResultScreenState();
}

class _DemoResultScreenState extends State<DemoResultScreen> {
  MeasurementResult? _result;
  Object? _error;
  bool _busy = true;
  double _confidence = 0.25;
  bool _showOutline = true;

  // Manual scale: two taps on a known length.
  double? _knownMm;
  bool _settingScale = false;
  final List<Offset> _points = [];
  UniformScale? _scale;

  final List<String> _runs = [];

  @override
  void initState() {
    super.initState();
    _run();
  }

  Future<void> _run() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final r = await widget.session.run(_confidence);
      if (!mounted) return;
      final total = r.timingsMs.values.fold<double>(0, (a, b) => a + b);
      setState(() {
        _result = r;
        _busy = false;
        _runs.insert(
          0,
          '${widget.session.usingGpu ? 'GPU' : 'CPU'}, confidence ${_confidence.toStringAsFixed(2)}: '
          'model ${r.timingsMs['segmentation']!.toStringAsFixed(0)} ms, all steps ${total.toStringAsFixed(0)} ms',
        );
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e;
        _busy = false;
      });
    }
  }

  Future<void> _toggleGpu(bool gpu) async {
    setState(() => _busy = true);
    try {
      await widget.session.useGpu(gpu);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = e;
        _busy = false;
      });
      return;
    }
    await _run();
  }

  Future<void> _askLength() async {
    final controller = TextEditingController(text: _knownMm?.toStringAsFixed(0) ?? '50');
    final mm = await showDialog<double>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Known length in the photo'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text('Put a ruler or a coin next to the wound. Enter its real length, then tap its two ends on the photo.'),
            const SizedBox(height: 12),
            TextField(
              controller: controller,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(labelText: 'Length in millimetres'),
            ),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Cancel')),
          FilledButton(
            onPressed: () => Navigator.pop(context, double.tryParse(controller.text.replaceAll(',', '.'))),
            child: const Text('Tap its ends'),
          ),
        ],
      ),
    );
    if (mm == null || mm <= 0) return;
    setState(() {
      _knownMm = mm;
      _settingScale = true;
      _points.clear();
      _scale = null;
    });
  }

  void _onTap(Offset canvasPoint) {
    setState(() {
      _points.add(canvasPoint);
      if (_points.length == 2) {
        final px = (_points[0] - _points[1]).distance;
        if (px > 4) _scale = UniformScale(_knownMm! / px);
        _settingScale = false;
      }
    });
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Wound measurement')),
        body: _body(),
      );

  Widget _body() {
    if (_error != null) {
      return Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.error_outline, size: 40, color: WoundColors.error),
            const SizedBox(height: 12),
            Text('The measurement could not run.\n$_error', textAlign: TextAlign.center),
            const SizedBox(height: 16),
            FilledButton(onPressed: _run, child: const Text('Try again')),
          ],
        ),
      );
    }
    final r = _result;
    if (r == null) {
      return const Center(child: Column(mainAxisSize: MainAxisSize.min, children: [CircularProgressIndicator(), SizedBox(height: 16), Text('Measuring…')]));
    }
    final found = r.segmentation.detections.isNotEmpty;
    final area = found ? _scale?.areaMm2(r.segmentation.mask) : null;
    final pixels = r.segmentation.areaPixels;
    final share = 100 * pixels / (r.image.originalWidth * r.image.scale * r.image.originalHeight * r.image.scale);
    return Stack(
      children: [
        ListView(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 32),
          children: [
            PhotoOverlay(
              result: r,
              showOutline: _showOutline,
              scalePoints: _points,
              onTap: _settingScale ? _onTap : null,
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 8,
              runSpacing: 6,
              children: [
                ChoiceChip(label: const Text('Outline'), selected: _showOutline, onSelected: (_) => setState(() => _showOutline = true)),
                ChoiceChip(label: const Text('Photo only'), selected: !_showOutline, onSelected: (_) => setState(() => _showOutline = false)),
                ActionChip(
                  avatar: const Icon(Icons.straighten, size: 18),
                  label: Text(_scale == null ? 'Set scale' : 'Change scale'),
                  onPressed: found ? _askLength : null,
                ),
              ],
            ),
            if (_settingScale)
              _note('Tap one end of the known length, then the other end (${_points.length} of 2).', WoundColors.warningSoft, WoundColors.warning),
            const SizedBox(height: 10),
            _verdict(r, found),
            const SizedBox(height: 10),
            _card('Size', [
              _row('In the picture', found ? '$pixels pixels (${share.toStringAsFixed(1)}% of the photo)' : '—'),
              _row(
                'Area',
                area != null ? '${area.toStringAsFixed(1)} mm²' : (found ? 'needs a scale: tap "Set scale"' : '—'),
                key: const Key('areaValue'),
                bold: area != null,
              ),
              if (_scale != null) _row('Scale', '${_scale!.mmPerPixel.toStringAsFixed(3)} mm per pixel (from ${_knownMm!.toStringAsFixed(0)} mm)'),
            ]),
            const SizedBox(height: 10),
            _card('Colour regions', [
              ColourBar(regions: r.colourRegions),
              const SizedBox(height: 8),
              const Text(
                'Colour only, not tissue type. The colours depend on the lighting, so compare photos taken the same way.',
                style: TextStyle(color: WoundColors.textSecondary, fontSize: 12.5),
              ),
            ]),
            const SizedBox(height: 10),
            _speedCard(r),
            const SizedBox(height: 10),
            _detailsCard(r),
          ],
        ),
        if (_busy)
          Positioned.fill(
            child: ColoredBox(
              color: Colors.black.withValues(alpha: 0.25),
              child: const Center(child: CircularProgressIndicator()),
            ),
          ),
      ],
    );
  }

  Widget _verdict(MeasurementResult r, bool found) {
    if (!found) {
      return _note('No wound found. Try another photo: keep the phone square to the wound and fill the frame.', WoundColors.warningSoft, WoundColors.warning);
    }
    final n = r.segmentation.detections.length;
    return _note(n == 1 ? 'Wound found.' : '$n wounds found. The size is the total of all of them.', WoundColors.successSoft, WoundColors.success);
  }

  Widget _speedCard(MeasurementResult r) {
    final t = r.timingsMs;
    final total = t.values.fold<double>(0, (a, b) => a + b);
    String ms(String k) => '${(t[k] ?? 0).toStringAsFixed(1)} ms';
    return _card('Speed on this phone', [
      _row('Photo preparation', ms('prepare')),
      _row('Wound model (${widget.session.usingGpu ? 'GPU' : 'CPU'})', ms('segmentation'), bold: true),
      _row('Colour regions', ms('colour')),
      _row('Area', ms('area')),
      _row('Total', '${total.toStringAsFixed(1)} ms', bold: true),
      const SizedBox(height: 8),
      SegmentedButton<bool>(
        segments: const [
          ButtonSegment(value: true, label: Text('GPU'), icon: Icon(Icons.memory)),
          ButtonSegment(value: false, label: Text('CPU'), icon: Icon(Icons.developer_board)),
        ],
        selected: {widget.session.usingGpu},
        onSelectionChanged: _busy ? null : (s) => _toggleGpu(s.first),
      ),
      const SizedBox(height: 8),
      Text('Confidence ${_confidence.toStringAsFixed(2)}  (lower finds more, higher finds only sure wounds)', style: const TextStyle(fontSize: 12.5, color: WoundColors.textSecondary)),
      Slider(
        value: _confidence,
        min: 0.1,
        max: 0.9,
        divisions: 16,
        label: _confidence.toStringAsFixed(2),
        onChanged: (v) => setState(() => _confidence = v),
        onChangeEnd: (_) => _run(),
      ),
      if (_runs.length > 1) ...[
        const Divider(),
        for (final run in _runs.take(5)) Text(run, style: const TextStyle(fontSize: 12.5, color: WoundColors.textSecondary)),
      ],
    ]);
  }

  Widget _detailsCard(MeasurementResult r) {
    String preview;
    try {
      preview = r.toAnalytics().toString();
    } on MeasurementIncomplete catch (e) {
      preview = 'Not complete yet: ${e.reason}. '
          '${e.reason.contains('skin type') ? 'The colour calibration is not in the app yet. ' : ''}'
          'The wound event is only written when the area and the skin type are known.';
    }
    return _card('Details', [
      _row('Photo', widget.session.photoDescription),
      _row('Model', widget.session.modelDescription),
      _row('Calibration', r.calibrationVersion == 'none' ? 'none (not in the demo)' : r.calibrationVersion),
      for (final n in r.notes) _row('Note', n),
      const SizedBox(height: 6),
      const Text('Wound event preview', style: TextStyle(fontWeight: FontWeight.w700)),
      const SizedBox(height: 4),
      Text(preview, key: const Key('analyticsPreview'), style: const TextStyle(fontSize: 12.5, color: WoundColors.textSecondary)),
      const SizedBox(height: 8),
      const Text('This measurement ran entirely on the phone. Nothing was sent anywhere.', style: TextStyle(fontSize: 12.5, color: WoundColors.textSecondary)),
    ]);
  }

  Widget _note(String text, Color bg, Color fg) => Container(
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(WoundRadii.m)),
        child: Text(text, style: TextStyle(color: fg, fontWeight: FontWeight.w600)),
      );

  Widget _card(String title, List<Widget> children) => Container(
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(
          color: WoundColors.surface,
          borderRadius: BorderRadius.circular(WoundRadii.m),
          border: Border.all(color: WoundColors.border),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(title, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15)),
            const SizedBox(height: 8),
            ...children,
          ],
        ),
      );

  Widget _row(String label, String value, {Key? key, bool bold = false}) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(width: 118, child: Text(label, style: const TextStyle(color: WoundColors.textSecondary))),
            Expanded(child: Text(value, key: key, style: TextStyle(fontWeight: bold ? FontWeight.w800 : FontWeight.w500))),
          ],
        ),
      );
}
