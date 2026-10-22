// CalibrationPipeline behind the C interface: port of pipeline.process_image + api.CalibrationPipeline.
// Not ported yet (see docs/phase05_plan.md, M2): marker correction, the light/dark model gate and
// white balance. Because of that, skin_type_confidence is always "unchecked" here.
#include "colorimetric/colorimetric.h"
#include "stages.hpp"

#include <opencv2/imgproc.hpp>

#include <chrono>
#include <cstring>
#include <exception>
#include <string>
#include <vector>

namespace {

const char* kTimingNames[CC_T_COUNT] = {"ita",   "1_raw_input", "2_illumination", "4_ccm_fit", "4_ccm_apply",
                                        "4_l_variance", "5_specular", "6_clahe", "7_final"};

struct Pipeline {
  cc_config cfg;
  cv::Mat calibrated;
  cv::Mat tensor;
  std::string notes;
  std::string error;
};

using Clock = std::chrono::steady_clock;
double msSince(Clock::time_point t0) { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); }

bool on(const cc_config& c, unsigned bit) { return (c.enabled_stages & bit) != 0; }

void note(Pipeline& p, const std::string& s) {
  if (!p.notes.empty()) p.notes += '\n';
  p.notes += s;
}

}  // namespace

extern "C" {

const char* cc_version(void) { return "0.1.0"; }

const char* cc_timing_name(int32_t slot) { return slot >= 0 && slot < CC_T_COUNT ? kTimingNames[slot] : ""; }

cc_config cc_default_config(void) {
  cc_config c;
  std::memset(&c, 0, sizeof c);
  c.enabled_stages = CC_STAGE_ALL;
  c.illumination = CC_ILLUM_MSR;
  c.gate_cutoff = 28.0;
  c.skin_v_min = 20.0;
  c.ccm_fixed_shift = 0;
  c.ccm_stratified = 1;
  c.ccm_l_variance = 1;
  c.ccm_k = 6;
  c.classify_on_processed = 0;
  c.retinex_work_size = 512;
  c.model_size = 512;
  c.normalise_zscore = 0;
  c.seed = 0;
  return c;
}

void* cc_create(const cc_config* config, char* error) {
  auto fail = [&](const char* msg) -> void* {
    if (error) {
      std::strncpy(error, msg, 255);
      error[255] = '\0';
    }
    return nullptr;
  };
  if (!config) return fail("config is null");
  if (config->enabled_stages & ~CC_STAGE_ALL) return fail("unknown stage bits");
  if (config->illumination < CC_ILLUM_NONE || config->illumination > CC_ILLUM_GRAY_WORLD) return fail("unknown illumination");
  if (config->ccm_k < 3) return fail("ccm_k must be at least 3");
  if (config->model_size < 1) return fail("model_size must be positive");
  auto* p = new Pipeline();
  p->cfg = *config;
  return p;
}

void cc_destroy(void* pipeline) { delete static_cast<Pipeline*>(pipeline); }

const char* cc_last_error(const void* pipeline) { return pipeline ? static_cast<const Pipeline*>(pipeline)->error.c_str() : "null pipeline"; }

int32_t cc_process(void* handle, const uint8_t* data, int32_t width, int32_t height, const cc_bbox* wound, cc_result* out) {
  if (!handle || !out) return -1;
  auto& p = *static_cast<Pipeline*>(handle);
  p.error.clear();
  p.notes.clear();
  if (!data || width < 1 || height < 1) {
    p.error = "expected a non-empty width*height*3 uint8 RGB buffer";
    return -2;
  }
  try {
    const cc_config& c = p.cfg;
    cc_result r;
    std::memset(&r, 0, sizeof r);
    cv::Mat rgb(height, width, CV_8UC3, const_cast<uint8_t*>(data));  // not owned, never written
    cv::Rect box;
    const cv::Rect* bbox = nullptr;
    if (wound) {
      box = cv::Rect(wound->x, wound->y, wound->w, wound->h);
      bbox = &box;
    }
    auto timed = [&](int slot, auto&& fn) {
      auto t0 = Clock::now();
      auto v = fn();
      r.stage_ms[slot] += msSince(t0);
      return v;
    };

    auto classify = [&](const cv::Mat& img) {
      std::string why;
      auto ita = cc::classifyImage(img, bbox, c.skin_v_min, &why);
      if (!ita) note(p, "skin tone not estimated: " + why);
      return ita;
    };

    std::optional<cc::Ita> ita;
    if (on(c, CC_STAGE_SKIN_TONE) && !c.classify_on_processed) ita = timed(CC_T_ITA, [&] { return classify(rgb); });

    cv::Mat img = rgb;
    if (on(c, CC_STAGE_INPUT)) img = timed(CC_T_RAW_INPUT, [&] { return cc::processRawInput(img); });
    if (on(c, CC_STAGE_ILLUMINATION)) {
      switch (c.illumination) {
        case CC_ILLUM_MSR: img = timed(CC_T_ILLUMINATION, [&] { return cc::multiScaleRetinexL(img, c.retinex_work_size); }); break;
        case CC_ILLUM_SSR: img = timed(CC_T_ILLUMINATION, [&] { return cc::singleScaleRetinexL(img, 80.0, c.retinex_work_size); }); break;
        case CC_ILLUM_GRAY_WORLD: img = timed(CC_T_ILLUMINATION, [&] { return cc::grayWorld(img); }); break;
        default: break;
      }
    }
    if (on(c, CC_STAGE_SKIN_TONE) && c.classify_on_processed) ita = timed(CC_T_ITA, [&] { return classify(img); });

    if (on(c, CC_STAGE_CCM)) {
      if (!ita) {
        note(p, "CCM skipped: no skin-tone estimate");
      } else if (ita->angle <= c.gate_cutoff) {
        cc::CcmOptions opt;
        opt.k = c.ccm_k;
        opt.fixedShift = c.ccm_fixed_shift != 0;
        opt.stratified = c.ccm_stratified != 0;
        opt.vMin = c.skin_v_min;
        opt.seed = c.seed;
        std::string why;
        auto M = timed(CC_T_CCM_FIT, [&] { return cc::estimateAdaptiveCcm(img, ita->fst, bbox, opt, &why); });
        if (!M) {
          note(p, "CCM skipped: " + why);
        } else {
          cv::Mat skin;
          if (c.ccm_l_variance) skin = cc::skinMaskHsv(img, bbox, c.skin_v_min);  // before the CCM lightens the skin
          img = timed(CC_T_CCM_APPLY, [&] { return cc::applyAdaptiveCcm(img, *M); });
          if (c.ccm_l_variance) img = timed(CC_T_L_VARIANCE, [&] { return cc::normaliseSkinLVariance(img, skin); });
          r.ccm_applied = 1;
        }
      }
    }
    if (on(c, CC_STAGE_HIGHLIGHT)) img = timed(CC_T_SPECULAR, [&] { return cc::removeSpecular(img); });
    if (on(c, CC_STAGE_CONTRAST)) img = timed(CC_T_CLAHE, [&] { return cc::claheMedian(img); });

    p.calibrated = img.data == rgb.data ? rgb.clone() : img;  // never hand back the caller's buffer
    if (on(c, CC_STAGE_FINAL)) {
      p.tensor = timed(CC_T_FINAL, [&] {
        return cc::finalNormalise(cc::letterbox(p.calibrated, c.model_size), c.normalise_zscore != 0);
      });
      r.tensor = p.tensor.ptr<float>();
      r.tensor_size = c.model_size;
    }

    r.calibrated_rgb = p.calibrated.data;
    r.width = width;
    r.height = height;
    if (ita) {
      r.fitzpatrick_class = ita->fst;
      r.ita_angle = ita->angle;
    }
    for (int i = 0; i < CC_T_COUNT; ++i) r.total_ms += r.stage_ms[i];
    r.notes = p.notes.c_str();
    *out = r;
    return 0;
  } catch (const std::exception& e) {
    p.error = e.what();
    return -3;
  }
}

}  // extern "C"
