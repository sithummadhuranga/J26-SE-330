import 'dart:math';

/// How long to wait before retrying. It starts at 2 seconds and doubles up to 5 minutes, with random jitter,
/// and resets after a success. If the server's Retry-After is longer, we use that instead.
class Backoff {
  Backoff([Random? random]) : _random = random ?? Random();

  static const initial = Duration(seconds: 2);
  static const max = Duration(minutes: 5);

  final Random _random;
  int _failures = 0;

  int get failures => _failures;

  void reset() => _failures = 0;

  /// Notes a failure and returns how long to wait before trying again.
  Duration next({Duration? retryAfter}) {
    final ceiling = Duration(microseconds: min(max.inMicroseconds, initial.inMicroseconds * (1 << min(_failures, 20))));
    _failures++;
    final jittered = Duration(microseconds: (_random.nextDouble() * ceiling.inMicroseconds).round());
    return retryAfter != null && retryAfter > jittered ? retryAfter : jittered;
  }
}
