#include "stages.hpp"

#include <opencv2/imgproc.hpp>
#include <opencv2/photo.hpp>

#include <algorithm>
#include <array>
#include <cmath>

namespace cc {
namespace {

constexpr double kPi = 3.14159265358979323846;

void checkRgb(const cv::Mat& m) {
  if (m.type() != CV_8UC3) throw std::invalid_argument("expected a uint8 RGB image (CV_8UC3)");
}

// numpy.percentile (linear interpolation) over a 256-bin histogram of uint8 values.
// `value(v)` maps a bin to the number that is interpolated, so float32-converted planes work too.
template <typename F>
double histPercentile(const std::array<int64_t, 256>& hist, int64_t n, double pct, F value) {
  auto kth = [&](int64_t k) {
    int64_t acc = 0;
    for (int v = 0; v < 256; ++v) {
      acc += hist[v];
      if (acc > k) return v;
    }
    return 255;
  };
  double idx = pct / 100.0 * double(n - 1);
  int64_t lo = int64_t(std::floor(idx));
  int64_t hi = std::min<int64_t>(lo + 1, n - 1);
  double frac = idx - double(lo);
  double a = value(kth(lo)), b = value(kth(hi));
  return a + frac * (b - a);
}

std::array<int64_t, 256> histogram(const cv::Mat& m, int channel = -1) {
  std::array<int64_t, 256> h{};
  int ch = m.channels();
  for (int y = 0; y < m.rows; ++y) {
    const uchar* p = m.ptr<uchar>(y);
    for (int x = 0; x < m.cols; ++x) {
      if (channel < 0) {
        for (int c = 0; c < ch; ++c) ++h[p[x * ch + c]];
      } else {
        ++h[p[x * ch + channel]];
      }
    }
  }
  return h;
}

// rgb_to_lab / lab_to_rgb: float64 LAB with L in [0,100] and signed a*, b*.
cv::Mat toLabF64(const cv::Mat& rgb) {
  cv::Mat lab8 = rgbToLab8(rgb), lab;
  lab8.convertTo(lab, CV_64FC3);
  for (int y = 0; y < lab.rows; ++y) {
    auto* p = lab.ptr<cv::Vec3d>(y);
    for (int x = 0; x < lab.cols; ++x) {
      p[x][0] *= 100.0 / 255.0;
      p[x][1] -= 128.0;
      p[x][2] -= 128.0;
    }
  }
  return lab;
}

cv::Mat labF64ToRgb(const cv::Mat& lab) {
  cv::Mat packed(lab.size(), CV_8UC3);
  for (int y = 0; y < lab.rows; ++y) {
    const auto* s = lab.ptr<cv::Vec3d>(y);
    auto* d = packed.ptr<cv::Vec3b>(y);
    for (int x = 0; x < lab.cols; ++x) {
      double v[3] = {s[x][0] * (255.0 / 100.0), s[x][1] + 128.0, s[x][2] + 128.0};
      for (int c = 0; c < 3; ++c) d[x][c] = cv::saturate_cast<uchar>(std::nearbyint(v[c]));
    }
  }
  cv::Mat out;
  cv::cvtColor(packed, out, cv::COLOR_Lab2RGB);
  return out;
}

cv::Mat retinexLog(const cv::Mat& L64, const std::vector<double>& sigmas, int workSize) {
  int h = L64.rows, w = L64.cols;
  cv::Mat L32;
  L64.convertTo(L32, CV_32F);
  double scale = workSize <= 0 ? 1.0 : std::min(1.0, double(workSize) / std::max(h, w));
  cv::Mat small = L32;
  if (scale != 1.0) {
    cv::resize(L32, small,
               cv::Size(std::max(1, int(std::nearbyint(w * scale))), std::max(1, int(std::nearbyint(h * scale)))),
               0, 0, cv::INTER_AREA);
  }
  cv::Mat logL;
  cv::log(L32 + 1.0f, logL);
  cv::Mat acc = cv::Mat::zeros(h, w, CV_32F);
  for (double sigma : sigmas) {
    cv::Mat blur, logBlur;
    cv::GaussianBlur(small, blur, cv::Size(0, 0), sigma);
    if (scale != 1.0) cv::resize(blur, blur, cv::Size(w, h), 0, 0, cv::INTER_LINEAR);
    cv::log(blur + 1.0f, logBlur);
    acc += logL - logBlur;
  }
  acc /= float(sigmas.size());
  cv::Mat r;
  acc.convertTo(r, CV_64F);
  return r;
}

cv::Mat applyRetinex(const cv::Mat& rgb, const std::vector<double>& sigmas, int workSize) {
  checkRgb(rgb);
  cv::Mat lab = toLabF64(rgb);
  std::vector<cv::Mat> ch;
  cv::split(lab, ch);
  cv::Mat& L = ch[0];
  cv::Mat R = retinexLog(L, sigmas, workSize);
  double mean = cv::mean(L)[0];
  for (int y = 0; y < L.rows; ++y) {
    double* l = L.ptr<double>(y);
    const double* r = R.ptr<double>(y);
    for (int x = 0; x < L.cols; ++x) l[x] = std::clamp((mean + 1.0) * std::exp(r[x]) - 1.0, 0.0, 100.0);
  }
  cv::merge(ch, lab);
  return labF64ToRgb(lab);  // a*/b* untouched, so erythema survives
}

cv::Mat lutU8(const cv::Mat& m, const uchar* lut) {
  cv::Mat out(m.size(), m.type());
  cv::LUT(m, cv::Mat(1, 256, CV_8U, const_cast<uchar*>(lut)), out);
  return out;
}

cv::Rect clampRect(const cv::Rect& r, const cv::Size& s) { return r & cv::Rect(0, 0, s.width, s.height); }

}  // namespace

// preprocess.py -------------------------------------------------------------------------------

cv::Mat rgbToLab8(const cv::Mat& rgb) {
  cv::Mat lab;
  cv::cvtColor(rgb, lab, cv::COLOR_RGB2Lab);
  return lab;
}

cv::Mat processRawInput(const cv::Mat& rgb, double lowPct, double highPct, bool denoise) {
  checkRgb(rgb);
  cv::Mat out = rgb;
  auto hist = histogram(rgb);
  int64_t n = int64_t(rgb.total()) * 3;
  auto id = [](int v) { return double(v); };
  double lo = histPercentile(hist, n, lowPct, id), hi = histPercentile(hist, n, highPct, id);
  if (hi - lo > 1e-6) {
    uchar lut[256];
    double gain = 255.0 / (hi - lo);
    for (int v = 0; v < 256; ++v) lut[v] = uchar(std::clamp((double(v) - lo) * gain, 0.0, 255.0));  // truncates
    out = lutU8(rgb, lut);
  }
  if (denoise) {
    cv::Mat f;
    cv::bilateralFilter(out, f, 5, 25, 5);
    out = f;
  }
  return out;
}

cv::Mat letterbox(const cv::Mat& rgb, int size, Letterbox* info) {
  checkRgb(rgb);
  int h = rgb.rows, w = rgb.cols;
  double scale = double(size) / std::max(h, w);
  int nh = std::max(1, int(std::nearbyint(h * scale))), nw = std::max(1, int(std::nearbyint(w * scale)));
  cv::Mat resized;
  cv::resize(rgb, resized, cv::Size(nw, nh), 0, 0, scale < 1 ? cv::INTER_AREA : cv::INTER_CUBIC);
  int top = (size - nh) / 2, left = (size - nw) / 2;
  cv::Mat canvas = cv::Mat::zeros(size, size, CV_8UC3);
  resized.copyTo(canvas(cv::Rect(left, top, nw, nh)));
  if (info) *info = {scale, left, top, h, w, nh, nw};
  return canvas;
}

cv::Mat finalNormalise(const cv::Mat& rgb, bool zscore) {
  checkRgb(rgb);
  cv::Mat x;
  rgb.convertTo(x, CV_32FC3, 1.0 / 255.0);
  if (!zscore) {
    const float mean[3] = {0.485f, 0.456f, 0.406f}, stdv[3] = {0.229f, 0.224f, 0.225f};
    for (int y = 0; y < x.rows; ++y) {
      auto* p = x.ptr<cv::Vec3f>(y);
      for (int c = 0; c < x.cols; ++c)
        for (int k = 0; k < 3; ++k) p[c][k] = (p[c][k] - mean[k]) / stdv[k];
    }
    return x;
  }
  cv::Scalar m, s;
  cv::meanStdDev(x, m, s);
  for (int y = 0; y < x.rows; ++y) {
    auto* p = x.ptr<cv::Vec3f>(y);
    for (int c = 0; c < x.cols; ++c)
      for (int k = 0; k < 3; ++k) p[c][k] = float((p[c][k] - m[k]) / std::max(s[k], 1e-6));
  }
  return x;
}

// illumination.py -----------------------------------------------------------------------------

cv::Mat grayWorld(const cv::Mat& rgb) {
  checkRgb(rgb);
  cv::Scalar m = cv::mean(rgb);
  double g = (m[0] + m[1] + m[2]) / 3.0;
  double gains[3] = {g / std::max(m[0], 1e-6), g / std::max(m[1], 1e-6), g / std::max(m[2], 1e-6)};
  cv::Mat out(rgb.size(), CV_8UC3);
  for (int y = 0; y < rgb.rows; ++y) {
    const auto* s = rgb.ptr<cv::Vec3b>(y);
    auto* d = out.ptr<cv::Vec3b>(y);
    for (int x = 0; x < rgb.cols; ++x)
      for (int k = 0; k < 3; ++k) d[x][k] = uchar(std::clamp(s[x][k] * gains[k], 0.0, 255.0));
  }
  return out;
}

cv::Mat singleScaleRetinexL(const cv::Mat& rgb, double sigma, int workSize) {
  return applyRetinex(rgb, {sigma}, workSize);
}

cv::Mat multiScaleRetinexL(const cv::Mat& rgb, int workSize) { return applyRetinex(rgb, {15, 80, 250}, workSize); }

// ita.py --------------------------------------------------------------------------------------

cv::Mat skinMaskHsv(const cv::Mat& rgb, const cv::Rect* wound, double vMin) {
  checkRgb(rgb);
  cv::Mat hsv;
  cv::cvtColor(rgb, hsv, cv::COLOR_RGB2HSV);
  cv::Mat mask(rgb.size(), CV_8U);
  const float vmin = float(vMin);
  for (int y = 0; y < hsv.rows; ++y) {
    const auto* p = hsv.ptr<cv::Vec3b>(y);
    uchar* m = mask.ptr<uchar>(y);
    for (int x = 0; x < hsv.cols; ++x) {
      float h = float(p[x][0]) * 2.0f, s = float(p[x][1]) / 255.0f * 100.0f, v = float(p[x][2]) / 255.0f * 100.0f;
      m[x] = (h >= 0 && h <= 25 && s >= 15 && s <= 80 && v >= vmin && v <= 100) ? 255 : 0;
    }
  }
  if (wound) mask(clampRect(*wound, mask.size())).setTo(0);
  return mask;
}

int fitzpatrickFromIta(double ita) {
  if (ita > 55) return 1;
  if (ita > 41) return 2;
  if (ita > 28) return 3;
  if (ita > 10) return 4;
  if (ita > -30) return 5;
  return 6;
}

std::optional<Ita> classifyImage(const cv::Mat& rgb, const cv::Rect* wound, double vMin, std::string* why,
                                 int minSkinPixels) {
  cv::Mat mask = skinMaskHsv(rgb, wound, vMin);
  int count = cv::countNonZero(mask);
  if (count < minSkinPixels) {
    if (why) *why = "only " + std::to_string(count) + " skin pixels found (need >= " + std::to_string(minSkinPixels) + ")";
    return std::nullopt;
  }
  cv::Mat lab = rgbToLab8(rgb);
  double sumL = 0, sumB = 0;
  for (int y = 0; y < lab.rows; ++y) {
    const auto* p = lab.ptr<cv::Vec3b>(y);
    const uchar* m = mask.ptr<uchar>(y);
    for (int x = 0; x < lab.cols; ++x) {
      if (!m[x]) continue;
      sumL += double(p[x][0]) * (100.0 / 255.0);
      sumB += double(p[x][2]) - 128.0;
    }
  }
  Ita r;
  r.meanL = sumL / count;
  r.meanB = sumB / count;
  r.angle = std::atan2(r.meanL - 50.0, r.meanB) * (180.0 / kPi);
  r.skinPixels = count;
  r.fst = fitzpatrickFromIta(r.angle);
  return r;
}

// ccm.py --------------------------------------------------------------------------------------

namespace {

constexpr double kLCeiling = 95.0;
constexpr int kMaxKmeansPixels = 20000;
constexpr double kReferenceSkinLStd = 8.5;
constexpr double kGainLo = 0.8, kGainHi = 1.25;
constexpr double kFraction[7] = {0, 0.0, 0.0, 0.25, 0.5, 0.75, 1.0};  // index = FST 1..6

cv::Vec3d labOfRgb(uchar r, uchar g, uchar b) {
  cv::Mat px(1, 1, CV_8UC3, cv::Scalar(r, g, b)), lab;
  cv::cvtColor(px, lab, cv::COLOR_RGB2Lab);
  auto v = lab.at<cv::Vec3b>(0, 0);
  return {v[0] * (100.0 / 255.0), double(v[1]) - 128.0, double(v[2]) - 128.0};
}

// numpy.percentile(L8, 99) on the uint8 L plane, in OpenCV's 0..255 scale.
double percentileL8(const cv::Mat& lab8, double pct) {
  auto h = histogram(lab8, 0);
  return histPercentile(h, int64_t(lab8.total()), pct, [](int v) { return double(v); });
}

}  // namespace

std::optional<cv::Mat> estimateAdaptiveCcm(const cv::Mat& rgb, int fst, const cv::Rect* wound, const CcmOptions& opt,
                                           std::string* why) {
  cv::Mat mask = skinMaskHsv(rgb, wound, opt.vMin);
  cv::Mat lab8 = rgbToLab8(rgb);
  std::vector<cv::Vec3f> px;
  for (int y = 0; y < lab8.rows; ++y) {
    const auto* p = lab8.ptr<cv::Vec3b>(y);
    const uchar* m = mask.ptr<uchar>(y);
    for (int x = 0; x < lab8.cols; ++x)
      if (m[x]) px.emplace_back(float(p[x][0]) * float(100.0 / 255.0), float(p[x][1]) - 128.0f, float(p[x][2]) - 128.0f);
  }
  if (int(px.size()) < opt.k) {
    if (why) *why = "only " + std::to_string(px.size()) + " skin pixels found, need >= k for clustering";
    return std::nullopt;
  }
  // Python draws a numpy random sample; here an evenly spaced one (deterministic, not identical).
  if (int(px.size()) > kMaxKmeansPixels) {
    std::vector<cv::Vec3f> s(kMaxKmeansPixels);
    for (int i = 0; i < kMaxKmeansPixels; ++i) s[i] = px[size_t(double(i) * double(px.size()) / kMaxKmeansPixels)];
    px.swap(s);
  }
  cv::Mat samples(int(px.size()), 3, CV_32F, px.data()), labels, centers;
  cv::setRNGSeed(int(opt.seed));
  cv::kmeans(samples, opt.k, labels, cv::TermCriteria(cv::TermCriteria::EPS + cv::TermCriteria::COUNT, 100, 0.2), 10,
             cv::KMEANS_PP_CENTERS, centers);
  std::vector<double> counts(opt.k, 0.0);
  for (int i = 0; i < labels.rows; ++i) counts[labels.at<int>(i)] += 1.0;

  cv::Mat src(opt.k, 3, CV_64F);
  for (int i = 0; i < opt.k; ++i)
    for (int c = 0; c < 3; ++c) src.at<double>(i, c) = double(centers.at<float>(i, c));

  cv::Vec3d shift;
  cv::Vec3d light = labOfRgb(194, 150, 130), dark = labOfRgb(115, 82, 68);
  if (opt.fixedShift) {
    shift = light - dark;
  } else {
    double skinL = 0, total = 0;
    for (int i = 0; i < opt.k; ++i) {
      skinL += src.at<double>(i, 0) * counts[i];
      total += counts[i];
    }
    skinL /= total;
    double gap = std::max(0.0, light[0] - skinL);
    if (opt.stratified) gap *= kFraction[fst];
    if (gap > 0) {
      double p99 = percentileL8(lab8, 99.0) * 100.0 / 255.0;
      double maxGain = std::max(1.0, kLCeiling / std::max(p99, 1e-6));
      gap = std::min(gap, (maxGain - 1.0) * skinL);
    }
    shift = {gap, 0, 0};
  }
  cv::Mat delta = cv::Mat::zeros(opt.k, 3, CV_64F);
  for (int i = 0; i < opt.k; ++i)
    for (int c = 0; c < 3; ++c) delta.at<double>(i, c) = shift[c];
  // M = I + argmin |src * X - delta|: minimum-norm solution, like numpy.linalg.lstsq.
  cv::Mat X;
  cv::solve(src, delta, X, cv::DECOMP_SVD);
  return cv::Mat::eye(3, 3, CV_64F) + X;
}

cv::Mat applyAdaptiveCcm(const cv::Mat& rgb, const cv::Mat& M) {
  checkRgb(rgb);
  cv::Mat lab8 = rgbToLab8(rgb);
  cv::Mat packed(rgb.size(), CV_8UC3);
  double m[3][3];
  for (int i = 0; i < 3; ++i)
    for (int j = 0; j < 3; ++j) m[i][j] = M.at<double>(i, j);
  for (int y = 0; y < lab8.rows; ++y) {
    const auto* s = lab8.ptr<cv::Vec3b>(y);
    auto* d = packed.ptr<cv::Vec3b>(y);
    for (int x = 0; x < lab8.cols; ++x) {
      double v[3] = {s[x][0] * (100.0 / 255.0), double(s[x][1]) - 128.0, double(s[x][2]) - 128.0};
      double o[3];
      for (int j = 0; j < 3; ++j) o[j] = v[0] * m[0][j] + v[1] * m[1][j] + v[2] * m[2][j];
      o[0] *= 255.0 / 100.0;
      o[1] += 128.0;
      o[2] += 128.0;
      for (int c = 0; c < 3; ++c) d[x][c] = uchar(std::clamp(o[c], 0.0, 255.0));  // truncates, as astype(uint8)
    }
  }
  cv::Mat out;
  cv::cvtColor(packed, out, cv::COLOR_Lab2RGB);
  return out;
}

cv::Mat normaliseSkinLVariance(const cv::Mat& rgb, const cv::Mat& skinMask) {
  if (cv::countNonZero(skinMask) < 50) return rgb.clone();
  cv::Mat lab8 = rgbToLab8(rgb);
  const float k = float(100.0 / 255.0);
  double sum = 0, sum2 = 0;
  int64_t n = 0;
  for (int y = 0; y < lab8.rows; ++y) {
    const auto* p = lab8.ptr<cv::Vec3b>(y);
    const uchar* m = skinMask.ptr<uchar>(y);
    for (int x = 0; x < lab8.cols; ++x) {
      if (!m[x]) continue;
      double l = double(float(p[x][0]) * k);
      sum += l;
      sum2 += l * l;
      ++n;
    }
  }
  double mean = sum / n, stdv = std::sqrt(std::max(0.0, sum2 / n - mean * mean));
  if (stdv < 1e-6) return rgb.clone();
  double gain = std::clamp(kReferenceSkinLStd / stdv, kGainLo, kGainHi);
  if (gain > 1.0) {
    auto h = histogram(lab8, 0);
    double p99 = histPercentile(h, int64_t(lab8.total()), 99.0, [&](int v) { return double(float(v) * k); });
    if (p99 > mean) gain = std::max(1.0, std::min(gain, (kLCeiling - mean) / (p99 - mean)));
  }
  for (int y = 0; y < lab8.rows; ++y) {
    auto* p = lab8.ptr<cv::Vec3b>(y);
    for (int x = 0; x < lab8.cols; ++x) {
      double l = double(float(p[x][0]) * k);
      double o = std::clamp((mean + (l - mean) * gain) * (255.0 / 100.0), 0.0, 255.0);
      p[x][0] = uchar(std::nearbyint(o));
    }
  }
  cv::Mat out;
  cv::cvtColor(lab8, out, cv::COLOR_Lab2RGB);
  return out;
}

// enhance.py ----------------------------------------------------------------------------------

cv::Mat removeSpecular(const cv::Mat& rgb) {
  checkRgb(rgb);
  cv::Mat hsv;
  cv::cvtColor(rgb, hsv, cv::COLOR_RGB2HSV);
  cv::Mat mask(rgb.size(), CV_8U);
  for (int y = 0; y < hsv.rows; ++y) {
    const auto* p = hsv.ptr<cv::Vec3b>(y);
    uchar* m = mask.ptr<uchar>(y);
    for (int x = 0; x < hsv.cols; ++x) m[x] = (p[x][2] > 240 && p[x][1] < 30) ? 1 : 0;
  }
  if (cv::countNonZero(mask) == 0) return rgb.clone();
  cv::dilate(mask, mask, cv::getStructuringElement(cv::MORPH_ELLIPSE, cv::Size(5, 5)));
  mask *= 255;
  cv::Mat bgr, out;
  cv::cvtColor(rgb, bgr, cv::COLOR_RGB2BGR);
  cv::inpaint(bgr, mask, out, 3, cv::INPAINT_TELEA);
  cv::cvtColor(out, out, cv::COLOR_BGR2RGB);
  return out;
}

cv::Mat claheMedian(const cv::Mat& rgb) {
  checkRgb(rgb);
  cv::Mat lab = rgbToLab8(rgb);
  std::vector<cv::Mat> ch;
  cv::split(lab, ch);
  cv::createCLAHE(2.0, cv::Size(8, 8))->apply(ch[0], ch[0]);
  cv::merge(ch, lab);
  cv::Mat out;
  cv::cvtColor(lab, out, cv::COLOR_Lab2RGB);
  cv::medianBlur(out, out, 3);
  return out;
}

}  // namespace cc
