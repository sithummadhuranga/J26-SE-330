import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/assessment/assessment_view.dart';
import 'package:melanin_wound_cdss/features/assessment/media_store.dart';

import '../measurement/analysis_session_test.dart' show tinyPng;

WoundOutline _outline() {
  final mask = Uint8List(640 * 480);
  for (var y = 100; y < 120; y++) {
    for (var x = 200; x < 250; x++) {
      mask[y * 640 + x] = 1;
    }
  }
  return WoundOutline(mask: mask, scale: 0.5, padLeft: 0, padTop: 0, originalWidth: 1280, originalHeight: 960);
}

void main() {
  final png = Uint8List.fromList(base64Decode(tinyPng));

  Future<void> show(WidgetTester tester, Widget child) async {
    await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: Scaffold(body: SizedBox(width: 300, height: 225, child: child))));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
  }

  testWidgets('bytes that are given are shown as the photo, with no sample illustration', (tester) async {
    await show(tester, AssessmentPhoto(store: MemoryMediaStore(), assessmentId: 'a', bytes: png));
    expect(find.byType(Image), findsOneWidget);
    expect(find.byType(WoundIllustration), findsNothing);
  });

  testWidgets('without bytes it looks in the store', (tester) async {
    final store = MemoryMediaStore();
    await store.save('b', png, null);
    await tester.runAsync(() async {
      await show(tester, AssessmentPhoto(store: store, assessmentId: 'b'));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
    expect(find.byType(Image), findsOneWidget);
    expect(find.byType(WoundIllustration), findsNothing);
  });

  testWidgets('with nothing stored it falls back to the sample illustration, as the prototype always did', (tester) async {
    await tester.runAsync(() async {
      await show(tester, AssessmentPhoto(store: MemoryMediaStore(), assessmentId: 'zzz'));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
    expect(find.byType(WoundIllustration), findsOneWidget);
    expect(find.byType(Image), findsNothing);
  });

  testWidgets('the wound outline is drawn only when asked and only when there is one', (tester) async {
    final store = MemoryMediaStore();
    await store.save('c', png, _outline());
    await tester.runAsync(() async {
      await show(tester, AssessmentPhoto(store: store, assessmentId: 'c', showOutline: true));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
    expect(find.byKey(const Key('woundOutline')), findsOneWidget);
    await tester.runAsync(() async {
      await show(tester, AssessmentPhoto(store: store, assessmentId: 'c'));
      await Future<void>.delayed(const Duration(milliseconds: 50));
    });
    await tester.pump();
    expect(find.byKey(const Key('woundOutline')), findsNothing);
  });
}
