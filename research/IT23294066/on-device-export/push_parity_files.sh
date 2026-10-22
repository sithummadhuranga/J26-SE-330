#!/bin/sh
# Copies the model and every file of a folder into the debug app's private folder, so the integration tests in
# mobile/integration_test can use them. The files contain DFUC2022 photos (research use only): never put them in the app
# or the repo.
#
#   push_parity_files.sh <model.tflite> <folder with the test files> [adb serial] [folder name inside the app]
# The folder name defaults to wound_parity; the accuracy test uses wound_accuracy.
# The app has to be installed as a debug build already. The test installs it and then waits for these files.
set -eu
MODEL="$1"
FILES="$2"
SERIAL="${3:-emulator-5554}"
NAME="${4:-wound_parity}"
ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
PKG=com.example.melanin_wound_cdss
STAGE=/data/local/tmp/$NAME

"$ADB" -s "$SERIAL" shell "rm -rf $STAGE && mkdir -p $STAGE"
"$ADB" -s "$SERIAL" push "$FILES/." "$STAGE/" >/dev/null
"$ADB" -s "$SERIAL" push "$MODEL" "$STAGE/model.tflite" >/dev/null
"$ADB" -s "$SERIAL" shell "run-as $PKG sh -c 'rm -rf files/$NAME && mkdir -p files/$NAME && cp $STAGE/* files/$NAME/' && rm -rf $STAGE"
"$ADB" -s "$SERIAL" shell "run-as $PKG ls files/$NAME" | wc -l | xargs echo "files copied into the app:"
