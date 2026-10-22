// Stage functions of the calibration pipeline: direct ports of the Python modules named on each
// declaration. Images are CV_8UC3 RGB (not BGR), like the Python side.
#pragma once

#include <opencv2/core.hpp>

#include <optional>
#include <string>

namespace cc {

// preprocess.py -------------------------------------------------------------------------------
cv::Mat rgbToLab8(const cv::Mat& rgb);   // OpenCV-packed LAB (L 0..255), as cv2.cvtColor
cv::Mat processRawInput(const cv::Mat& rgb, double lowPct = 0.5, double highPct = 99.5, bool denoise = true);

struct Letterbox {
  double scale;
  int padLeft, padTop, origH, origW, newH, newW;
};
cv::Mat letterbox(const cv::Mat& rgb, int size, Letterbox* info = nullptr);
cv::Mat finalNormalise(const cv::Mat& rgb, bool zscore);  // CV_32FC3

// illumination.py -----------------------------------------------------------------------------
cv::Mat grayWorld(const cv::Mat& rgb);
cv::Mat singleScaleRetinexL(const cv::Mat& rgb, double sigma = 80.0, int workSize = 512);
cv::Mat multiScaleRetinexL(const cv::Mat& rgb, int workSize = 512);

// ita.py --------------------------------------------------------------------------------------
cv::Mat skinMaskHsv(const cv::Mat& rgb, const cv::Rect* wound, double vMin);  // CV_8U, 255 = skin
struct Ita {
  double angle;
  double meanL, meanB;
  int skinPixels;
  int fst;  // 1..6 = I..VI
};
std::optional<Ita> classifyImage(const cv::Mat& rgb, const cv::Rect* wound, double vMin,
                                 std::string* why, int minSkinPixels = 100);
int fitzpatrickFromIta(double ita);

// ccm.py --------------------------------------------------------------------------------------
struct CcmOptions {
  int k = 6;
  bool fixedShift = false;
  bool stratified = true;
  double vMin = 20.0;
  uint64_t seed = 0;
};
// 3x3 CV_64F matrix M with row-vector convention (lab * M), or nullopt if it cannot be fitted.
std::optional<cv::Mat> estimateAdaptiveCcm(const cv::Mat& rgb, int fst, const cv::Rect* wound,
                                           const CcmOptions& opt, std::string* why);
cv::Mat applyAdaptiveCcm(const cv::Mat& rgb, const cv::Mat& M);
cv::Mat normaliseSkinLVariance(const cv::Mat& rgb, const cv::Mat& skinMask);

// enhance.py ----------------------------------------------------------------------------------
cv::Mat removeSpecular(const cv::Mat& rgb);
cv::Mat claheMedian(const cv::Mat& rgb);

}  // namespace cc
