// Checks the C++ port against the Python reference (fixtures from scripts/make_native_fixtures.py).
// Usage: parity_test <fixtures dir>
#include "colorimetric/colorimetric.h"
#include "stages.hpp"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <cmath>
#include <cstdio>
#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <vector>

namespace {

struct Case {
  std::string kind, name, input, expected;
  std::map<std::string, std::string> kv;
};

std::vector<Case> readManifest(const std::string& dir) {
  std::ifstream in(dir + "/manifest.tsv");
  std::vector<Case> cases;
  for (std::string line; std::getline(in, line);) {
    std::vector<std::string> f;
    std::stringstream ss(line);
    for (std::string t; std::getline(ss, t, '\t');) f.push_back(t);
    if (f.size() < 4) continue;
    Case c{f[0], f[1], f[2], f[3], {}};
    for (size_t i = 4; i < f.size(); ++i) {
      auto eq = f[i].find('=');
      c.kv[f[i].substr(0, eq)] = f[i].substr(eq + 1);
    }
    cases.push_back(std::move(c));
  }
  return cases;
}

cv::Mat readRgb(const std::string& path) {
  cv::Mat bgr = cv::imread(path, cv::IMREAD_COLOR), rgb;
  if (bgr.empty()) throw std::runtime_error("cannot read " + path);
  cv::cvtColor(bgr, rgb, cv::COLOR_BGR2RGB);
  return rgb;
}

struct Diff {
  double maxAbs, meanAbs;
};

Diff diff(const cv::Mat& a, const cv::Mat& b) {
  if (a.size() != b.size() || a.type() != b.type()) return {1e9, 1e9};
  cv::Mat d;
  cv::absdiff(a, b, d);
  d = d.reshape(1);
  double mx;
  cv::minMaxLoc(d, nullptr, &mx);
  return {mx, cv::mean(d)[0]};
}

// Bounds measured on the first full run on the arm64 emulator (187 cases): stages differ by at most
// 2 levels with a mean of 0.0000, the pipeline without the CCM by at most 4 levels (mean under 0.001), and
// with the CCM by at most 13 (mean 0.011) because k-means sees an evenly spaced sample, not numpy's random one.
struct Tol {
  double maxAbs, meanAbs;
};
Tol tolFor(const Case& c) {
  if (c.kind == "stage_raw_input" || c.kind == "stage_gray_world" || c.kind == "stage_specular" || c.kind == "stage_clahe")
    return {1, 0.001};
  if (c.kind == "stage_msr" || c.kind == "stage_ssr") return {2, 0.001};
  if (c.kind == "pipeline") return c.kv.at("ccm") == "1" ? Tol{16, 0.03} : Tol{4, 0.01};
  return {0, 0};
}

// The legacy v1 CCM (ablation variant full_ccm_v1) adds a fixed +27.8 L*, which saturates the image, so the
// pixels depend on the k-means sample: Python run against itself with an evenly spaced sample instead of
// numpy's random one differs by the same amount (mean 35.5 on p03_tan_skin) as the C++ port does. Only the
// decisions (FST class, whether the CCM ran) are compared for it.
bool pixelsComparable(const Case& c) { return !(c.kv.at("variant") == "full_ccm_v1" && c.kv.at("ccm") == "1"); }

cc_config configOf(const Case& c) {
  cc_config cfg = cc_default_config();
  cfg.enabled_stages = unsigned(std::stoi(c.kv.at("stages")));  // stage 7 is off in these fixtures
  cfg.illumination = std::stoi(c.kv.at("illum"));
  cfg.ccm_fixed_shift = std::stoi(c.kv.at("fixed"));
  cfg.ccm_stratified = std::stoi(c.kv.at("strat"));
  cfg.ccm_l_variance = std::stoi(c.kv.at("lvar"));
  cfg.gate_cutoff = std::stod(c.kv.at("cutoff"));
  return cfg;
}

const char* kRoman[] = {"none", "I", "II", "III", "IV", "V", "VI"};

}  // namespace

