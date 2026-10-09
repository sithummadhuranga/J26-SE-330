import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:melanin_wound_cdss/features/sync/api/sync_api.dart';

/// The HTTP client's reachability probe (§6.2: reachability, not just being on a network).
void main() {
  Future<bool> probe(int status, String body, {String type = 'application/json'}) =>
      HttpSyncApi(Uri.parse('http://gateway'),
              client: MockClient((_) async => http.Response(body, status, headers: {'content-type': type})))
          .health();

  test('the gateway answering {"status":"ok"} is reachable', () async {
    expect(await probe(200, '{"status":"ok"}'), isTrue);
  });

  test('a Wi-Fi login page answering 200 is not the gateway', () async {
    expect(await probe(200, '<html><body>Accept the terms to use hospital Wi-Fi</body></html>', type: 'text/html'),
        isFalse);
    expect(await probe(200, '{"portal":true}'), isFalse);
  });

  test('an error answer or no answer is unreachable', () async {
    expect(await probe(503, '{"status":"ok"}'), isFalse);
    final down = HttpSyncApi(Uri.parse('http://gateway'), client: MockClient((_) async => throw http.ClientException('refused')));
    expect(await down.health(), isFalse);
  });
}
