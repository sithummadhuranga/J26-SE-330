import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/auth/auth_session.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/figure_repository.dart';
import 'package:melanin_wound_cdss/features/sync/data/queue_repository.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';

import 'support/fakes.dart';

/// Guideline figures on the device (architecture §6 figures_local, §10.4).
void main() {
  late AppDatabase db;
  late QueueRepository queue;
  late FakeGateway gateway;
  late AuthSession auth;
  late FigureRepository figures;
  final now = DateTime.utc(2026, 10, 1, 8);
  const png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];

  void build({FakeGateway? fake}) {
    gateway = fake ?? FakeGateway();
    gateway.figures['iwgdf-2023.r1/F1'] = png;
    gateway.figures['iwgdf-2023.r1/F2'] = png;
    auth = AuthSession(gateway, MemorySecretStore(), clock: () => now);
    figures = FigureRepository(db, gateway, auth, clock: () => now);
  }

  Future<void> signedIn() async =>
      auth.signIn(username: 'n.silva', password: 'correct-password', deviceId: await queue.deviceId());

  setUp(() {
    db = AppDatabase.inMemory();
    queue = QueueRepository(db, clock: () => now);
    build();
  });
  tearDown(() => db.close());

  test('a figure is fetched once, stored with its license, then served from the cache without the network', () async {
    await signedIn();
    final first = await figures.figureFor('iwgdf-2023.r1', 'F1');
    expect(first.isFound, isTrue);
    expect(first.figure!.bytes, png);
    expect(first.figure!.contentType, 'image/png');
    expect(first.figure!.licence, 'CC BY-NC 4.0 (test)');
    expect(first.figure!.attribution, 'Test corpus');
    expect(gateway.figureRequests, 1);

    gateway.reachable = false;
    final again = await figures.figureFor('iwgdf-2023.r1', 'F1');
    expect(again.isFound, isTrue);
    expect(gateway.figureRequests, 1, reason: 'a frozen corpus version never changes: no revalidation');
  });

  test('the cache survives an app restart (a new repository on the same database)', () async {
    await signedIn();
    await figures.figureFor('iwgdf-2023.r1', 'F1');
    final afterRestart = FigureRepository(db, gateway, AuthSession(gateway, MemorySecretStore(), clock: () => now));
    gateway.reachable = false;
    expect((await afterRestart.figureFor('iwgdf-2023.r1', 'F1')).isFound, isTrue);
  });

  test('misses say why: offline, not found, signed out, service failing', () async {
    expect((await figures.figureFor('iwgdf-2023.r1', 'F1')).miss, FigureMiss.needsSignIn);

    await signedIn();
    expect((await figures.figureFor('iwgdf-2023.r1', 'F9')).miss, FigureMiss.notFound);
    expect(await figures.cached('iwgdf-2023.r1', 'F9'), isNull);
    final asked = gateway.figureRequests;
    expect((await figures.figureFor('iwgdf-2023.r1', 'F9')).miss, FigureMiss.notFound);
    expect(gateway.figureRequests, asked, reason: 'a 404 is not asked again in this app session');

    gateway.figureServiceDown = true;
    expect((await figures.figureFor('iwgdf-2023.r1', 'F2')).miss, FigureMiss.unavailable);
    gateway.figureServiceDown = false;

    gateway.reachable = false;
    expect((await figures.figureFor('iwgdf-2023.r1', 'F2')).miss, FigureMiss.offline);
    gateway.reachable = true;
    expect((await figures.figureFor('iwgdf-2023.r1', 'F2')).isFound, isTrue, reason: 'nothing bad was cached');
  });

  test('an access token that lapsed is refreshed once and the figure fetched (§7.1: 401)', () async {
    await signedIn();
    gateway.expireAccessToken();
    final result = await figures.figureFor('iwgdf-2023.r1', 'F1');
    expect(result.isFound, isTrue);
    expect(gateway.refreshes, 1);
  });

  test('figure references are read from the recommendation payload; malformed ones are skipped', () {
    expect(
        FigureRepository.figureReferences('{"figures": [{"corpusVersion": "c1", "figureId": "F1", "tier": "A"}, '
            '{"figureId": "no-corpus"}, "junk", {"corpusVersion": "c1", "figureId": "F2"}]}'),
        [(corpusVersion: 'c1', figureId: 'F1'), (corpusVersion: 'c1', figureId: 'F2')]);
    expect(FigureRepository.figureReferences('{"sections": []}'), isEmpty);
    expect(FigureRepository.figureReferences('not json'), isEmpty);
  });

  test('a sync caches the figures the received advice cites, so they show offline later', () async {
    build(fake: FakeGateway(recommendationFigures: [
      {'corpusVersion': 'iwgdf-2023.r1', 'figureId': 'F1'},
      {'corpusVersion': 'iwgdf-2023.r1', 'figureId': 'F2'},
      {'corpusVersion': 'iwgdf-2023.r1', 'figureId': 'F9'}, // the service has no F9
    ]));
    final engine = SyncEngine(queue: queue, api: gateway, auth: auth, figures: figures, clock: () => now);
    await signedIn();
    await queue.enqueue(sampleEvent());
    await queue.enqueue(sampleEvent());

    final report = await engine.sync();
    expect(report.outcome, SyncOutcome.success);
    expect(report.figures, 2, reason: 'F1 and F2 once each, although two recommendations cite them; F9 is 404');
    expect(await figures.missingReferences(), isEmpty);

    gateway.reachable = false;
    expect((await figures.figureFor('iwgdf-2023.r1', 'F2')).isFound, isTrue);
  });

  test('a figure fetch failing never fails the sync; the next sync fetches it', () async {
    build(fake: FakeGateway(recommendationFigures: [
      {'corpusVersion': 'iwgdf-2023.r1', 'figureId': 'F1'},
    ]));
    final engine = SyncEngine(queue: queue, api: gateway, auth: auth, figures: figures, clock: () => now);
    await signedIn();
    await queue.enqueue(sampleEvent());

    gateway.figureServiceDown = true;
    final first = await engine.sync();
    expect(first.outcome, SyncOutcome.success);
    expect(first.figures, 0);
    expect(await figures.missingReferences(), hasLength(1));

    gateway.figureServiceDown = false;
    expect((await engine.sync()).figures, 1);
    expect(await figures.missingReferences(), isEmpty);
  });
}
