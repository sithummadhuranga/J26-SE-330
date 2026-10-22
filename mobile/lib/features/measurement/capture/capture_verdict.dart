import '../light_calibrator.dart';
import 'sticker_gate.dart';

enum Verdict { ok, warn, retake }

/// The outcome of the capture check: how bad it is and the reason code (null when everything is fine).
class CaptureVerdict {
  const CaptureVerdict(this.verdict, this.code);

  final Verdict verdict;
  final String? code;
}

/// The decision on plain codes: the verdict, the code that decides it, and every finding in the order it counts.
class CodeJudgement {
  const CodeJudgement(this.verdict, this.code, this.findings);

  final Verdict verdict;
  final String? code;
  final List<String> findings;
}

/// All codes in the agreed order, copied from Color Matric's `quality.TABLE_ORDER` (a test compares it with his vectors file).
const kTableOrder = [
  'no_candidate', 'cut_by_edge', 'weak_frame_contrast', 'weak_orientation', 'layout_mismatch', 'mirrored',
  'crop_invalid', 'grey_clipped', 'grey_not_uniform', 'grey_not_neutral', 'grey_gain_out_of_range', 'skin_patch_off', 'too_few_skin_pixels',
];

/// A sticker that cannot be found or read means retake (there is no scale). Everything else only warns: the scale comes from the
/// black frame, not from the colours.
const _retakeCodes = {
  'no_candidate', 'cut_by_edge', 'weak_frame_contrast', 'weak_orientation', 'layout_mismatch', 'mirrored', 'crop_invalid',
};

/// The decision table. [finderCode] is null when a sticker was accepted; [markerCode] is '' when its patches were used and null
/// when they were not evaluated; [skinPixels] is null when skin was not evaluated. The worst finding decides, ties go to the first
/// in [kTableOrder]. A code that is not in the list throws, so a typo can never pass a photo.
/// This follows the table agreed with Color Matric; his test vectors are checked against it in the tests.
CodeJudgement judgeCodes({String? finderCode, String? markerCode, int? skinPixels}) {
  final found = <String>[];
  for (final c in [finderCode, if (markerCode != null && markerCode.isNotEmpty) markerCode]) {
    if (c == null) continue;
    if (!kTableOrder.contains(c)) throw ArgumentError('unknown reason code "$c"');
    found.add(c);
  }
  if (skinPixels != null && skinPixels < kMinSkinPixels) found.add('too_few_skin_pixels');
  found.sort((a, b) {
    final ra = _retakeCodes.contains(a) ? 0 : 1, rb = _retakeCodes.contains(b) ? 0 : 1;
    return ra != rb ? ra.compareTo(rb) : kTableOrder.indexOf(a).compareTo(kTableOrder.indexOf(b));
  });
  if (found.isEmpty) return const CodeJudgement(Verdict.ok, null, []);
  return CodeJudgement(_retakeCodes.contains(found.first) ? Verdict.retake : Verdict.warn, found.first, found);
}

/// [judgeCodes] on what the capture check found.
CaptureVerdict judgeCapture({required GateScan scan, MarkerCheck? marker, SkinType? skin}) {
  final j = judgeCodes(
    finderCode: scan.code?.code,
    markerCode: marker == null ? null : (marker.code?.code ?? (marker.used ? '' : null)),
    skinPixels: skin?.skinPixels,
  );
  return CaptureVerdict(j.verdict, j.code);
}
