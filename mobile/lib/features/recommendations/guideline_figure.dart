import 'package:flutter/material.dart';

import '../../core/theme.dart';
import '../../core/widgets.dart';
import '../sync/data/app_database.dart';
import '../sync/data/figure_repository.dart';

/// One figure the guidance cites, from the phone's cache (fetched once when missing). The image is shown whole and
/// unaltered: no crop, overlay or colour change, since the guideline's licence does not allow adapted copies.
class GuidelineFigureCard extends StatefulWidget {
  const GuidelineFigureCard({super.key, required this.figures, required this.entry});

  final FigureRepository figures;

  /// The response's `figures[]` entry: figureId and corpusVersion, plus label, caption, source, pdfIndex, citedBy
  /// and attribution when the service sends them.
  final Map<String, dynamic> entry;

  @override
  State<GuidelineFigureCard> createState() => _GuidelineFigureCardState();
}

class _GuidelineFigureCardState extends State<GuidelineFigureCard> {
  // Asked once per screen, not on every rebuild.
  late final Future<FigureLookup> _lookup =
      widget.figures.figureFor(widget.entry['corpusVersion'] as String, widget.entry['figureId'] as String);

  String get _label => widget.entry['label'] as String? ?? widget.entry['figureId'] as String;

  String get _whereFrom => [
        if ((widget.entry['citedBy'] as List?)?.isNotEmpty ?? false)
          'Cited by ${(widget.entry['citedBy'] as List).join(', ')}',
        if (widget.entry['source'] case final String source) source,
        if (widget.entry['pdfIndex'] case final num page) 'PDF page $page',
      ].join(' · ');

  @override
  Widget build(BuildContext context) => WoundCard(
        padding: const EdgeInsets.all(16),
        child: FutureBuilder<FigureLookup>(
          future: _lookup,
          builder: (context, snap) {
            final figure = snap.data?.figure;
            final attribution = figure != null && figure.attribution.isNotEmpty
                ? figure.attribution
                : widget.entry['attribution'] as String? ?? '';
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(_label, style: Theme.of(context).textTheme.titleMedium),
                if (_whereFrom.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text(_whereFrom, style: const TextStyle(fontSize: 12, color: WoundColors.textSecondary)),
                ],
                const SizedBox(height: 12),
                if (figure != null) ...[
                  GestureDetector(onTap: () => _openFull(figure, attribution), child: _image(figure)),
                  Align(
                    alignment: Alignment.centerRight,
                    child: TextButton.icon(
                      onPressed: () => _openFull(figure, attribution),
                      icon: const Icon(Icons.zoom_in_rounded, size: 18),
                      label: const Text('View full screen'),
                    ),
                  ),
                ] else
                  _Placeholder(
                    snap.connectionState == ConnectionState.done ? _missText(snap.data?.miss) : 'Loading figure…',
                  ),
                if (widget.entry['caption'] case final String caption when caption.isNotEmpty) ...[
                  const SizedBox(height: 10),
                  Text(caption, style: const TextStyle(fontSize: 13, height: 1.45)),
                ],
                if (attribution.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text(attribution, style: const TextStyle(fontSize: 11.5, color: WoundColors.textTertiary)),
                ],
                if (figure != null && figure.licence.isNotEmpty)
                  Text('Licence: ${figure.licence}', style: const TextStyle(fontSize: 11.5, color: WoundColors.textTertiary)),
              ],
            );
          },
        ),
      );

  void _openFull(FiguresLocalData figure, String attribution) => Navigator.of(context).push(MaterialPageRoute<void>(
        builder: (_) => FigureViewerScreen(title: _label, figure: figure, attribution: attribution),
      ));

  static String _missText(FigureMiss? miss) => switch (miss) {
        FigureMiss.offline => 'The figure shows once the phone is back online.',
        FigureMiss.notFound => 'This figure is not available.',
        FigureMiss.needsSignIn => 'Sign in to load this figure.',
        FigureMiss.unavailable || null => 'The figure could not be loaded. Try again later.',
      };
}

Widget _image(FiguresLocalData figure) => Image.memory(
      figure.bytes,
      fit: BoxFit.contain,
      gaplessPlayback: true,
      errorBuilder: (_, _, _) => const _Placeholder('The figure could not be displayed.'),
    );

class _Placeholder extends StatelessWidget {
  const _Placeholder(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity,
        padding: const EdgeInsets.all(14),
        decoration: BoxDecoration(color: WoundColors.surfaceAlt, borderRadius: BorderRadius.circular(WoundRadii.field)),
        child: Row(
          children: [
            const Icon(Icons.image_outlined, size: 18, color: WoundColors.textTertiary),
            const SizedBox(width: 8),
            Expanded(child: Text(text, style: const TextStyle(fontSize: 12.5, color: WoundColors.textSecondary))),
          ],
        ),
      );
}

/// The figure full screen, to pinch and zoom into small print on a phone.
class FigureViewerScreen extends StatelessWidget {
  const FigureViewerScreen({super.key, required this.title, required this.figure, required this.attribution});

  final String title;
  final FiguresLocalData figure;
  final String attribution;

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(title)),
        body: Column(
          children: [
            Expanded(child: InteractiveViewer(maxScale: 6, child: Center(child: _image(figure)))),
            if (attribution.isNotEmpty)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
                child: Text(attribution, style: const TextStyle(fontSize: 11.5, color: WoundColors.textTertiary)),
              ),
          ],
        ),
      );
}
