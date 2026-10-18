import 'package:flutter/material.dart';

import '../../app/app_services.dart';
import '../../core/theme.dart';
import '../../core/widgets.dart';
import 'guideline_figure.dart';

/// The Recommendation Service's guidance (contracts/rag-response.schema.json 1.0) as delivered, readable offline.
/// Section text is shown exactly as sent; everything else (escalation, withheld, figures, evidence) is what the
/// service's code wrote beside it. Keys the response does not carry are simply not shown.
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
    final citationList = _list('citations');
    final citations = {for (final c in citationList) c['tag'] as String? ?? '': c};
    final withheld = _list('withheld');
    final recordNext = ((payload['nextBestAssessment'] as List?) ?? const []).whereType<String>().toList();
    // The same entries FigureRepository prefetches, so a figure shown here is one the phone may already hold.
    final figures = [
      for (final f in _list('figures'))
        if (f['corpusVersion'] is String && f['figureId'] is String) f,
    ];
    final escalation = payload['escalation'] as Map<String, dynamic>?;
    final extractive = payload['mode'] == 'extractive';
    final trajectory = escalation?['triggered'] == true ? null : _trajectoryNote(escalation);
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
            _EscalationBanner(escalation!),
          ],
          if (trajectory != null) ...[
            const SizedBox(height: 14),
            Row(
              key: const Key('trajectory'),
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(Icons.timeline_rounded, size: 16, color: WoundColors.textTertiary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(trajectory, style: const TextStyle(fontSize: 12.5, color: WoundColors.textSecondary)),
                ),
              ],
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
          if (withheld.isNotEmpty || recordNext.isNotEmpty) ...[
            const SizedBox(height: 4),
            const SectionTitle('Not shown until more is recorded'),
            WoundCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  for (final w in withheld) ...[
                    KeyValueRow(
                      _fieldLabel(w['missingField'] as String? ?? ''),
                      WoundBadge('${w['blockedCount'] ?? 0} withheld', tone: BadgeTone.warning),
                    ),
                    if (w['message'] case final String message when message.isNotEmpty)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 6),
                        child: Text(message, style: const TextStyle(fontSize: 12, color: WoundColors.textTertiary)),
                      ),
                  ],
                  if (recordNext.isNotEmpty) ...[
                    if (withheld.isNotEmpty) const Divider(height: 18),
                    Text(
                      'Record next: ${recordNext.map(_fieldLabel).join(', ')}',
                      key: const Key('recordNext'),
                      style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: WoundColors.accentDark),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 12),
          ],
          if (figures.isNotEmpty) ...[
            const SizedBox(height: 4),
            const SectionTitle('Figures'),
            for (final f in figures) ...[
              GuidelineFigureCard(key: Key('figure-${f['figureId']}'), figures: services.figures, entry: f),
              const SizedBox(height: 12),
            ],
          ],
          if (citationList.isNotEmpty) ...[
            WoundCard(
              padding: EdgeInsets.zero,
              child: Theme(
                // ExpansionTile draws divider lines by default; the card already frames it.
                data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
                child: ExpansionTile(
                  title: Text(
                    'Evidence (${citationList.length} ${citationList.length == 1 ? 'passage' : 'passages'})',
                    style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w700),
                  ),
                  childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 12),
                  children: [for (final c in citationList) _EvidenceRow(c)],
                ),
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

  /// What the trajectory measured when it did not escalate, or why it could not be measured.
  static String? _trajectoryNote(Map<String, dynamic>? escalation) {
    if (escalation == null) return null;
    if (escalation['reason'] case final String reason when reason.isNotEmpty) {
      return 'Healing trajectory not computed: $reason.';
    }
    final par = escalation['par'], weeks = escalation['elapsedWeeks'];
    if (par is! num || weeks is! num) return null;
    final change = par >= 0 ? '${_number(par)}% area reduction' : 'area grew ${_number(-par)}%';
    return 'Healing trajectory: $change over ${_number(weeks)} weeks.';
  }

  /// "pedalPulses" → "Pedal pulses".
  static String _fieldLabel(String field) {
    final words = field.replaceAllMapped(RegExp('([A-Z])'), (m) => ' ${m[1]!.toLowerCase()}').trim();
    return words.isEmpty ? 'More detail' : words[0].toUpperCase() + words.substring(1);
  }
}

/// The service rounds to one decimal; 6.0 reads better as "6".
String _number(num n) {
  final fixed = n.toStringAsFixed(1);
  return fixed.endsWith('.0') ? fixed.substring(0, fixed.length - 2) : fixed;
}

class _EscalationBanner extends StatelessWidget {
  const _EscalationBanner(this.escalation);

  final Map<String, dynamic> escalation;

  @override
  Widget build(BuildContext context) {
    final weeks = escalation['elapsedWeeks'], par = escalation['par'], rule = escalation['rule'];
    final measured = [
      if (weeks is num) 'after ${_number(weeks)} weeks',
      if (par is num) '(${_number(par)}% area reduction)',
    ].join(' ');
    return Container(
      key: const Key('escalation'),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(color: WoundColors.warningSoft, borderRadius: BorderRadius.circular(WoundRadii.m)),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.trending_flat_rounded, color: WoundColors.warning),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Healing may be stalled${measured.isEmpty ? '' : ' $measured'}. Consider specialist review.',
                  style: const TextStyle(color: WoundColors.warning, fontWeight: FontWeight.w700),
                ),
                if (rule is String && rule.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Text('Rule $rule, measured from capture times',
                      style: const TextStyle(fontSize: 12, color: WoundColors.warning)),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// One delivered passage: tag, sub-guideline, recommendation, grade and the PDF page it came from.
class _EvidenceRow extends StatelessWidget {
  const _EvidenceRow(this.citation);

  final Map<String, dynamic> citation;

  @override
  Widget build(BuildContext context) {
    final tag = citation['tag'] as String? ?? '';
    final where = [
      if (citation['source'] case final String source) source,
      if (citation['recommendation'] case final String rec) 'Recommendation $rec',
      if (citation['pdfIndex'] case final num page) 'PDF page $page',
    ].join(' · ');
    return Padding(
      key: Key('evidence-$tag'),
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _CitationChip(tag: tag, source: null),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (where.isNotEmpty) Text(where, style: const TextStyle(fontSize: 12.5)),
                if (citation['grade'] case final String grade)
                  Text(grade, style: const TextStyle(fontSize: 12, color: WoundColors.textSecondary)),
              ],
            ),
          ),
        ],
      ),
    );
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
