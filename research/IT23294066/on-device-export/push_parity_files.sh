#!/bin/sh
# Copies the model and the golden files into the debug app's private folder, so that
# mobile/integration_test/segmentation_parity_test.dart can use them. The golden files contain DFUC2022 photos (research
# use only): never put them in the app or the repo.
#
#   push_parity_files.sh <model.tflite> <golden_vectors_dir> [adb serial]
# The app has to be installed as a debug build already. The test installs it and then waits for these files.
set -eu
MODEL="$1"
GOLDEN="$2"
SERIAL="${3:-emulator-5554}"
ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
PKG=com.example.melanin_wound_cdss
STAGE=/data/local/tmp/wound_parity

"$ADB" -s "$SERIAL" shell "rm -rf $STAGE && mkdir -p $STAGE"
"$ADB" -s "$SERIAL" push "$MODEL" "$STAGE/model.tflite" >/dev/null
for f in "$GOLDEN"/*.jpg "$GOLDEN"/*.union.u8 "$GOLDEN"/*.rgb.u8 "$GOLDEN"/manifest.json; do
  "$ADB" -s "$SERIAL" push "$f" "$STAGE/" >/dev/null
done
"$ADB" -s "$SERIAL" shell "run-as $PKG sh -c 'rm -rf files/wound_parity && mkdir -p files/wound_parity && cp $STAGE/* files/wound_parity/' && rm -rf $STAGE"
"$ADB" -s "$SERIAL" shell "run-as $PKG ls files/wound_parity" | wc -l | xargs echo "files copied into the app:"
