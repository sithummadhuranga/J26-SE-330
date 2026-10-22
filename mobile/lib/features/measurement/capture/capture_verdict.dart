import '../light_calibrator.dart';
import 'sticker_gate.dart';

enum Verdict { ok, warn, retake }

/// The outcome of the capture check: how bad it is and the reason code (null when everything is fine).
class CaptureVerdict {
  const CaptureVerdict(this.verdict, this.code);

  final Verdict verdict;
  final String? code;
}

/// The decision table. The verdict is about using the photo for area and colour; the worst result wins, and between equal ones the
/// first in this order: the finder, the sticker's colour patches, the skin. A sticker that cannot be found or read means retake,
/// because there is no scale; colour problems only warn, because the scale comes from the black frame, not from the colours.
/// This is CV's draft of the table agreed with Color Matric; his test vectors replace it.
CaptureVerdict judgeCapture({required GateScan scan, MarkerCheck? marker, SkinType? skin}) {
  final finder = scan.code;
  if (finder != null) return CaptureVerdict(Verdict.retake, finder.code);
  final m = marker?.code;
  if (m != null) return CaptureVerdict(m == MarkerCode.cropInvalid ? Verdict.retake : Verdict.warn, m.code);
  final s = skin?.code;
  if (s != null) return CaptureVerdict(Verdict.warn, s.code);
  return const CaptureVerdict(Verdict.ok, null);
}
