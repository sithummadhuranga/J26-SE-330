import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/data/app_database.dart';
import 'package:melanin_wound_cdss/features/sync/data/patient_repository.dart';

import 'support/fakes.dart';

void main() {
  late AppDatabase db;
  late FakeGateway gateway;
  late PatientRepository patients;
  late String token;

  setUp(() async {
    db = AppDatabase.inMemory();
    gateway = FakeGateway();
    patients = PatientRepository(db, gateway);
    token = (await gateway.login(username: 'n.silva', password: 'correct-password', deviceId: 'dev-1')).accessToken;
  });
  tearDown(() => db.close());

  test('a new patient gets a pseudonymous ref in the contract pattern and keeps only the label', () async {
    final p = await patients.create('  A.F. bed 12  ');
    expect(p.patientRef, matches(RegExp(r'^p-[0-9a-f]{12}$')));
    expect(p.displayAlias, 'A.F. bed 12');
    expect(p.aliasSyncedAt, isNull);
    expect((await patients.create('K.P.')).patientRef, isNot(p.patientRef));
  });

  test('empty and over-long labels are refused', () async {
    expect(() => patients.create('  '), throwsArgumentError);
    expect(() => patients.create('x' * (PatientRepository.maxAliasLength + 1)), throwsArgumentError);
  });

  test('labels go to the server once; offline stops and retries next time', () async {
    final a = await patients.create('A.F.');
    await patients.create('K.P.');

    gateway.reachable = false;
    expect(await patients.pushAliases(token), 0);
    expect(gateway.patientAliases, isEmpty);

    gateway.reachable = true;
    expect(await patients.pushAliases(token), 2);
    expect(gateway.patientAliases[a.patientRef], 'A.F.');
    expect((await patients.byRef(a.patientRef))!.aliasSyncedAt, isNotNull);

    expect(await patients.pushAliases(token), 0, reason: 'nothing left to send');
  });

  test('a record the server can never store is not retried forever', () async {
    await db
        .into(db.patientLocal)
        .insert(PatientLocalCompanion.insert(patientRef: 'not-a-ref', displayAlias: 'Bad', createdAt: DateTime.now()));
    expect(await patients.pushAliases(token), 1, reason: '400 is final: marked so it is not sent again');
    expect(gateway.patientAliases, isEmpty);
  });
}
