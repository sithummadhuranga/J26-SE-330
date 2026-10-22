import 'package:flutter/material.dart';

import '../../core/theme.dart';
import '../../core/widgets.dart';
import 'chat_repository.dart';

/// DRAFT (not agreed with Member 4): follow-up questions on one delivered advice. Every answer line is the service's
/// checked statement with its sources; a question it could not answer shows the service's fixed message. The saved
/// conversation stays readable offline; asking needs a connection.
class ChatScreen extends StatefulWidget {
  const ChatScreen({super.key, required this.chats, required this.assessmentId, required this.revision});

  final ChatRepository chats;
  final String assessmentId;
  final int revision;

  @override
  State<ChatScreen> createState() => _ChatScreenState();
}

class _ChatScreenState extends State<ChatScreen> {
  final _question = TextEditingController();
  ChatState _state = const ChatState(null);
  bool _loading = true;
  bool _asking = false;
  String _language = 'auto';

  static const _languages = {'auto': 'Same as question', 'en': 'English', 'si': 'Sinhala', 'ta': 'Tamil'};

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _question.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    final saved = await widget.chats.cached(widget.assessmentId, widget.revision);
    if (mounted && saved != null) setState(() => _state = ChatState(saved));
    final opened = await widget.chats.open(widget.assessmentId, widget.revision);
    if (!mounted) return;
    setState(() {
      _state = opened;
      _loading = false;
    });
  }

  Future<void> _ask() async {
    final question = _question.text.trim();
    if (question.isEmpty || _asking) return;
    setState(() => _asking = true);
    final result = await widget.chats.ask(widget.assessmentId, widget.revision, question, language: _language);
    if (!mounted) return;
    setState(() {
      _state = result;
      _asking = false;
      if (result.problem == null) _question.clear();
    });
  }

  bool get _online => _state.problem != ChatProblem.offline && _state.problem != ChatProblem.needsSignIn;

  @override
  Widget build(BuildContext context) {
    final turns = _state.turns;
    return Scaffold(
      appBar: AppBar(title: const Text('Ask about this guidance')),
      body: Column(
        children: [
          Expanded(
            child: ListView(
              padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
              children: [
                if (_problemText() case final text?) ...[
                  Container(
                    key: Key(_state.problem == ChatProblem.offline ? 'chatOffline' : 'chatProblem'),
                    padding: const EdgeInsets.all(12),
                    decoration: BoxDecoration(color: WoundColors.warningSoft, borderRadius: BorderRadius.circular(WoundRadii.field)),
                    child: Text(text, style: const TextStyle(color: WoundColors.warning, fontWeight: FontWeight.w600)),
                  ),
                  const SizedBox(height: 12),
                ],
                if (_loading && turns.isEmpty) const Center(child: Padding(padding: EdgeInsets.all(24), child: CircularProgressIndicator())),
                if (!_loading && turns.isEmpty)
                  const EmptyState(
                    icon: Icons.forum_outlined,
                    title: 'No questions yet',
                    message: 'Answers come only from the guideline passages approved for this assessment, each with its source.',
                  ),
                for (final t in turns) ...[_Turn(t), const SizedBox(height: 14)],
                if (_asking)
                  const Row(children: [
                    SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)),
                    SizedBox(width: 10),
                    Expanded(child: Text('Checking the answer against the guideline…')),
                  ]),
              ],
            ),
          ),
          _input(),
        ],
      ),
    );
  }

  String? _problemText() => switch (_state.problem) {
        ChatProblem.offline => 'Saved conversation. Asking a new question needs a connection.',
        ChatProblem.needsSignIn => 'Sign in again to ask questions.',
        ChatProblem.notAvailable => 'This advice cannot be discussed: the server has no answer for it.',
        ChatProblem.rejected => 'The question was not accepted. Keep it under 1,000 characters.',
        ChatProblem.conflict => 'Another question was answered at the same moment. Close and reopen this chat.',
        ChatProblem.unavailable => 'The answer service cannot be reached just now. Try again in a moment.',
        null => null,
      };

  Widget _input() => SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 4, 16, 12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              const NoticeStrip(
                'Do not type names, phone or ID numbers. Recognised details are removed before sending, but a name '
                'without a title (Mr, Mrs, Dr) is not.',
                key: Key('chatPrivacyNote'),
                icon: Icons.privacy_tip_outlined,
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  DropdownButton<String>(
                    key: const Key('chatLanguage'),
                    value: _language,
                    underline: const SizedBox(),
                    items: [for (final e in _languages.entries) DropdownMenuItem(value: e.key, child: Text(e.value))],
                    onChanged: (v) => setState(() => _language = v ?? 'auto'),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: TextField(
                      key: const Key('chatQuestion'),
                      controller: _question,
                      enabled: _online && !_asking,
                      maxLength: 1000,
                      minLines: 1,
                      maxLines: 4,
                      decoration: const InputDecoration(hintText: 'Ask about this guidance', counterText: ''),
                    ),
                  ),
                  IconButton(
                    key: const Key('chatSend'),
                    icon: const Icon(Icons.send_rounded),
                    color: WoundColors.accent,
                    onPressed: _online && !_asking && !_loading ? _ask : null,
                  ),
                ],
              ),
            ],
          ),
        ),
      );
}

