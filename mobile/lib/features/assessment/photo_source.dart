import 'dart:typed_data';

import 'package:image_picker/image_picker.dart';

import 'analysis/wound_analyzer.dart';

enum PhotoOrigin { camera, device }

/// The camera or gallery could not give a photo. [message] is for the nurse.
class PhotoUnavailable implements Exception {
  const PhotoUnavailable(this.message);

  final String message;

  @override
  String toString() => 'PhotoUnavailable: $message';
}

/// Where the capture step gets its photo from. The app shows the capture screen the same way for both; only the source differs.
abstract class PhotoSource {
  /// True for the prototype source that has no photo (the rest of the assessment then runs on sample data).
  bool get isSample;

  /// Takes or picks a photo. Null when the nurse cancelled.
  Future<WoundCapture?> pick(PhotoOrigin origin);
}

/// The prototype: no photo, so the flow continues with a sample capture. This is what the tests and an app without a camera use.
class SamplePhotoSource implements PhotoSource {
  const SamplePhotoSource();

  @override
  bool get isSample => true;

  @override
  Future<WoundCapture?> pick(PhotoOrigin origin) async => const WoundCapture();
}

/// The phone's camera and gallery, through the system picker. The photo stays on the phone: only its bytes in memory go on to the
/// check and the analysis.
class DevicePhotoSource implements PhotoSource {
  DevicePhotoSource({Future<Uint8List?> Function(ImageSource source)? picker}) : _picker = picker ?? _systemPicker;

  final Future<Uint8List?> Function(ImageSource source) _picker;

  static Future<Uint8List?> _systemPicker(ImageSource source) async => (await ImagePicker().pickImage(source: source))?.readAsBytes();

  @override
  bool get isSample => false;

  @override
  Future<WoundCapture?> pick(PhotoOrigin origin) async {
    try {
      final bytes = await _picker(origin == PhotoOrigin.camera ? ImageSource.camera : ImageSource.gallery);
      return bytes == null ? null : WoundCapture(imageBytes: bytes);
    } catch (_) {
      throw const PhotoUnavailable('The camera is not available. Use Choose from device.');
    }
  }
}
