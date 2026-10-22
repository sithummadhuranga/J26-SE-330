import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/colour_regions.dart';
import 'package:melanin_wound_cdss/features/measurement/demo/colour_bar.dart';

Widget host(List<ColourRegion> regions, {double width = 400}) =>
    MaterialApp(home: Scaffold(body: Align(alignment: Alignment.topLeft, child: SizedBox(width: width, child: ColourBar(regions: regions)))));

void main() {
  testWidgets('without wound pixels it says so instead of drawing a bar', (tester) async {
    await tester.pumpWidget(host(const []));
    expect(find.text('No wound pixels to group.'), findsOneWidget);
    expect(find.byKey(const Key('colourBar1')), findsNothing);
  });

  testWidgets('the segments are as wide as the shares and use the group colours', (tester) async {
    await tester.pumpWidget(host(const [
      ColourRegion(1, 60.0, [200, 40, 40]),
      ColourRegion(2, 30.0, [60, 60, 200]),
      ColourRegion(3, 10.0, [30, 200, 30]),
    ]));
    final w = [for (final k in [1, 2, 3]) tester.getSize(find.byKey(Key('colourBar$k'))).width];
    expect(w[0] / w[1], closeTo(2.0, 0.05));
    expect(w[1] / w[2], closeTo(3.0, 0.1));
    expect(w.reduce((a, b) => a + b), closeTo(400, 0.5));
    final box = tester.widget<Container>(find.byKey(const Key('colourBar1')));
    expect(box.color, const Color.fromARGB(255, 200, 40, 40));
  });

  testWidgets('the legend names each group with its share and its RGB values, and never a tissue', (tester) async {
    await tester.pumpWidget(host(const [ColourRegion(1, 55.6, [93.4, 32.0, 38.0]), ColourRegion(2, 44.4, [120, 60, 40])]));
    expect(find.text('Group 1'), findsOneWidget);
    expect(find.text('55.6%'), findsOneWidget);
    expect(find.text('RGB 93, 32, 38'), findsOneWidget);
    expect(find.text('44.4%'), findsOneWidget);
    expect(find.textContaining('tissue'), findsNothing);
  });

  testWidgets('a very small group still gets a visible segment', (tester) async {
    await tester.pumpWidget(host(const [ColourRegion(1, 99.95, [200, 40, 40]), ColourRegion(2, 0.05, [60, 60, 200])]));
    expect(tester.getSize(find.byKey(const Key('colourBar2'))).width, greaterThan(0.3));
  });

  testWidgets('the legend does not overflow on a narrow phone', (tester) async {
    await tester.pumpWidget(host(const [ColourRegion(1, 55.6, [193, 132, 138]), ColourRegion(2, 37.8, [107, 51, 53]), ColourRegion(3, 6.6, [128, 94, 78])], width: 260));
    expect(tester.takeException(), isNull);                                   // an overflow would be reported as an exception
    expect(find.text('Group 3'), findsOneWidget);
  });
}
