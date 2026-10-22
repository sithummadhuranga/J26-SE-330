import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/assessment/processing_screen.dart';
import 'package:melanin_wound_cdss/features/sync/data/tables.dart';
import 'package:melanin_wound_cdss/features/sync/engine/sync_engine.dart';

void main() {
  SyncReport report(SyncOutcome o, {String? detail}) => SyncReport(o, detail: detail);

  group('what the guidance step says', () {
    test('advice that arrived is done, with nothing to explain', () {
      expect(describeAdviceWait(QueueStatus.complete, report(SyncOutcome.success)), (StepState.done, null));
      expect(describeAdviceWait(QueueStatus.superseded, report(SyncOutcome.success)), (StepState.done, null));
    });

    test('a rejected assessment says so and keeps the server\'s detail', () {
      final (state, note) = describeAdviceWait(QueueStatus.rejected, report(SyncOutcome.success, detail: 'schema: areaMm2 must be >= 0'))!;
      expect(state, StepState.attention);
      expect(note, contains('did not accept'));
      expect(note, contains('areaMm2 must be >= 0'));
    });

    test('advice the server deferred is named as the guideline service not answering, not as a slow sync', () {
      final (state, note) = describeAdviceWait(QueueStatus.adviceDeferred, report(SyncOutcome.success))!;
      expect(state, StepState.attention);
      expect(note, contains('guideline service could not answer yet'));
      expect(note, contains('tries again by itself'));
      expect(note, isNot(contains('taking longer')));
    });

    test('offline and busy say so and show the reason the sync gave', () {
      for (final o in [SyncOutcome.offline, SyncOutcome.serverBusy, SyncOutcome.backingOff]) {
        final (state, note) = describeAdviceWait(QueueStatus.accepted, report(o, detail: 'connection reset'))!;
        expect(state, StepState.attention, reason: '$o');
        expect(note, contains('saved'), reason: '$o');
        expect(note, contains('connection reset'), reason: '$o');
      }
      expect(describeAdviceWait(QueueStatus.accepted, report(SyncOutcome.offline))!.$2, isNot(contains('(')));
    });

    test('a signed-out phone is told to sign in again', () {
      expect(describeAdviceWait(QueueStatus.accepted, report(SyncOutcome.needsSignIn))!.$2, contains('Sign in again'));
    });

    test('while it is simply still working there is nothing to say yet', () {
      expect(describeAdviceWait(QueueStatus.accepted, report(SyncOutcome.success)), isNull);
    });

    test('running out of time names the last sync result and its detail', () {
      final (state, note) = describeAdviceTimeout(report(SyncOutcome.success, detail: 'no change for this assessment'));
      expect(state, StepState.attention);
      expect(note, contains('still waiting'));
      expect(note, contains('success'));
      expect(note, contains('no change for this assessment'));
    });
  });

  group('what an unexpected error says', () {
    test('shows the kind of error and its message instead of hiding it', () {
      final (state, note) = describeAdviceError(StateError('database is closed'));
      expect(state, StepState.attention);
      expect(note, contains('saved'));
      expect(note, contains('StateError'));
      expect(note, contains('database is closed'));
    });

    test('a very long message is cut short', () {
      final (_, note) = describeAdviceError(Exception('x' * 600));
      expect(note!.length, lessThan(260));
    });
  });
}
