import 'dart:isolate';
import 'dart:typed_data';

import 'capture_check.dart';
import 'decoded_photo.dart';

/// A photo that has been decoded and checked.
class CheckedPhoto {
  const CheckedPhoto(this.width, this.height, this.check);

  final int width, height;
  final CaptureCheck check;
}

/// Looks at a decoded photo off the main isolate, so a spinner keeps moving while the sticker is searched for.
Future<CaptureCheck> checkInBackground(DecodedPhoto photo) {
  final transfer = TransferableTypedData.fromList([photo.rgba]);
  final w = photo.width, h = photo.height;
  return Isolate.run(() => checkCapture(transfer.materialize().asUint8List(), w, h));
}

/// Decodes a photo as the camera or gallery gave it (JPEG, PNG) and runs the capture check on it. Throws when the bytes are not a
/// picture the phone can open.
Future<CheckedPhoto> checkEncodedPhoto(Uint8List encoded) async {
  final photo = await decodePhoto(encoded);
  return CheckedPhoto(photo.width, photo.height, await checkInBackground(photo));
}
