#!/bin/sh
# Copies the wound model and some photos into the measurement demo app on a phone, using adb push into the app's external
# files folder (this also works for release builds). The photos stay inside the app, not in the phone's gallery, so
# they are not backed up anywhere; they disappear when the app is uninstalled. DFUC2022 photos are for research use only.
#
#   push_demo_files.sh <model.tflite> <folder with photos> [adb serial]
# Start the demo app once first, so that Android has created its folder, then tap "Check again" in the app.
set -eu
MODEL="$1"
PHOTOS="$2"
SERIAL="${3:-}"
ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
PKG=com.example.melanin_wound_cdss
DEST=/sdcard/Android/data/$PKG/files
if [ -n "$SERIAL" ]; then set -- -s "$SERIAL"; else set --; fi

"$ADB" "$@" shell "mkdir -p $DEST/models $DEST/demo_photos"
"$ADB" "$@" push "$MODEL" "$DEST/models/wound_seg.tflite" >/dev/null
for f in "$PHOTOS"/*; do
  case "$f" in *.jpg|*.jpeg|*.JPG|*.JPEG|*.png|*.PNG) "$ADB" "$@" push "$f" "$DEST/demo_photos/" >/dev/null ;; esac
done
echo "model: $("$ADB" "$@" shell "ls -l $DEST/models/wound_seg.tflite" | awk '{print $5}') bytes; photos in the app: $("$ADB" "$@" shell "ls $DEST/demo_photos" | wc -l | tr -d ' ')"