/// One stored turn: the question as the service stored it, then its checked statements or its fixed message.
class _Turn extends StatelessWidget {
  const _Turn(this.turn);

  final Map<String, dynamic> turn;

  @override
  Widget build(BuildContext context) {
    final statements = ((turn['statements'] as List?) ?? const []).whereType<Map<String, dynamic>>().toList();
    final citations = (turn['citations'] as Map<String, dynamic>?) ?? const {};
    final notices = ((turn['notices'] as List?) ?? const []).whereType<String>().toList();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Align(
          alignment: Alignment.centerRight,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
            decoration: BoxDecoration(color: WoundColors.accentSoft, borderRadius: BorderRadius.circular(WoundRadii.m)),
            child: Text(turn['question'] as String? ?? '', style: const TextStyle(color: WoundColors.accentDark)),
          ),
        ),
        const SizedBox(height: 8),
        WoundCard(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              if (statements.isEmpty)
                Text(turn['message'] as String? ?? '', style: const TextStyle(color: WoundColors.textSecondary, height: 1.45)),
              for (final s in statements)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(s['text'] as String? ?? '', style: const TextStyle(fontSize: 14, height: 1.5)),
                      const SizedBox(height: 4),
                      Wrap(spacing: 6, children: [for (final tag in (s['cite'] as List? ?? const []).cast<String>()) _Tag(tag)]),
                    ],
                  ),
                ),
              if (citations.isNotEmpty) ...[
                const Divider(height: 18),
                for (final e in citations.entries)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Text(_source(e.key, e.value as Map<String, dynamic>),
                        style: const TextStyle(fontSize: 12, color: WoundColors.textSecondary)),
                  ),
              ],
              for (final n in notices) ...[
                const SizedBox(height: 6),
                Text(n, style: const TextStyle(fontSize: 12, color: WoundColors.textTertiary)),
              ],
            ],
          ),
        ),
      ],
    );
  }

  // "S1 · PAD · Recommendation 3 · Strong; Moderate · PDF page 12"; the PDF page index, not the printed number.
  static String _source(String tag, Map<String, dynamic> c) => [
        tag,
        if (c['source'] case final String s) s,
        if (c['recommendation'] case final String r) 'Recommendation $r',
        if (c['grade'] case final String g) g,
        if (c['pdfIndex'] case final num p) 'PDF page $p',
      ].join(' · ');
}

class _Tag extends StatelessWidget {
  const _Tag(this.tag);

  final String tag;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
        decoration: BoxDecoration(color: WoundColors.accentSoft, borderRadius: BorderRadius.circular(6)),
        child: Text(tag, style: const TextStyle(color: WoundColors.accentDark, fontSize: 11, fontWeight: FontWeight.w700)),
      );
}
