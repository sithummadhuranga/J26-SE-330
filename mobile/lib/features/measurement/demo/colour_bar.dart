import 'package:flutter/material.dart';

import '../../../core/theme.dart';
import '../colour_regions.dart';

/// The wound's colour regions as one bar split by share, with a legend. Colour only: nothing here says what tissue it is.
class ColourBar extends StatelessWidget {
  const ColourBar({super.key, required this.regions});

  final List<ColourRegion> regions;

  Color _color(ColourRegion r) => Color.fromARGB(255, r.centre[0].round(), r.centre[1].round(), r.centre[2].round());

  @override
  Widget build(BuildContext context) {
    if (regions.isEmpty) {
      return const Text('No wound pixels to group.', style: TextStyle(color: WoundColors.textSecondary));
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ClipRRect(
          borderRadius: BorderRadius.circular(8),
          child: SizedBox(
            height: 26,
            child: Row(
              children: [
                for (final r in regions)
                  Expanded(
                    flex: (r.percent * 10).round().clamp(1, 1000),
                    child: Container(key: Key('colourBar${r.cluster}'), color: _color(r)),
                  ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 10),
        for (final r in regions)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 3),
            child: Row(
              children: [
                Container(
                  width: 16,
                  height: 16,
                  decoration: BoxDecoration(
                    color: _color(r),
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: WoundColors.borderStrong),
                  ),
                ),
                const SizedBox(width: 10),
                Text('Group ${r.cluster}', style: const TextStyle(fontWeight: FontWeight.w600)),
                const Spacer(),
                Text('${r.percent.toStringAsFixed(1)}%', style: const TextStyle(fontWeight: FontWeight.w700)),
                const SizedBox(width: 12),
                Text(
                  'RGB ${r.centre[0].round()}, ${r.centre[1].round()}, ${r.centre[2].round()}',
                  style: const TextStyle(color: WoundColors.textSecondary, fontSize: 12.5),
                ),
              ],
            ),
          ),
      ],
    );
  }
}
