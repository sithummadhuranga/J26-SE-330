import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// Where the wound was found, as the model saw it (a 640 x 480 mask) with what is needed to put it back on the photo.
class WoundOutline {
  WoundOutline({
    required this.mask,
    required this.scale,
    required this.padLeft,
    required this.padTop,
    required this.originalWidth,
    required this.originalHeight,
    this.maskWidth = 640,
    this.maskHeight = 480,
  }) : assert(mask.length == maskWidth * maskHeight);

  final Uint8List mask;
  final double scale;
  final int padLeft, padTop, originalWidth, originalHeight, maskWidth, maskHeight;

  /// The pixels on the edge of the wound (a wound pixel with an empty neighbour, or at the edge of the mask), as fractions of the
  /// original photo's width and height, ready to draw on top of it.
  List<(double, double)> boundaryFractions() {
    final out = <(double, double)>[];
    for (var y = 0; y < maskHeight; y++) {
      for (var x = 0; x < maskWidth; x++) {
        if (mask[y * maskWidth + x] == 0) continue;
        final edge = x == 0 || y == 0 || x == maskWidth - 1 || y == maskHeight - 1 ||
            mask[y * maskWidth + x - 1] == 0 || mask[y * maskWidth + x + 1] == 0 ||
            mask[(y - 1) * maskWidth + x] == 0 || mask[(y + 1) * maskWidth + x] == 0;
        if (!edge) continue;
        final px = (x + 0.5 - padLeft) / scale, py = (y + 0.5 - padTop) / scale;
        out.add(((px / originalWidth).clamp(0.0, 1.0), (py / originalHeight).clamp(0.0, 1.0)));
      }
    }
    return out;
  }

  Map<String, dynamic> toJson() {
    final packed = Uint8List((mask.length + 7) ~/ 8);
    for (var i = 0; i < mask.length; i++) {
      if (mask[i] != 0) packed[i >> 3] |= 1 << (i & 7);
    }
    return {
      'scale': scale, 'padLeft': padLeft, 'padTop': padTop, 'originalWidth': originalWidth, 'originalHeight': originalHeight,
      'maskWidth': maskWidth, 'maskHeight': maskHeight, 'mask': base64Encode(packed),
    };
  }

  factory WoundOutline.fromJson(Map<String, dynamic> j) {
    final w = j['maskWidth'] as int, h = j['maskHeight'] as int;
    final packed = base64Decode(j['mask'] as String);
    final mask = Uint8List(w * h);
    for (var i = 0; i < mask.length; i++) {
      mask[i] = (packed[i >> 3] >> (i & 7)) & 1;
    }
    return WoundOutline(
      mask: mask,
      scale: (j['scale'] as num).toDouble(),
      padLeft: j['padLeft'] as int,
      padTop: j['padTop'] as int,
      originalWidth: j['originalWidth'] as int,
      originalHeight: j['originalHeight'] as int,
      maskWidth: w,
      maskHeight: h,
    );
  }
}

/// An analyzer that can hand over where it found the wound, once, after the analysis it just did (null when it found none).
abstract interface class WoundOutlineSource {
  WoundOutline? takeOutline();
}

/// A photo kept on the phone with the outline of its wound (null when none was found or kept).
class AssessmentMedia {
  const AssessmentMedia(this.photo, this.outline);

  final Uint8List photo;
  final WoundOutline? outline;
}

/// Keeps each assessment's photo on the phone, next to the numbers that were synced. The photo itself is never uploaded.
abstract class MediaStore {
  Future<void> save(String assessmentId, Uint8List photo, WoundOutline? outline);

  Future<AssessmentMedia?> load(String assessmentId);
}

/// Photos in a folder of the app's own storage, one file per assessment.
class FileMediaStore implements MediaStore {
  FileMediaStore(this.dir);

  final Directory dir;

  String _safe(String id) {
    if (!RegExp(r'^[A-Za-z0-9_-]+$').hasMatch(id)) throw ArgumentError('not a usable assessment id: $id');
    return id;
  }

  @override
  Future<void> save(String assessmentId, Uint8List photo, WoundOutline? outline) async {
    final id = _safe(assessmentId);
    await dir.create(recursive: true);
    await File('${dir.path}/$id.jpg').writeAsBytes(photo, flush: true);
    final o = File('${dir.path}/$id.outline.json');
    if (outline == null) {
      if (o.existsSync()) await o.delete();
    } else {
      await o.writeAsString(jsonEncode(outline.toJson()), flush: true);
    }
  }

  @override
  Future<AssessmentMedia?> load(String assessmentId) async {
    final id = _safe(assessmentId);
    final f = File('${dir.path}/$id.jpg');
    if (!f.existsSync()) return null;
    final o = File('${dir.path}/$id.outline.json');
    WoundOutline? outline;
    if (o.existsSync()) {
      try {
        outline = WoundOutline.fromJson(jsonDecode(await o.readAsString()) as Map<String, dynamic>);
      } catch (_) {
        outline = null; // a damaged outline must not hide the photo
      }
    }
    return AssessmentMedia(await f.readAsBytes(), outline);
  }
}

/// For tests and the prototype: nothing is written to the phone.
class MemoryMediaStore implements MediaStore {
  final _items = <String, AssessmentMedia>{};

  @override
  Future<void> save(String assessmentId, Uint8List photo, WoundOutline? outline) async => _items[assessmentId] = AssessmentMedia(photo, outline);

  @override
  Future<AssessmentMedia?> load(String assessmentId) async => _items[assessmentId];
}
