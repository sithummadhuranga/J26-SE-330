#!/bin/sh
# Cross-compile the C++ port for Android arm64 with the NDK installed through Android Studio's SDK
# Manager, then (optionally) run the checks on the emulator or a phone over adb.
#   ./build_android.sh            build only
#   ./build_android.sh run        build, push fixtures, run api_test, parity_test and bench on the device
#   ./build_android.sh sanitize   same checks (no bench) with AddressSanitizer + UBSan
# OpenCV: the official Android SDK 5.0.0 (same major version as the Python cv2), unzipped to $OPENCV_ANDROID.
set -e
cd "$(dirname "$0")"
MODE=${1:-build}
SDK=${ANDROID_HOME:-$HOME/Library/Android/sdk}
NDK=${NDK:-$(ls -d "$SDK"/ndk/* | sort | tail -1)}
CMAKE_BIN=${CMAKE_BIN:-$(ls -d "$SDK"/cmake/* | sort | tail -1)/bin}
OPENCV_ANDROID=${OPENCV_ANDROID:-$HOME/Library/Android/opencv-5.0.0}
PY=${PY:-../.venv/bin/python}
TOOLS=$NDK/toolchains/llvm/prebuilt/darwin-x86_64
OUT=build/android-arm64
SAN=""
if [ "$MODE" = sanitize ]; then OUT=build/android-arm64-san; SAN="-DCC_SANITIZE=${SANITIZE:-address,undefined}"; fi
# Is a connected device an emulator? (it then needs tools/hide_sve.c)
ADB="$SDK/platform-tools/adb"
EMU=0
if [ "$("$ADB" shell getprop ro.kernel.qemu 2>/dev/null | tr -d '\r')" = 1 ] || [ "$("$ADB" shell getprop ro.boot.qemu 2>/dev/null | tr -d '\r')" = 1 ]; then EMU=1; fi
[ "$EMU" = 1 ] && [ "$MODE" != build ] && SAN="$SAN -DCC_HIDE_SVE=ON"

"$CMAKE_BIN/cmake" -S . -B "$OUT" -G Ninja -DCMAKE_MAKE_PROGRAM="$CMAKE_BIN/ninja" \
  -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 -DANDROID_STL=c++_static \
  -DCMAKE_BUILD_TYPE=Release -DOpenCV_DIR="$OPENCV_ANDROID/sdk/native/jni/abi-arm64-v8a" $SAN
"$CMAKE_BIN/cmake" --build "$OUT"

if [ "$MODE" = run ] || [ "$MODE" = sanitize ]; then
  DEV=/data/local/tmp/colorimetric
  rm -rf build/device && mkdir -p build/device/photos
  "$PY" ../scripts/make_native_fixtures.py build/device "$DEV"
  cp ../tests/golden/photos/*.png build/device/photos/
  "$ADB" shell "rm -rf $DEV && mkdir -p $DEV"
  "$ADB" push -q build/device/. "$DEV/"
  "$ADB" push -q "$OUT/parity_test" "$OUT/api_test" "$OUT/bench" "$OUT/libcolorimetric.so" "$DEV/"
  PRE=""
  if [ "$MODE" = sanitize ]; then
    RT=$(ls -d "$TOOLS"/lib/clang/*/lib/linux | tail -1)
    "$ADB" push -q "$RT/libclang_rt.asan-aarch64-android.so" "$DEV/"
    [ -f "$RT/libclang_rt.ubsan_standalone-aarch64-android.so" ] && "$ADB" push -q "$RT/libclang_rt.ubsan_standalone-aarch64-android.so" "$DEV/"
    PRE="$DEV/libclang_rt.asan-aarch64-android.so"
  fi
  if [ "$EMU" = 1 ]; then echo "emulator detected: SVE hidden, and latency below is NOT phone latency"; fi
  RUN="cd $DEV && ASAN_OPTIONS=detect_leaks=0:abort_on_error=0 UBSAN_OPTIONS=print_stacktrace=1:halt_on_error=1 ${PRE:+LD_PRELOAD=$PRE} LD_LIBRARY_PATH=."
  "$ADB" shell "$RUN ./api_test"
  "$ADB" shell "$RUN ./parity_test $DEV"
  [ "$MODE" = run ] && "$ADB" shell "$RUN ./bench $DEV/photos/p04_dark_skin.png ${RUNS:-20}"
fi
exit 0