int main(int argc, char** argv) {
  if (argc < 2) {
    std::fprintf(stderr, "usage: parity_test <fixtures dir>\n");
    return 2;
  }
  int failed = 0, total = 0;
  auto report = [&](const Case& c, bool ok, const std::string& detail) {
    ++total;
    if (!ok) ++failed;
    std::printf("%s %-16s %-24s %s\n", ok ? "ok  " : "FAIL", c.kind.c_str(), c.name.c_str(), detail.c_str());
  };
  auto check = [&](const Case& c, const cv::Mat& got, const std::string& label = "") {
    Diff d = diff(got, readRgb(c.expected));
    Tol t = tolFor(c);
    char buf[160];
    std::snprintf(buf, sizeof buf, "%s max %.0f mean %.4f (limit %.0f / %.3f)", label.c_str(), d.maxAbs, d.meanAbs, t.maxAbs, t.meanAbs);
    report(c, d.maxAbs <= t.maxAbs && d.meanAbs <= t.meanAbs, buf);
  };

  for (const Case& c : readManifest(argv[1])) {
    try {
      cv::Mat rgb = readRgb(c.input);
      if (c.kind == "stage_raw_input") check(c, cc::processRawInput(rgb));
      else if (c.kind == "stage_msr") check(c, cc::multiScaleRetinexL(rgb));
      else if (c.kind == "stage_ssr") check(c, cc::singleScaleRetinexL(rgb));
      else if (c.kind == "stage_gray_world") check(c, cc::grayWorld(rgb));
      else if (c.kind == "stage_specular") check(c, cc::removeSpecular(rgb));
      else if (c.kind == "stage_clahe") check(c, cc::claheMedian(rgb));
      else if (c.kind == "ita") {
        std::string why;
        auto r = cc::classifyImage(rgb, nullptr, 20.0, &why);
        if (c.kv.at("fst") == "none") {
          report(c, !r, r ? "expected no estimate" : "no estimate, as Python");
        } else if (!r) {
          report(c, false, "no estimate: " + why);
        } else {
          double want = std::stod(c.kv.at("angle"));
          bool ok = std::fabs(r->angle - want) < 1e-3 && kRoman[r->fst] == c.kv.at("fst") && r->skinPixels == std::stoi(c.kv.at("skin"));
          char buf[120];
          std::snprintf(buf, sizeof buf, "ita %.4f vs %.4f, fst %s vs %s, skin %d", r->angle, want, kRoman[r->fst], c.kv.at("fst").c_str(), r->skinPixels);
          report(c, ok, buf);
        }
      } else if (c.kind == "tensor") {
        cv::Mat got = cc::finalNormalise(cc::letterbox(rgb, 512), c.kv.at("mode") == "zscore");
        std::ifstream f(c.expected, std::ios::binary);
        std::vector<float> want(got.total() * 3);
        f.read(reinterpret_cast<char*>(want.data()), std::streamsize(want.size() * sizeof(float)));
        double mx = 0;
        const float* g = got.ptr<float>();
        for (size_t i = 0; i < want.size(); ++i) mx = std::max(mx, double(std::fabs(g[i] - want[i])));
        char buf[80];
        std::snprintf(buf, sizeof buf, "%s max abs %.5f (limit 0.005)", c.kv.at("mode").c_str(), mx);
        report(c, f.good() && mx <= 0.005, buf);
      } else if (c.kind == "pipeline") {
        cc_config cfg = configOf(c);
        char err[256];
        void* p = cc_create(&cfg, err);
        cc_result r;
        int rc = p ? cc_process(p, rgb.data, rgb.cols, rgb.rows, nullptr, &r) : -9;
        if (rc != 0) {
          report(c, false, p ? cc_last_error(p) : err);
        } else {
          cv::Mat got(r.height, r.width, CV_8UC3, const_cast<uint8_t*>(r.calibrated_rgb));
          bool sameFst = c.kv.at("fst") == kRoman[r.fitzpatrick_class] || (c.kv.at("fst") == "none" && r.fitzpatrick_class == 0);
          bool sameCcm = r.ccm_applied == std::stoi(c.kv.at("ccm"));
          Diff d = diff(got, readRgb(c.expected));
          Tol t = tolFor(c);
          char buf[200];
          std::snprintf(buf, sizeof buf, "%s fst %s ccm %d | max %.0f mean %.4f (limit %.0f / %.3f%s) | %.1f ms", c.kv.at("variant").c_str(),
                        kRoman[r.fitzpatrick_class], r.ccm_applied, d.maxAbs, d.meanAbs, t.maxAbs, t.meanAbs, pixelsComparable(c) ? "" : ", pixels not compared", r.total_ms);
          bool pixelsOk = !pixelsComparable(c) || (d.maxAbs <= t.maxAbs && d.meanAbs <= t.meanAbs);
          report(c, sameFst && sameCcm && pixelsOk, buf);
        }
        cc_destroy(p);
      }
    } catch (const std::exception& e) {
      report(c, false, e.what());
    }
  }
  std::printf("\n%d cases, %d failed\n", total, failed);
  return failed ? 1 : 0;
}
