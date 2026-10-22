import 'dart:typed_data';

import '../measurement/colour_regions.dart';
import 'media_store.dart';

/// What can be said about a photo that has no sticker in it: where the wound is, its colour regions and a skin type. There is no
/// area, because the sticker is the only scale there is, and no colour correction, because its grey patch is the only reference.
/// This is shown on the phone but is not saved or synced: the event contract needs an area.
class LocalAnalysis {
  const LocalAnalysis({required this.outline, required this.colourRegions, required this.fitzpatrickClass, required this.notes});

  final WoundOutline outline;
  final List<ColourRegion> colourRegions;

  /// 'I' to 'VI', or null when there were too few skin pixels.
  final String? fitzpatrickClass;
  final List<String> notes;
}

/// An analyzer that can also work on a photo without a sticker.
abstract interface class LocalAnalyzer {
  Future<LocalAnalysis> analyzeWithoutScale(Uint8List photo);
}
