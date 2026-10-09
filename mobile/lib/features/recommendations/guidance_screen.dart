import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';

/// Shows the Recommendation Service's guidance as-is; Member 3 owns the final design.
class GuidanceScreen extends StatelessWidget {
  const GuidanceScreen({super.key, required this.services, required this.payload, required this.receivedAt});

  final AppServices services;
  final Map<String, dynamic> payload;
  final DateTime receivedAt;

  List<Map<String, dynamic>> _list(String key) =>
      ((payload[key] as List?) ?? const []).whereType<Map<String, dynamic>>().toList();

  @override
  Widget build(BuildContext context) {
    final sections = _list('sections');
    final citations = {for (final c in _list('citations')) c['tag'] as String? ?? '': c};
    final withheld = _list('withheld');
    final escalation = payload['escalation'] as Map<String, dynamic>?;
    final extractive = payload['mode'] == 'extractive';
    return Scaffold(
      appBar: AppBar(title: const Text('Evidence-Based Guidance')),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 28),
        children: [
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Flexible(
                child: WoundBadge(
                  extractive ? 'Guideline extracts' : 'Generated summary',
                  tone: BadgeTone.accent,
                  icon: Icons.menu_book_outlined,
                ),
              ),
              const SizedBox(width: 12),
              Flexible(
                child: Text(
                  'Received ${formatTime(receivedAt)}',
                  textAlign: TextAlign.end,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall,
                ),
              ),
            ],
          ),
          if (escalation?['triggered'] == true) ...[
            const SizedBox(height: 14),
            Container(
              key: const Key('escalation'),
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: WoundColors.warningSoft,
                borderRadius: BorderRadius.circular(WoundRadii.m),
              ),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(Icons.trending_flat_rounded, color: WoundColors.warning),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      'Healing may be stalled'
                      '${escalation?['elapsedWeeks'] == null ? '' : ' after ${escalation!['elapsedWeeks']} weeks'}. '
                      'Consider specialist review.',
                      style: const TextStyle(color: WoundColors.warning, fontWeight: FontWeight.w700),
                    ),
                  ),
                ],
              ),
            ),
          ],
          const SizedBox(height: 14),
          if (sections.isEmpty)
            const EmptyState(
              icon: Icons.menu_book_outlined,
              title: 'No guidance sections',
              message: 'The service returned no guidance for this assessment.',
            ),
          for (final s in sections) ...[
            WoundCard(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(s['heading'] as String? ?? '', style: Theme.of(context).textTheme.titleMedium),
                  const SizedBox(height: 6),
                  Text(s['text'] as String? ?? '', style: const TextStyle(fontSize: 14, height: 1.5)),
                  if ((s['citationTags'] as List?)?.isNotEmpty ?? false) ...[
                    const SizedBox(height: 12),
                    Wrap(
                      spacing: 6,
                      runSpacing: 6,
                      children: [
                        for (final tag in (s['citationTags'] as List).cast<String>())
                          _CitationChip(tag: tag, source: citations[tag]?['source'] as String?),
                      ],
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 12),
          ],
          if (withheld.isNotEmpty) ...[
            const SizedBox(height: 4),
            const SectionTitle('Not shown until more is recorded'),
            WoundCard(
              child: Column(
                children: [
                  for (final w in withheld)
                    KeyValueRow(
                      _fieldLabel(w['missingField'] as String? ?? ''),
                      WoundBadge('${w['blockedCount'] ?? 0} withheld', tone: BadgeTone.warning),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),
          ],
          const NoticeStrip(
            'Decision-support information only. Guidance is drawn from published guidelines; clinical assessment and '
            'treatment decisions remain with the clinician.',
            icon: Icons.info_outline_rounded,
          ),
          const SizedBox(height: 10),
          Text(
            'Guideline snapshot ${payload['corpusVersion'] ?? '—'}',
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 11.5, color: WoundColors.textTertiary),
          ),
        ],
      ),
    );
  }

  /// "pedalPulses" → "Pedal pulses".
  static String _fieldLabel(String field) {
    final words = field.replaceAllMapped(RegExp('([A-Z])'), (m) => ' ${m[1]!.toLowerCase()}').trim();
    return words.isEmpty ? 'More detail' : words[0].toUpperCase() + words.substring(1);
  }
}

class _CitationChip extends StatelessWidget {
  const _CitationChip({required this.tag, required this.source});

  final String tag;
  final String? source;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 5),
    decoration: BoxDecoration(color: WoundColors.accentSoft, borderRadius: BorderRadius.circular(8)),
    child: Text(
      source == null ? tag : '$tag · $source',
      style: const TextStyle(color: WoundColors.accentDark, fontSize: 11.5, fontWeight: FontWeight.w700),
    ),
  );
}
