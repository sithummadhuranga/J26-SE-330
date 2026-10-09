import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/sync/domain/wound_event_validator.dart';

import 'support/fakes.dart';

/// The app's validator against the contract's own examples (contracts/examples), so it cannot drift from the schema.
void main() {
  final examples = Directory('../contracts/examples')
      .listSync()
      .whereType<File>()
      .where((f) => f.path.contains('wound-event.'))
      .toList();

  test('the contract examples are found', () => expect(examples, isNotEmpty));

  for (final file in examples) {
    final name = file.uri.pathSegments.last;
    final valid = name.endsWith('.valid.json');
    test('contract example $name is ${valid ? 'accepted' : 'rejected'}', () {
      final errors = WoundEventValidator.validate(jsonDecode(file.readAsStringSync()) as Map<String, dynamic>);
      expect(errors.isEmpty, valid, reason: errors.join('; '));
    });
  }

  test('a generated sample is valid', () => expect(WoundEventValidator.validate(sampleEvent()), isEmpty));

  test('tri-state is strict: a missing key is never read as not_recorded (§5)', () {
    final e = sampleEvent();
    (e['clinicalAssessment'] as Map).remove('pedalPulses');
    expect(WoundEventValidator.validate(e), contains(contains('pedalPulses is required')));
    final f = sampleEvent();
    (f['clinicalAssessment'] as Map)['pedalPulses'] = 'no';
    expect(WoundEventValidator.validate(f).join(), contains('present, absent or not_recorded'));
  });

  test('a name in place of the pseudonym is refused', () {
    expect(WoundEventValidator.validate(sampleEvent()..['patientRef'] = 'Kamal Perera').join(), contains('patientRef'));
  });

  test('fields outside the contract are refused (data minimization, §12)', () {
    expect(WoundEventValidator.validate(sampleEvent()..['patientName'] = 'Kamal').join(), contains('not allowed'));
  });

  test('ids must be UUIDv7', () {
    expect(WoundEventValidator.validate(sampleEvent()..['eventId'] = '00000000-0000-4000-8000-000000000000').join(),
        contains('eventId'));
  });

  test('over 16 KB is refused (§5: analytics only, no images)', () {
    final e = sampleEvent();
    ((e['analytics'] as Map)['pipeline'] as Map)['segmentation'] = 'x' * 17000;
    expect(WoundEventValidator.validate(e).join(), contains('limit is 16384'));
  });
}
