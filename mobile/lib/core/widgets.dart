import 'package:flutter/material.dart';

import 'theme.dart';

/// The title row at the top of a tab page, with an optional action on the right. Tabs don't have a back button.
class TabHeader extends StatelessWidget {
  const TabHeader({super.key, required this.title, this.action});

  final String title;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.fromLTRB(20, 12, 12, 12),
    decoration: const BoxDecoration(
      border: Border(bottom: BorderSide(color: WoundColors.border)),
    ),
    child: Row(
      children: [
        Expanded(child: Text(title, style: Theme.of(context).textTheme.titleLarge)),
        ?action,
      ],
    ),
  );
}

/// A white rounded card with the soft shadow from the prototype.
class WoundCard extends StatelessWidget {
  const WoundCard({super.key, required this.child, this.padding = const EdgeInsets.all(14), this.onTap});

  final Widget child;
  final EdgeInsetsGeometry padding;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => Material(
    color: WoundColors.surface,
    borderRadius: BorderRadius.circular(WoundRadii.m),
    child: InkWell(
      borderRadius: BorderRadius.circular(WoundRadii.m),
      onTap: onTap,
      child: Ink(
        // The fill covers the shadow. Without it, the blurred shadow shows through and the card looks grey.
        decoration: BoxDecoration(
          color: WoundColors.surface,
          borderRadius: BorderRadius.circular(WoundRadii.m),
          border: Border.all(color: WoundColors.border),
          boxShadow: WoundShadows.card,
        ),
        child: Padding(padding: padding, child: child),
      ),
    ),
  );
}

enum BadgeTone { success, warning, error, neutral, accent }

/// A small rounded status label, like "Synced", "Advice ready" or "Detected".
class WoundBadge extends StatelessWidget {
  const WoundBadge(this.label, {super.key, this.tone = BadgeTone.neutral, this.icon});

  final String label;
  final BadgeTone tone;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    final (bg, fg) = switch (tone) {
      BadgeTone.success => (WoundColors.successSoft, WoundColors.success),
      BadgeTone.warning => (WoundColors.warningSoft, WoundColors.warning),
      BadgeTone.error => (WoundColors.errorSoft, WoundColors.error),
      BadgeTone.accent => (WoundColors.accentSoft, WoundColors.accentDark),
      BadgeTone.neutral => (WoundColors.surfaceAlt, WoundColors.textSecondary),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 9, vertical: 4),
      decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(20)),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[Icon(icon, size: 12, color: fg), const SizedBox(width: 4)],
          // On small phones or with large system text, cut it short with an ellipsis rather than overflowing.
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(color: fg, fontSize: 11.5, fontWeight: FontWeight.w700),
            ),
          ),
        ],
      ),
    );
  }
}

/// The small bold heading above a group, like "Recent assessments" or "Settings".
class SectionTitle extends StatelessWidget {
  const SectionTitle(this.text, {super.key, this.trailing});

  final String text;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 10),
    child: Row(
      children: [
        Expanded(
          child: Text(
            text,
            style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: WoundColors.textSecondary),
          ),
        ),
        ?trailing,
      ],
    ),
  );
}

/// A low-key information strip for privacy and decision-support notices.
class NoticeStrip extends StatelessWidget {
  const NoticeStrip(this.text, {super.key, this.icon = Icons.shield_outlined});

  final String text;
  final IconData icon;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.all(12),
    decoration: BoxDecoration(color: WoundColors.surfaceAlt, borderRadius: BorderRadius.circular(WoundRadii.field)),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 15, color: WoundColors.textTertiary),
        const SizedBox(width: 8),
        Expanded(
          child: Text(text, style: const TextStyle(fontSize: 12, color: WoundColors.textSecondary, height: 1.45)),
        ),
      ],
    ),
  );
}

/// A label that sits above a field, the way the prototype lays out its forms.
class FieldLabel extends StatelessWidget {
  const FieldLabel(this.text, {super.key});

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 6),
    child: Text(
      text,
      style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: WoundColors.textSecondary),
    ),
  );
}

/// A key–value row inside a card.
class KeyValueRow extends StatelessWidget {
  const KeyValueRow(this.label, this.value, {super.key});

  final String label;
  final Widget value;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Row(
      children: [
        Expanded(
          child: Text(label, style: const TextStyle(fontSize: 13.5, color: WoundColors.textSecondary)),
        ),
        value,
      ],
    ),
  );
}

/// A round avatar showing initials, for a patient or a clinician.
class InitialsAvatar extends StatelessWidget {
  const InitialsAvatar(this.text, {super.key, this.size = 44});

  final String text;
  final double size;

  /// Works out initials from a label. For example "AF bed 12" gives AF, "Demo Admin" gives DA
  /// and "admin.demo" gives AD.
  static String initialsOf(String s) {
    final parts = s.trim().split(RegExp(r'[\s.,_-]+')).where((p) => p.isNotEmpty).toList();
    if (parts.isEmpty) return '?';
    final first = parts.first;
    if (parts.length == 1 || (first.length == 2 && first == first.toUpperCase())) {
      return first.substring(0, first.length.clamp(1, 2)).toUpperCase();
    }
    return (first[0] + parts[1][0]).toUpperCase();
  }

  @override
  Widget build(BuildContext context) => Container(
    width: size,
    height: size,
    alignment: Alignment.center,
    decoration: const BoxDecoration(color: WoundColors.accentSoft, shape: BoxShape.circle),
    child: Text(
      initialsOf(text),
      style: TextStyle(color: WoundColors.accentDark, fontWeight: FontWeight.w800, fontSize: size * 0.33),
    ),
  );
}

/// What we show when a list is empty: a title and one line of help, centred.
class EmptyState extends StatelessWidget {
  const EmptyState({super.key, required this.icon, required this.title, required this.message});

  final IconData icon;
  final String title;
  final String message;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 36, horizontal: 16),
    child: Column(
      children: [
        Container(
          width: 48,
          height: 48,
          decoration: BoxDecoration(color: WoundColors.surfaceAlt, borderRadius: BorderRadius.circular(16)),
          child: Icon(icon, color: WoundColors.textTertiary),
        ),
        const SizedBox(height: 14),
        Text(title, style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 4),
        Text(
          message,
          textAlign: TextAlign.center,
          style: const TextStyle(color: WoundColors.textSecondary),
        ),
      ],
    ),
  );
}

/// Turns the contract's mm² into cm², which is what the prototype shows.
String formatArea(num areaMm2) => '${(areaMm2 / 100).toStringAsFixed(2)} cm²';

String formatDate(DateTime t) {
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  final l = t.toLocal();
  return '${l.day} ${months[l.month - 1]} ${l.year}';
}

String formatTime(DateTime t) {
  final l = t.toLocal();
  final h = l.hour % 12 == 0 ? 12 : l.hour % 12;
  return '$h:${l.minute.toString().padLeft(2, '0')} ${l.hour < 12 ? 'AM' : 'PM'}';
}

/// An image area that takes up at most [maxHeightFactor] of the screen, so the controls below stay visible.
class MediaFrame extends StatelessWidget {
  const MediaFrame({super.key, required this.aspectRatio, required this.child, this.maxHeightFactor = 0.45});

  final double aspectRatio;
  final double maxHeightFactor;
  final Widget child;

  @override
  Widget build(BuildContext context) => Center(
    child: ConstrainedBox(
      constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * maxHeightFactor),
      child: AspectRatio(aspectRatio: aspectRatio, child: child),
    ),
  );
}
