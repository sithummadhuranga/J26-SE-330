/* C interface of the calibration module (proposal Table 3), for dart:ffi and the NDK.
 *
 * Mirrors colorimetric.api.CalibrationPipeline: same stages, same stage toggles, same output
 * geometry (calibrated image has exactly the input height and width). No C++ types cross the
 * boundary, and every buffer in cc_result belongs to the pipeline handle: it stays valid until
 * the next cc_process() or cc_destroy() on that handle. A handle is not thread-safe.
 */
#ifndef COLORIMETRIC_H
#define COLORIMETRIC_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define CC_API __declspec(dllexport)
#else
#define CC_API __attribute__((visibility("default")))
#endif

/* Stage bits for cc_config.enabled_stages (PipelineStage in api.py). */
enum {
  CC_STAGE_INPUT = 1u << 0,
  CC_STAGE_ILLUMINATION = 1u << 1,
  CC_STAGE_SKIN_TONE = 1u << 2,
  CC_STAGE_CCM = 1u << 3,
  CC_STAGE_HIGHLIGHT = 1u << 4,
  CC_STAGE_CONTRAST = 1u << 5,
  CC_STAGE_FINAL = 1u << 6,
  CC_STAGE_ALL = 0x7Fu
};

enum { CC_ILLUM_NONE = 0, CC_ILLUM_MSR = 1, CC_ILLUM_SSR = 2, CC_ILLUM_GRAY_WORLD = 3 };

typedef struct {
  uint32_t enabled_stages;   /* CC_STAGE_* bits */
  int32_t illumination;      /* CC_ILLUM_*, used when the illumination stage is on */
  double gate_cutoff;        /* CCM runs when ITA <= this (degrees) */
  double skin_v_min;         /* lower HSV V limit of the skin mask, percent */
  int32_t ccm_fixed_shift;   /* 1 = v1 ColorChecker offset, 0 = relative lift (v2) */
  int32_t ccm_stratified;    /* close a per-FST fraction of the gap */
  int32_t ccm_l_variance;    /* normalise skin L* spread after the CCM */
  int32_t ccm_k;             /* k-means clusters */
  int32_t classify_on_processed; /* 1 = ITA from the processed image, 0 = from the raw one */
  int32_t retinex_work_size; /* long side of the blur copy, 0 = full size */
  int32_t model_size;        /* side of the square model tensor */
  int32_t normalise_zscore;  /* 1 = per-image z-score, 0 = ImageNet mean/std */
  uint32_t seed;             /* k-means seed */
} cc_config;

typedef struct {
  int32_t x, y, w, h;        /* wound box in input pixels, excluded from the skin sample */
} cc_bbox;

/* Timing slots, in pipeline order. cc_timing_name(i) gives the key used by the Python module. */
enum {
  CC_T_ITA, CC_T_RAW_INPUT, CC_T_ILLUMINATION, CC_T_CCM_FIT, CC_T_CCM_APPLY, CC_T_L_VARIANCE,
  CC_T_SPECULAR, CC_T_CLAHE, CC_T_FINAL, CC_T_COUNT
};

typedef struct {
  const uint8_t* calibrated_rgb;  /* width*height*3, same size as the input */
  int32_t width, height;
  const float* tensor;            /* model_size*model_size*3, NULL when the final stage is off */
  int32_t tensor_size;
  int32_t fitzpatrick_class;      /* 0 = not estimated, 1..6 = I..VI */
  double ita_angle;               /* valid when fitzpatrick_class != 0 */
  int32_t ccm_applied;
  double stage_ms[CC_T_COUNT];    /* 0 for stages that did not run */
  double total_ms;
  const char* notes;              /* '\n' separated, may be empty */
} cc_result;

CC_API const char* cc_version(void);
CC_API cc_config cc_default_config(void);
CC_API const char* cc_timing_name(int32_t slot);

/* Returns NULL (and writes to *error if given, which must hold 256 bytes) on a bad config. */
CC_API void* cc_create(const cc_config* config, char* error);
CC_API void cc_destroy(void* pipeline);

/* Return codes of cc_process(). */
enum {
  CC_OK = 0,
  CC_ERR_ARGUMENT = -1,   /* null pointer */
  CC_ERR_SIZE = -2,       /* width/height not positive, above CC_MAX_SIDE, or width*height*3 too large */
  CC_ERR_LENGTH = -3,     /* rgb_len is not width*height*3 */
  CC_ERR_PROCESSING = -4  /* a stage failed; cc_last_error() says why */
};
enum { CC_MAX_SIDE = 32768 };

/* rgb: width*height*3 uint8, row-major, no padding; rgb_len is its length in bytes and must be exactly
 * width*height*3 (checked, so a short buffer is refused instead of read past its end). wound may be NULL.
 * Returns CC_OK or a negative CC_ERR_* code; cc_last_error() then describes it. */
CC_API int32_t cc_process(void* pipeline, const uint8_t* rgb, uint64_t rgb_len, int32_t width, int32_t height,
                          const cc_bbox* wound, cc_result* out);
CC_API const char* cc_last_error(const void* pipeline);

#ifdef __cplusplus
}
#endif
#endif
