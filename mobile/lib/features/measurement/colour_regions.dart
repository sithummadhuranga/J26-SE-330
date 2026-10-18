import 'dart:math' as math;
import 'dart:typed_data';

/// One colour group of a wound: its number (1 is the largest group), its share of the wound in percent, and its
/// average colour. It describes colour only, not tissue type.
class ColourRegion {
  const ColourRegion(this.cluster, this.percent, this.centre);

  final int cluster;
  final double percent;

  /// Average [r, g, b], 0..255.
  final List<double> centre;
}

/// Groups the wound's pixels into [k] colour regions with a K-Means that has no random start, so it gives the same answer
/// as research/IT23294066/colour-regions/colour_regions.py (see that file for the steps and why k is 3).
///
/// [pixels] holds an image row by row with [channels] bytes per pixel (3 for RGB, 4 for RGBA), and [mask] has one 0/1
/// byte per pixel. Percentages are rounded to one decimal and add up to 100.0. An empty mask gives an empty list.
List<ColourRegion> colourRegions(
  Uint8List pixels,
  Uint8List mask, {
  int channels = 4,
  int k = 3,
  int maxSample = 4000,
  int maxIter = 30,
}) {
  final wound = <int>[
    for (var p = 0; p < mask.length; p++)
      if (mask[p] != 0) p,
  ];
  final n = wound.length;
  if (n == 0) return const [];
  final r = Float64List(n), g = Float64List(n), b = Float64List(n);
  for (var i = 0; i < n; i++) {
    final o = wound[i] * channels;
    r[i] = pixels[o].toDouble();
    g[i] = pixels[o + 1].toDouble();
    b[i] = pixels[o + 2].toDouble();
  }

  // Every step-th wound pixel, in row order.
  final step = (n + maxSample - 1) ~/ maxSample;
  final sample = <int>[for (var i = 0; i < n; i += step) i];
  final m = sample.length;

  double dist(int i, double cr, double cg, double cb) {
    final dr = r[i] - cr, dg = g[i] - cg, db = b[i] - cb;
    return math.sqrt(dr * dr + dg * dg + db * db);
  }

  var mr = 0.0, mg = 0.0, mb = 0.0;
  for (final i in sample) {
    mr += r[i];
    mg += g[i];
    mb += b[i];
  }
  mr /= m;
  mg /= m;
  mb /= m;

  // First centre: the pixel closest to the mean. Then always the pixel farthest from the centres chosen so far.
  var first = sample[0];
  var best = dist(first, mr, mg, mb);
  for (final i in sample) {
    final d = dist(i, mr, mg, mb);
    if (d < best) {
      best = d;
      first = i;
    }
  }
  final cr = <double>[r[first]], cg = <double>[g[first]], cb = <double>[b[first]];
  final nearest = [for (final i in sample) dist(i, cr[0], cg[0], cb[0])];
  while (cr.length < math.min(k, m)) {
    var far = 0;
    for (var j = 1; j < m; j++) {
      if (nearest[j] > nearest[far]) far = j;
    }
    if (nearest[far] == 0) break; // fewer different colours than k
    final p = sample[far];
    cr.add(r[p]);
    cg.add(g[p]);
    cb.add(b[p]);
    for (var j = 0; j < m; j++) {
      nearest[j] = math.min(nearest[j], dist(sample[j], r[p], g[p], b[p]));
    }
  }
  final count = cr.length;

  int closest(int i) {
    var c = 0;
    var d = dist(i, cr[0], cg[0], cb[0]);
    for (var j = 1; j < count; j++) {
      final dj = dist(i, cr[j], cg[j], cb[j]);
      if (dj < d) {
        d = dj;
        c = j;
      }
    }
    return c;
  }

  // Lloyd iterations on the sample until no pixel changes group.
  List<int>? labels;
  for (var it = 0; it < maxIter; it++) {
    final next = [for (final i in sample) closest(i)];
    if (labels != null && _same(labels, next)) break;
    labels = next;
    for (var c = 0; c < count; c++) {
      var sr = 0.0, sg = 0.0, sb = 0.0, size = 0;
      for (var j = 0; j < m; j++) {
        if (labels[j] != c) continue;
        sr += r[sample[j]];
        sg += g[sample[j]];
        sb += b[sample[j]];
        size++;
      }
      if (size > 0) {
        cr[c] = sr / size;
        cg[c] = sg / size;
        cb[c] = sb / size;
      }
    }
  }

  // Every wound pixel goes to its nearest centre; groups are numbered from largest to smallest.
  final counts = List<int>.filled(count, 0);
  for (var i = 0; i < n; i++) {
    counts[closest(i)]++;
  }
  final order = [
    for (var c = 0; c < count; c++)
      if (counts[c] > 0) c,
  ]..sort((a, c) => counts[c] != counts[a] ? counts[c].compareTo(counts[a]) : a.compareTo(c));

  final pct = [for (final c in order) _halfUp(counts[c] / n * 1000, 10)];
  var sum = 0.0;
  for (final p in pct) {
    sum += p;
  }
  pct[0] = _halfUp((pct[0] + (100.0 - sum)) * 10, 10); // rounding leftovers go to the largest group
  return [
    for (var i = 0; i < order.length; i++)
      ColourRegion(i + 1, pct[i], [
        _halfUp(cr[order[i]] * 100, 100),
        _halfUp(cg[order[i]] * 100, 100),
        _halfUp(cb[order[i]] * 100, 100),
      ]),
  ];
}

bool _same(List<int> a, List<int> b) {
  for (var i = 0; i < a.length; i++) {
    if (a[i] != b[i]) return false;
  }
  return true;
}

double _halfUp(double x, double scale) => (x + 0.5).floorToDouble() / scale;
