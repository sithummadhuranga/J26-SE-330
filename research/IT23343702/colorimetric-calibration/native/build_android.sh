#!/bin/sh
# Cross-compile the C++ port for Android arm64 with the NDK installed through Android Studio's SDK
# Manager, then (optionally) run the parity test and bench on the emulator or a phone over adb.
#   ./build_android.sh           build only
#   ./build_android.sh run       build, push fixtures, run parity_test and bench on the connected device
# OpenCV: the official Android SDK 5.0.0 (same major version as the Python cv2), unzipped to $OPENCV_ANDROID.
set -e
cd "$(dirname "$0")"
SDK=${ANDROID_HOME:-$HOME/Library/Android/sdk}
NDK=${NDK:-$(ls -d "$SDK"/ndk/* | sort | tail -1)}
CMAKE_BIN=${CMAKE_BIN:-$(ls -d "$SDK"/cmake/* | sort | tail -1)/bin}
OPENCV_ANDROID=${OPENCV_ANDROID:-$HOME/Library/Android/opencv-5.0.0}
PY=${PY:-../.venv/bin/python}
OUT=build/android-arm64

"$CMAKE_BIN/cmake" -S . -B "$OUT" -G Ninja -DCMAKE_MAKE_PROGRAM="$CMAKE_BIN/ninja" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 -DANDROID_STL=c++_static \
  -DCMAKE_BUILD_TYPE=Release -DOpenCV_DIR="$OPENCV_ANDROID/sdk/native/jni/abi-arm64-v8a"
"$CMAKE_BIN/cmake" --build "$OUT"

if [ "$1" = "run" ]; then
  ADB="$SDK/platform-tools/adb"
  DEV=/data/local/tmp/colorimetric
  rm -rf build/device && mkdir -p build/device/photos
  "$PY" ../scripts/make_native_fixtures.py build/device "$DEV"
  cp ../tests/golden/photos/*.png build/device/photos/
  "$ADB" shell "rm -rf $DEV && mkdir -p $DEV"
  "$ADB" push -q build/device/. "$DEV/"
  "$ADB" push -q "$OUT/parity_test" "$OUT/bench" "$OUT/libcolorimetric.so" "$DEV/"
  # The emulator on an Apple M4 advertises SVE2/SME it cannot run; hide them there (tools/hide_sve.c).
  PRE=""
  if [ "$("$ADB" shell getprop ro.kernel.qemu | tr -d '\r')" = 1 ] || [ "$("$ADB" shell getprop ro.boot.qemu | tr -d '\r')" = 1 ]; then
    "$NDK/toolchains/llvm/prebuilt/darwin-x86_64/bin/aarch64-linux-android26-clang" -shared -fPIC -O2 -o build/libhide_sve.so tools/hide_sve.c -ldl
    "$ADB" push -q build/libhide_sve.so "$DEV/"
    PRE="LD_PRELOAD=$DEV/libhide_sve.so"
    echo "emulator detected: latency below is NOT phone latency"
  fi
  "$ADB" shell "cd $DEV && $PRE LD_LIBRARY_PATH=. ./parity_test $DEV"
  "$ADB" shell "cd $DEV && $PRE LD_LIBRARY_PATH=. ./bench $DEV/photos/p04_dark_skin.png ${RUNS:-20}"
fi
