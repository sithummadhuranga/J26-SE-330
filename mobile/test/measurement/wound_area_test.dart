import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:melanin_wound_cdss/features/measurement/wound_area.dart';
import 'package:melanin_wound_cdss/features/measurement/yolo_seg_postprocess.dart';

/// Checks the area calculation with made-up wounds whose true area is known. The tilted-plane test places a disc of
/// exactly 400 mm² next to a 50 mm square marker, as a pinhole camera 35 cm away with a 75 degree field of view would
/// see them, so the right answer is known to the pixel.
void main() {
  test('a uniform scale turns a pixel count into mm²', () {
    final mask = Uint8List(kInputW * kInputH);
    for (var y = 100; y < 200; y++) {
      for (var x = 100; x < 200; x++) {
        mask[y * kInputW + x] = 1;
      }
    }
    expect(const UniformScale(0.5).areaMm2(mask), closeTo(2500.0, 1e-9));
    expect(const UniformScale(0.5).areaMm2(Uint8List(kInputW * kInputH)), 0.0);
  });

  test('a square marker of 50 mm that is 100 pixels wide gives 0.5 mm per pixel', () {
    final corners = [(100.0, 100.0), (200.0, 100.0), (200.0, 200.0), (100.0, 200.0)];
    expect(UniformScale.fromSquare(corners, 50).mmPerPixel, closeTo(0.5, 1e-12));
    final h = HomographyScale.fromSquare(corners, 50);
    final (u, v) = h.toPlane(150, 150);
    expect([u, v], [closeTo(25, 1e-9), closeTo(25, 1e-9)]);
    final mask = Uint8List(kInputW * kInputH)..fillRange(0, 4000, 1);
    expect(h.areaMm2(mask), closeTo(UniformScale(0.5).areaMm2(mask), 1e-6));
  });

  group('a tilted plane', () {
    const f = 640 / 2 / 0.7673269879789604; // focal length in pixels for a 75 degree horizontal field of view
    const z = 350.0; // mm from the camera to the plane's centre
    const cx = 320.0, cy = 240.0;

    (double, double) project(double u, double v, double tilt) {
      final t = tilt * math.pi / 180;
      final depth = z + u * math.sin(t);
      return (f * u * math.cos(t) / depth + cx, f * v / depth + cy);
    }

    // A pixel position back to millimetres on the plane (inverse of project).
    (double, double) unproject(double x, double y, double tilt) {
      final t = tilt * math.pi / 180;
      final u = z * (x - cx) / (f * math.cos(t) - (x - cx) * math.sin(t));
      final depth = z + u * math.sin(t);
      return (u, (y - cy) * depth / f);
    }

    // Returns the wound's pixel mask and the marker's corner pixels for a given tilt.
    (Uint8List, List<(double, double)>) scene(double tilt) {
      const r = 11.283791670955125; // radius of a 400 mm² disc
      final mask = Uint8List(kInputW * kInputH);
      for (var y = 0; y < kInputH; y++) {
        for (var x = 0; x < kInputW; x++) {
          final (u, v) = unproject(x + 0.5, y + 0.5, tilt);
          if (math.pow(u - 25, 2) + v * v <= r * r) mask[y * kInputW + x] = 1;
        }
      }
      final corners = [for (final (u, v) in [(-50.0, -25.0), (0.0, -25.0), (0.0, 25.0), (-50.0, 25.0)]) project(u, v, tilt)];
      return (mask, corners);
    }

    test('seen square-on, both kinds of scale are close to 400 mm²', () {
      final (mask, corners) = scene(0);
      expect(UniformScale.fromSquare(corners, 50).areaMm2(mask), closeTo(400, 400 * 0.02));
      expect(HomographyScale.fromSquare(corners, 50).areaMm2(mask), closeTo(400, 400 * 0.02));
    });

    test('tilted 20 degrees, the homography still finds the area but a uniform scale does not', () {
      final (mask, corners) = scene(20);
      final uniform = UniformScale.fromSquare(corners, 50).areaMm2(mask);
      final homography = HomographyScale.fromSquare(corners, 50).areaMm2(mask);
      expect(homography, closeTo(400, 400 * 0.02));
      expect((uniform - 400).abs() / 400, greaterThan(0.05));
    });
  });

  test('bad marker input is refused', () {
    expect(() => UniformScale.fromSquare([(0, 0), (1, 0), (1, 1)], 50), throwsArgumentError);
    expect(() => HomographyScale.fromSquare([(0, 0), (1, 0), (1, 1), (0, 1)], 0), throwsArgumentError);
    expect(() => HomographyScale.fromSquare([(0, 0), (1, 1), (2, 2), (3, 3)], 50), throwsArgumentError);
  });
}
