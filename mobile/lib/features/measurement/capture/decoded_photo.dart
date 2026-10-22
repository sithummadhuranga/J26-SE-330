import 'dart:typed_data';
import 'dart:ui' as ui;

/// A decoded photo: RGBA pixels and size as the phone's image decoder delivers them (this is where a wrongly rotated
/// photo would show up).
class DecodedPhoto {
  const DecodedPhoto(this.width, this.height, this.rgba);

  final int width, height;
  final Uint8List rgba;
}

Future<DecodedPhoto> decodePhoto(Uint8List encoded) async {
  final codec = await ui.instantiateImageCodec(encoded);
  final frame = await codec.getNextFrame();
  final image = frame.image;
  final data = await image.toByteData(format: ui.ImageByteFormat.rawRgba);
  final photo = DecodedPhoto(image.width, image.height, data!.buffer.asUint8List());
  image.dispose();
  codec.dispose();
  return photo;
}
