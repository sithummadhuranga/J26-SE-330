// Edge cases of the C interface: bad arguments must be refused with a code, never crash or read past a
// buffer. Meant to run under ASan/UBSan too (build_android.sh sanitize). Usage: api_test
#include "colorimetric/colorimetric.h"

#include <unistd.h>

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <csignal>
#include <dlfcn.h>
#include <random>
#include <string>
#include <vector>

namespace {

int failed = 0, total = 0;

void expect(bool ok, const std::string& what) {
  ++total;
  if (!ok) ++failed;
  std::printf("%s %s\n", ok ? "ok  " : "FAIL", what.c_str());
}

std::vector<uint8_t> skinLike(int w, int h, unsigned seed) {
  std::mt19937 rng(seed);
  std::vector<uint8_t> px(size_t(w) * h * 3);
  for (size_t i = 0; i < px.size(); i += 3) {
    px[i] = uint8_t(150 + rng() % 40);
    px[i + 1] = uint8_t(100 + rng() % 40);
    px[i + 2] = uint8_t(80 + rng() % 40);
  }
  return px;
}

}  // namespace

// Say where a SIGILL happened (library + offset, for llvm-addr2line) instead of dying silently.
void onIllegal(int, siginfo_t* info, void*) {
  Dl_info d;
  if (dladdr(info->si_addr, &d))
    std::fprintf(stderr, "SIGILL at %s+0x%lx\n", d.dli_fname, (unsigned long)((char*)info->si_addr - (char*)d.dli_fbase));
  else
    std::fprintf(stderr, "SIGILL at %p\n", info->si_addr);
  _exit(132);
}

int main() {
  struct sigaction sa;
  std::memset(&sa, 0, sizeof sa);
  sa.sa_sigaction = onIllegal;
  sa.sa_flags = SA_SIGINFO;
  sigaction(SIGILL, &sa, nullptr);
  std::setvbuf(stdout, nullptr, _IONBF, 0);  // keep the lines printed so far if a sanitizer kills the process
  cc_config cfg = cc_default_config();
  char err[256] = {0};
  void* p = cc_create(&cfg, err);
  expect(p != nullptr, "create with the default config");
  cc_result r;
  auto img = skinLike(64, 48, 1);
  const uint64_t n = img.size();

  expect(cc_process(nullptr, img.data(), n, 64, 48, nullptr, &r) == CC_ERR_ARGUMENT, "null pipeline");
  expect(cc_process(p, nullptr, n, 64, 48, nullptr, &r) == CC_ERR_ARGUMENT, "null image");
  expect(cc_process(p, img.data(), n, 64, 48, nullptr, nullptr) == CC_ERR_ARGUMENT, "null result");
  expect(cc_process(p, img.data(), n, 0, 48, nullptr, &r) == CC_ERR_SIZE, "zero width");
  expect(cc_process(p, img.data(), n, 64, -1, nullptr, &r) == CC_ERR_SIZE, "negative height");
  expect(cc_process(p, img.data(), n, CC_MAX_SIDE + 1, 48, nullptr, &r) == CC_ERR_SIZE, "width above the limit");
  expect(cc_process(p, img.data(), n, 30000, 30000, nullptr, &r) == CC_ERR_SIZE, "width*height*3 above 2^31 (would overflow an int)");
  expect(cc_process(p, img.data(), n - 1, 64, 48, nullptr, &r) == CC_ERR_LENGTH, "buffer one byte short");
  expect(cc_process(p, img.data(), n + 1, 64, 48, nullptr, &r) == CC_ERR_LENGTH, "buffer one byte long");
  expect(cc_process(p, img.data(), 0, 64, 48, nullptr, &r) == CC_ERR_LENGTH, "length zero");
  expect(std::strlen(cc_last_error(p)) > 0, "error text is set after a refusal");

  expect(cc_process(p, img.data(), n, 64, 48, nullptr, &r) == CC_OK, "valid call after refusals");
  expect(r.width == 64 && r.height == 48 && r.calibrated_rgb != nullptr, "output keeps the input size");
  expect(std::strlen(cc_last_error(p)) == 0, "error text cleared after success");

  // Tiny and odd sizes: any code is fine, a crash or a sanitizer report is not.
  for (auto wh : std::vector<std::pair<int, int>>{{1, 1}, {1, 7}, {7, 1}, {2, 2}, {3, 5}, {17, 3}, {100, 1}, {5, 300}}) {
    auto t = skinLike(wh.first, wh.second, 2);
    int rc = cc_process(p, t.data(), t.size(), wh.first, wh.second, nullptr, &r);
    expect(rc == CC_OK || rc == CC_ERR_PROCESSING, "tiny image " + std::to_string(wh.first) + "x" + std::to_string(wh.second) + " -> " + std::to_string(rc));
  }

  // Wound boxes that are partly or wholly outside the image, empty, or inverted.
  for (cc_bbox b : {cc_bbox{-10, -10, 30, 30}, cc_bbox{60, 40, 100, 100}, cc_bbox{0, 0, 0, 0}, cc_bbox{5, 5, -3, -3},
                    cc_bbox{1000, 1000, 5, 5}, cc_bbox{0, 0, 64, 48}, cc_bbox{2147483000, 0, 2147483000, 10}}) {
    int rc = cc_process(p, img.data(), n, 64, 48, &b, &r);
    expect(rc == CC_OK || rc == CC_ERR_PROCESSING, "wound box " + std::to_string(b.x) + "," + std::to_string(b.y) + "," + std::to_string(b.w) + "," + std::to_string(b.h));
  }

  // Every stage combination, both illumination modes, on random pixels.
  int bad = 0;
  for (uint32_t stages = 0; stages <= CC_STAGE_ALL; ++stages) {
    for (int illum = 0; illum <= CC_ILLUM_GRAY_WORLD; ++illum) {
      cc_config c = cc_default_config();
      c.enabled_stages = stages;
      c.illumination = illum;
      void* q = cc_create(&c, nullptr);
      auto t = skinLike(40, 30, stages * 7 + illum);
      int rc = cc_process(q, t.data(), t.size(), 40, 30, nullptr, &r);
      if (rc != CC_OK || r.width != 40 || r.height != 30 || ((stages & CC_STAGE_FINAL) != 0) != (r.tensor != nullptr)) ++bad;
      cc_destroy(q);
    }
  }
  expect(bad == 0, "all 128 stage sets x 4 illumination modes run and keep the size");

  // Constant, black and white images (degenerate statistics).
  for (int v : {0, 255, 128}) {
    std::vector<uint8_t> flat(size_t(50) * 50 * 3, uint8_t(v));
    int rc = cc_process(p, flat.data(), flat.size(), 50, 50, nullptr, &r);
    expect(rc == CC_OK || rc == CC_ERR_PROCESSING, "flat image " + std::to_string(v));
  }

  // Bad configs are refused by cc_create.
  cc_config bc = cc_default_config();
  bc.enabled_stages = 0xFFFFu;
  expect(cc_create(&bc, err) == nullptr && std::strlen(err) > 0, "unknown stage bits refused");
  bc = cc_default_config();
  bc.ccm_k = 2;
  expect(cc_create(&bc, nullptr) == nullptr, "ccm_k below 3 refused (no error buffer given)");
  expect(cc_create(nullptr, err) == nullptr, "null config refused");

  cc_destroy(p);
  cc_destroy(nullptr);
  expect(true, "destroy null is harmless");
  std::printf("\n%d checks, %d failed\n", total, failed);
  return failed ? 1 : 0;
}
