import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/app/app_services.dart';
import 'package:melanin_wound_cdss/core/theme.dart';
import 'package:melanin_wound_cdss/features/recommendations/chat_api.dart';
import 'package:melanin_wound_cdss/features/recommendations/chat_repository.dart';
import 'package:melanin_wound_cdss/features/recommendations/chat_screen.dart';
import 'package:melanin_wound_cdss/features/recommendations/guidance_screen.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';

import '../../sync/support/fakes.dart';

/// DRAFT follow-up chat on delivered advice (not agreed with Member 4): the cached history, the gateway calls and the
/// screen. Questions and answers are placeholders, never guideline text.
void main() {
  const assessmentId = '6f1d3c9e-8a2b-4c5d-9e0f-1a2b3c4d5e6f';

  Map<String, dynamic> turn(int n, {String outcome = 'answered'}) => {
        'turn': n,
        'question': 'Question $n [name removed]?',
        'removed': ['name'],
        'language': 'en',
        'outcome': outcome,
        'shownLanguage': 'en',
        'statements': outcome == 'answered'
            ? [
                {'text': 'Point one', 'cite': ['S1']},
                {'text': 'Point two', 'cite': ['S3']},
              ]
            : <Map<String, dynamic>>[],
        'english': <Map<String, dynamic>>[],
        'message': outcome == 'answered' ? null : 'The guideline passages available do not answer this question.',
        'notices': ['Some guidance stays withheld until these are recorded in a new assessment: pedal pulses.'],
        'citations': outcome == 'answered'
            ? {
                'S1': {'chunkId': 'PAD-R17', 'source': 'PAD', 'pdfIndex': 12, 'recommendation': '3', 'grade': 'Strong; Moderate',
                  'chunkHash': 'a' * 64},
                'S3': {'chunkId': 'OFF-P04', 'source': 'Offloading', 'pdfIndex': 30, 'recommendation': null, 'grade': null,
                  'chunkHash': 'b' * 64},
              }
            : <String, dynamic>{},
        'answeredBy': 'deepseek-v4.1-flash',
        'createdAt': '2026-10-19T09:00:00.0000000+00:00',
      };

  Map<String, dynamic> session(List<Map<String, dynamic>> turns) => {
        'sessionId': '11111111-2222-4333-8444-555555555555',
        'caseId': assessmentId,
        'revision': 1,
        'corpusVersion': 'iwgdf-2023.r2',
        'turns': turns,
      };

  group('ChatRepository', () {
    late AppDatabase db;
    late FakeGateway gateway;
    late AuthSession auth;
    late FakeChatApi api;
    late ChatRepository chats;

    setUp(() async {
      db = AppDatabase.inMemory();
      gateway = FakeGateway();
      auth = AuthSession(gateway, MemorySecretStore(), browser: FakeBrowserSignIn(gateway));
      await auth.signIn(deviceId: 'dev-test');
      api = FakeChatApi(gateway);
      chats = ChatRepository(db, api, auth);
    });
    tearDown(() => db.close());

    test('opening stores the session, so it is readable offline later', () async {
      api.sessionBody = session([turn(1)]);
      final opened = await chats.open(assessmentId, 1);
      expect(opened.problem, isNull);
      expect(opened.turns, hasLength(1));

      gateway.reachable = false;
      final offline = await chats.open(assessmentId, 1);
      expect(offline.problem, ChatProblem.offline);
      expect(offline.turns.single['question'], 'Question 1 [name removed]?', reason: 'the saved history, as the service stored it');
    });

    test('a question adds its turn to the saved session', () async {
      api.sessionBody = session([turn(1)]);
      await chats.open(assessmentId, 1);
      api.turnBody = turn(2, outcome: 'not_covered');

      final asked = await chats.ask(assessmentId, 1, 'And this?', language: 'en');
      expect(asked.problem, isNull);
      expect(asked.turns.map((t) => t['turn']), [1, 2]);
      expect(api.asked.single, (question: 'And this?', language: 'en'));
      expect((await chats.cached(assessmentId, 1))!['turns'], hasLength(2));
    });

    test('a lapsed token is refreshed once; the service\'s refusals are named', () async {
      api.sessionBody = session([]);
      api.failNextWith = ApiException(401);
      expect((await chats.open(assessmentId, 1)).problem, isNull);
      expect(gateway.refreshes, 1);

      for (final (status, problem) in [
        (404, ChatProblem.notAvailable),
        (422, ChatProblem.rejected),
        (409, ChatProblem.conflict),
        (502, ChatProblem.unavailable),
      ]) {
        api.failNextWith = ApiException(status);
        expect((await chats.ask(assessmentId, 1, 'q')).problem, problem, reason: 'HTTP $status');
      }
    });

    test('without a session nothing is sent and the saved history is still shown', () async {
      api.sessionBody = session([turn(1)]);
      await chats.open(assessmentId, 1);
      await auth.signOut();
      final state = await chats.ask(assessmentId, 1, 'q');
      expect(state.problem, ChatProblem.needsSignIn);
      expect(state.turns, hasLength(1));
      expect(api.asked, isEmpty);
    });
  });

  test('a version 2 database upgrades to 3 with chat_local and keeps its rows', () async {
    final dir = await Directory.systemTemp.createTemp('chat-upgrade');
    addTearDown(() => dir.delete(recursive: true));
    final file = File('${dir.path}/app.db');

    final v3 = AppDatabase(NativeDatabase(file));
    await v3.into(v3.syncState).insert(SyncStateCompanion.insert(key: 'k', value: 'v'));
    await v3.customStatement('DROP TABLE chat_local');
    await v3.customStatement('PRAGMA user_version = 2');
    await v3.close();

    final upgraded = AppDatabase(NativeDatabase(file));
    await upgraded.into(upgraded.chatLocal).insert(ChatLocalCompanion.insert(
        assessmentId: assessmentId, revision: 1, sessionJson: '{}', updatedAt: DateTime.utc(2026, 10, 19)));
    expect(await upgraded.select(upgraded.chatLocal).get(), hasLength(1));
    expect((await upgraded.select(upgraded.syncState).getSingle()).value, 'v');
    await upgraded.close();
  });

  group('screens', () {
    late AppServices services;
    late FakeGateway gateway;
    late FakeChatApi api;

    Future<void> settle(WidgetTester tester, [int rounds = 25]) async {
      for (var i = 0; i < rounds; i++) {
        await tester.pump(const Duration(milliseconds: 20));
      }
    }

    // Services and the fake chat routes only; each test shows its screen once its data is set.
    Future<void> start(WidgetTester tester) async {
      tester.view
        ..physicalSize = const Size(1080, 2340)
        ..devicePixelRatio = 2.6;
      addTearDown(tester.view.reset);
      gateway = FakeGateway();
      services = await AppServices.open(
          db: AppDatabase.inMemory(),
          secrets: MemorySecretStore(),
          api: gateway,
          browser: FakeBrowserSignIn(gateway),
          online: const Stream.empty());
      await services.signIn();
      api = FakeChatApi(gateway);
    }

    Future<void> close(WidgetTester tester) async {
      await tester.pumpWidget(const SizedBox());
      await settle(tester);
      services.scheduler.dispose();
      final closing = services.db.close();
      await settle(tester);
      await closing;
    }

    ChatScreen chatScreen() => ChatScreen(
        chats: ChatRepository(services.db, api, services.auth), assessmentId: assessmentId, revision: 1);

    testWidgets('a question is asked and its checked answer shown with its sources and notices', (tester) async {
      await start(tester);
      api.sessionBody = session([]);
      await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: chatScreen()));
      await settle(tester);
      expect(find.byKey(const Key('chatPrivacyNote')), findsOneWidget, reason: 'warns before anything is typed');

      api.turnBody = turn(1);
      await tester.enterText(find.byKey(const Key('chatQuestion')), 'What applies?');
      await tester.tap(find.byKey(const Key('chatSend')));
      await settle(tester);

      expect(find.text('Question 1 [name removed]?'), findsOneWidget, reason: 'the question as stored, not as typed');
      expect(find.text('Point one'), findsOneWidget);
      expect(find.text('Point two'), findsOneWidget);
      expect(find.text('S1 · PAD · Recommendation 3 · Strong; Moderate · PDF page 12'), findsOneWidget);
      expect(find.text('S3 · Offloading · PDF page 30'), findsOneWidget);
      expect(find.textContaining('pedal pulses'), findsOneWidget);
      expect(tester.widget<TextField>(find.byKey(const Key('chatQuestion'))).controller!.text, isEmpty);
      await close(tester);
    });

    testWidgets('a turn that was not answered shows the service\'s fixed message', (tester) async {
      await start(tester);
      api.sessionBody = session([turn(1, outcome: 'not_covered')]);
      await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: chatScreen()));
      await settle(tester);
      expect(find.text('The guideline passages available do not answer this question.'), findsOneWidget);
      await close(tester);
    });

    testWidgets('offline, the saved conversation shows and asking is turned off', (tester) async {
      await start(tester);
      api.sessionBody = session([turn(1)]);
      await ChatRepository(services.db, api, services.auth).open(assessmentId, 1);
      gateway.reachable = false;
      await tester.pumpWidget(MaterialApp(theme: woundTheme(), home: chatScreen()));
      await settle(tester);

      expect(find.text('Point one'), findsOneWidget);
      expect(find.byKey(const Key('chatOffline')), findsOneWidget);
      expect(tester.widget<IconButton>(find.byKey(const Key('chatSend'))).onPressed, isNull);
      await close(tester);
    });

    testWidgets('the guidance screen offers the chat only when the advice names its assessment', (tester) async {
      final advice = {
        'caseId': assessmentId,
        'revision': 1,
        'mode': 'extractive',
        'sections': [
          {'heading': 'Management', 'text': 'Fake advice [S1].', 'citationTags': ['S1']}
        ],
      };
      await start(tester);
      api.sessionBody = session([]);
      await tester.pumpWidget(MaterialApp(
          theme: woundTheme(),
          home: GuidanceScreen(services: services, payload: advice, receivedAt: DateTime(2026, 10, 19), chatApi: api)));
      await settle(tester);
      await tester.scrollUntilVisible(find.byKey(const Key('askAboutGuidance')), 200, scrollable: find.byType(Scrollable).first);
      await tester.tap(find.byKey(const Key('askAboutGuidance')));
      await settle(tester);
      expect(find.byType(ChatScreen), findsOneWidget);

      await tester.pumpWidget(MaterialApp(
          theme: woundTheme(),
          home: GuidanceScreen(services: services, payload: {...advice}..remove('caseId'), receivedAt: DateTime(2026, 10, 19), chatApi: api)));
      await settle(tester);
      expect(find.byKey(const Key('askAboutGuidance')), findsNothing);
      await close(tester);
    });
  });
}

/// The gateway's chat routes in memory: scripted bodies, an optional failure for the next call, and the token check
/// of the fake gateway it shares a session with.
class FakeChatApi implements ChatApi {
  FakeChatApi(this.gateway);

  final FakeGateway gateway;
  Map<String, dynamic> sessionBody = const {};
  Map<String, dynamic> turnBody = const {};
  ApiException? failNextWith;
  final List<({String question, String language})> asked = [];

  void _check(String accessToken) {
    if (!gateway.reachable) throw TransportException('unreachable');
    if (failNextWith case final e?) {
      failNextWith = null;
      throw e;
    }
  }

  @override
  Future<Map<String, dynamic>> open(String accessToken, String assessmentId, int revision) async {
    _check(accessToken);
    return sessionBody;
  }

  @override
  Future<Map<String, dynamic>> ask(String accessToken, String assessmentId, int revision, String question, String language) async {
    _check(accessToken);
    asked.add((question: question, language: language));
    return turnBody;
  }
}
