import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';
import 'package:melanin_wound_cdss/features/assessment/photo_source.dart';

void main() {
  test('the sample source hands over a capture with no photo, so the prototype flow still works', () async {
    const source = SamplePhotoSource();
    expect(source.isSample, isTrue);
    final c = await source.pick(PhotoOrigin.camera);
    expect(c, isNotNull);
    expect(c!.imageBytes, isNull);
  });

  test('the device source asks the camera or the gallery and returns the photo bytes', () async {
    final asked = <ImageSource>[];
    final source = DevicePhotoSource(picker: (s) async {
      asked.add(s);
      return Uint8List.fromList([1, 2, 3]);
    });
    expect(source.isSample, isFalse);
    expect((await source.pick(PhotoOrigin.camera))!.imageBytes, [1, 2, 3]);
    expect((await source.pick(PhotoOrigin.device))!.imageBytes, [1, 2, 3]);
    expect(asked, [ImageSource.camera, ImageSource.gallery]);
  });

  test('cancelling the camera gives null, not an empty capture', () async {
    final source = DevicePhotoSource(picker: (_) async => null);
    expect(await source.pick(PhotoOrigin.camera), isNull);
  });

  test('a camera that is not available is reported with words a nurse can act on', () async {
    final source = DevicePhotoSource(picker: (_) async => throw StateError('no camera'));
    expect(() => source.pick(PhotoOrigin.camera), throwsA(isA<PhotoUnavailable>().having((e) => e.message, 'message', contains('Choose from device'))));
  });
}
