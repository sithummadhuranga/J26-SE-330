import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../../../core/theme.dart';
import 'analysis_session.dart';
import 'demo_result_screen.dart';

/// Start screen of the wound measurement demo: take a photo, pick one, or use a sample photo.
class DemoHomeScreen extends StatefulWidget {
  const DemoHomeScreen({super.key});

  @override
  State<DemoHomeScreen> createState() => _DemoHomeScreenState();
}

class _DemoHomeScreenState extends State<DemoHomeScreen> {
  final DemoEngine _engine = DemoEngine();
  bool _loading = true;
  List<File> _samples = const [];

  @override
  void initState() {
    super.initState();
    _refresh();
  }

  @override
  void dispose() {
    _engine.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    setState(() => _loading = true);
    await _engine.load();
    final files = await DemoEngine.samplePhotos();
    if (!mounted) return;
    setState(() {
      _samples = files;
      _loading = false;
    });
  }

  Future<void> _analyse(Future<List<int>?> bytes) async {
    if (!_engine.ready) return;
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => const Center(child: CircularProgressIndicator()),
    );
    DecodedPhoto? photo;
    try {
      final data = await bytes;
      if (data != null) photo = await decodePhoto(Uint8List.fromList(data));
    } finally {
      if (mounted) Navigator.of(context, rootNavigator: true).pop();
    }
    if (photo == null || !mounted) return;
    await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => DemoResultScreen(session: EngineSession(_engine, photo!))));
  }

  Future<List<int>?> _pick(ImageSource source) async {
    final file = await ImagePicker().pickImage(source: source);
    return file?.readAsBytes();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Wound measurement demo')),
        body: _loading
            ? const Center(child: CircularProgressIndicator())
            : ListView(
                padding: const EdgeInsets.all(16),
                children: [
                  _modelCard(),
                  const SizedBox(height: 14),
                  FilledButton.icon(
                    onPressed: _engine.ready ? () => _analyse(_pick(ImageSource.camera)) : null,
                    icon: const Icon(Icons.photo_camera),
                    label: const Text('Scan a wound with the camera'),
                  ),
                  const SizedBox(height: 10),
                  OutlinedButton.icon(
                    onPressed: _engine.ready ? () => _analyse(_pick(ImageSource.gallery)) : null,
                    icon: const Icon(Icons.photo_library_outlined),
                    label: const Text('Choose a photo'),
                  ),
                  const SizedBox(height: 22),
                  const Text('Sample photos', style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
                  const SizedBox(height: 4),
                  Text(
                    _samples.isEmpty
                        ? 'None on this phone yet. Copy photos into the demo with push_demo_photos.sh.'
                        : 'Tap a photo to measure it. They are stored only inside this app, not in the gallery.',
                    style: const TextStyle(color: WoundColors.textSecondary, fontSize: 13),
                  ),
                  const SizedBox(height: 10),
                  if (_samples.isNotEmpty)
                    GridView.count(
                      crossAxisCount: 3,
                      mainAxisSpacing: 8,
                      crossAxisSpacing: 8,
                      shrinkWrap: true,
                      physics: const NeverScrollableScrollPhysics(),
                      children: [
                        for (final f in _samples)
                          InkWell(
                            onTap: _engine.ready ? () => _analyse(f.readAsBytes()) : null,
                            borderRadius: BorderRadius.circular(12),
                            child: ClipRRect(
                              borderRadius: BorderRadius.circular(12),
                              child: Image.file(f, fit: BoxFit.cover, cacheWidth: 360),
                            ),
                          ),
                      ],
                    ),
                  const SizedBox(height: 22),
                  const Text(
                    'Research prototype, not for clinical use. The measurement runs on this phone and uses no network.',
                    style: TextStyle(color: WoundColors.textTertiary, fontSize: 12.5),
                  ),
                ],
              ),
      );

  Widget _modelCard() {
    final ok = _engine.ready;
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: ok ? WoundColors.successSoft : WoundColors.warningSoft,
        borderRadius: BorderRadius.circular(WoundRadii.m),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(ok ? Icons.check_circle : Icons.info_outline, color: ok ? WoundColors.success : WoundColors.warning),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  ok ? 'Model ready (${_engine.gpuActive ? 'GPU' : 'CPU'})' : 'The model is not ready',
                  style: const TextStyle(fontWeight: FontWeight.w800),
                ),
              ),
              TextButton(onPressed: _refresh, child: const Text('Check again')),
            ],
          ),
          if (ok)
            Text('${_engine.versionName}, ${((_engine.modelBytes ?? 0) / 1048576).toStringAsFixed(1)} MB${_engine.gpuNote == null ? '' : '\n${_engine.gpuNote}'}',
                style: const TextStyle(color: WoundColors.textSecondary, fontSize: 13))
          else
            Text(_engine.loadError ?? 'Unknown problem.', style: const TextStyle(color: WoundColors.textSecondary, fontSize: 13)),
        ],
      ),
    );
  }
}
