// Per-stage latency of the C++ pipeline at several photo sizes.
// Usage: bench <photo.png> [runs=20]. The photo is resized (aspect kept) to each size first.
#include "colorimetric/colorimetric.h"

#include <opencv2/imgcodecs.hpp>
#include <opencv2/imgproc.hpp>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <vector>

int main(int argc, char** argv) {
  if (argc < 2) {
    std::fprintf(stderr, "usage: bench <photo.png> [runs]\n");
    return 2;
  }
  int runs = argc > 2 ? std::atoi(argv[2]) : 20;
  cv::Mat bgr = cv::imread(argv[1], cv::IMREAD_COLOR), base;
  if (bgr.empty()) return 2;
  cv::cvtColor(bgr, base, cv::COLOR_BGR2RGB);

  cc_config cfg = cc_default_config();
  void* p = cc_create(&cfg, nullptr);
  const int sizes[][2] = {{512, 384}, {1600, 1200}, {4000, 3000}};
  std::printf("median of %d runs, ms\n", runs);
  std::printf("%-10s", "size");
  for (int i = 0; i < CC_T_COUNT; ++i) std::printf(" %12s", cc_timing_name(i));
  std::printf(" %9s\n", "total");
  for (auto& s : sizes) {
    cv::Mat img;
    cv::resize(base, img, cv::Size(s[0], s[1]), 0, 0, cv::INTER_LINEAR);
    std::vector<std::vector<double>> t(CC_T_COUNT + 1);
    for (int r = 0; r < runs + 1; ++r) {
      cc_result res;
      if (cc_process(p, img.data, img.cols, img.rows, nullptr, &res) != 0) return 1;
      if (r == 0) continue;  // warm-up
      for (int i = 0; i < CC_T_COUNT; ++i) t[i].push_back(res.stage_ms[i]);
      t[CC_T_COUNT].push_back(res.total_ms);
    }
    std::printf("%dx%-5d", s[0], s[1]);
    for (auto& v : t) {
      std::nth_element(v.begin(), v.begin() + v.size() / 2, v.end());
      std::printf(" %12.2f", v[v.size() / 2]);
    }
    std::printf("\n");
  }
  cc_destroy(p);
  return 0;
}
