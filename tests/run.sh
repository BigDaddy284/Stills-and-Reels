#!/bin/bash
# Builds the app and runs the Linux tests (Mono, Xvfb, openbox, system ffmpeg, Python 3 with Pillow for fixtures).
#   tests/run.sh            quick set: logic, pickup, picture, compositor, project, sound, export, scrub
#   tests/run.sh all        adds the window tests (close, hide, snip windows, theme) and the recording finish test
#   tests/run.sh <name>...  only the named tests (logic pickup picture compose sound export scrub play close hide snip sniptools theme finish bench shots)
#                           bench times the compositor; shots writes screenshots of the editor to $SR_WORK/shots
# The recording sync tests (AvSyncTest) are run by hand: see the comment at the top of tests/AvSyncTest.cs.
set -u
cd "$(dirname "$0")/.."
ROOT="$PWD"
WORK="${SR_WORK:-/tmp/sr-tests}"
mkdir -p "$WORK" /tmp/pp
export SR_EXE="$ROOT/build/StillsAndReels.exe"
REFS="-r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll -r:Microsoft.CSharp.dll"
FAILED=""

./build.sh > "$WORK/build.log" 2>&1 || { cat "$WORK/build.log"; echo "BUILD FAILED"; exit 1; }
grep -E "error|warning CS(0219|0168)" "$WORK/build.log" | head -20
echo "built $(ls -la build/StillsAndReels.exe | awk '{print $5}') bytes"

if ! xdpyinfo -display :99 >/dev/null 2>&1; then (Xvfb :99 -screen 0 1920x1080x24 >/dev/null 2>&1 &); sleep 1; fi
export DISPLAY=:99
pgrep -x openbox >/dev/null || (openbox >/dev/null 2>&1 &)

fixtures() {
  [ -f /tmp/pp/landscape.jpg ] && [ -f /tmp/pp/rotated_exif.jpg ] || python3 - <<'PY'
from PIL import Image, ImageDraw
im = Image.new("RGB", (1600, 900), (40, 90, 160)); d = ImageDraw.Draw(im)
for i in range(0, 1600, 80): d.rectangle([i, 0, i + 40, 900], fill=(60 + i % 150, 120, 90))
im.save("/tmp/pp/landscape.jpg", quality=92)
im2 = Image.new("RGB", (400, 200), (200, 60, 60)); ex = im2.getexif(); ex[0x0112] = 6
im2.save("/tmp/pp/rotated_exif.jpg", exif=ex)
PY
  [ -f "$WORK/src1080.mp4" ] || ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 \
    -f lavfi -i sine=frequency=440:sample_rate=48000 -t 20 -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p \
    -colorspace bt709 -color_primaries bt709 -color_trc bt709 -c:a aac "$WORK/src1080.mp4"
  [ -f "$WORK/src720.mp4" ] || ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=30 \
    -f lavfi -i sine=frequency=660:sample_rate=48000 -t 6 -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p \
    -g 30 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -c:a aac "$WORK/src720.mp4"
}

compile() { # name [extra mcs args]
  local n="$1"; shift
  rm -f "$WORK/$n.exe"
  mcs -sdk:4.8 $REFS "$@" -out:"$WORK/$n.exe" "tests/$n.cs" 2>&1 | grep -E "error"
  [ -f "$WORK/$n.exe" ] || { FAILED="$FAILED $n(compile)"; return 1; }
}
# Tests that use the app's own types are compiled together with its sources.
WITH_SRC="-unsafe -nowarn:414,169,649 -r:System.Runtime.Serialization.dll -r:System.Xml.dll -r:Accessibility.dll src/*.cs"

run() { # label command...
  local label="$1"; shift
  echo "=== $label"
  if "$@" > "$WORK/out.log" 2>&1; then
    if grep -q "FAIL" "$WORK/out.log"; then grep "FAIL" "$WORK/out.log"; FAILED="$FAILED $label"; else tail -1 "$WORK/out.log"; fi
  else
    tail -15 "$WORK/out.log"; FAILED="$FAILED $label"
  fi
}

t_logic()   { compile logic && run logic mono "$WORK/logic.exe"; }
t_pickup()  { compile pickup && run pickup mono "$WORK/pickup.exe" "$SR_EXE"; }
t_picture() { compile PictureTest -main:PictureTest $WITH_SRC && run picture mono "$WORK/PictureTest.exe" "$WORK"; }
t_compose() { [ -f tests/ComposeTest.cs ] || return 0; compile ComposeTest -main:ComposeTest $WITH_SRC && run compose mono "$WORK/ComposeTest.exe"; }
t_sound()   { compile SoundTest -main:SoundTest $WITH_SRC && run sound mono "$WORK/SoundTest.exe" "$WORK"; }
t_export()  {
  compile ExportTest || return
  run export mono "$WORK/ExportTest.exe" "$SR_EXE" "$WORK/src1080.mp4" "$WORK/out.mp4" 1920 1080 30 18
  local psnr; psnr=$(ffmpeg -hide_banner -i "$WORK/out.mp4" -i "$WORK/src1080.mp4" -lavfi psnr -f null - 2>&1 | sed -n 's/.*average:\([0-9.]*\).*/\1/p' | tail -1)
  echo "export PSNR against the source: $psnr dB (2.6 gave 35.0 on this clip; must not be lower than 34.5)"
  awk -v p="$psnr" 'BEGIN{exit !(p+0 >= 34.5)}' || FAILED="$FAILED export-psnr"
}
t_scrub()   { compile ScrubTest && run scrub mono "$WORK/ScrubTest.exe" "$SR_EXE" "$WORK/src720.mp4" scrub; }
t_play()    { compile ScrubTest && run play mono "$WORK/ScrubTest.exe" "$SR_EXE" "$WORK/src720.mp4" play; }
t_close()   { compile CloseTest && run close mono "$WORK/CloseTest.exe" OpenSnip OpenVideoSnip; }
t_hide()    { compile HideTest && run hide-picture mono "$WORK/HideTest.exe" picture && run hide-video mono "$WORK/HideTest.exe" video; }
t_snip()    { compile SnipWindowsTest || return; for m in windows "start pic" "start vid" keep hide timer args; do run "snip $m" mono "$WORK/SnipWindowsTest.exe" $m; done; }
t_sniptools() { compile SnipToolsTest -main:SnipToolsTest $WITH_SRC && run sniptools mono "$WORK/SnipToolsTest.exe"; }
t_theme()   { compile ThemeTest && run theme mono "$WORK/ThemeTest.exe" "$SR_EXE"; }
t_finish()  { compile FinishTest && run finish mono "$WORK/FinishTest.exe" "$SR_EXE"; }
t_bench()   { [ -f tests/Bench.cs ] || return 0; compile Bench -main:Bench $WITH_SRC && mono "$WORK/Bench.exe" "$WORK"; }

t_shots()   { compile UiShot -main:UiShot $WITH_SRC -resource:engine/ffmpeg.exe.gz,StillsAndReels.ffmpeg.exe.gz -resource:app.ico,StillsAndReels.app.ico && { timeout 120 mono "$WORK/UiShot.exe" "$WORK" > "$WORK/shots.log" 2>&1; pkill -f "$WORK/engine" 2>/dev/null; echo "$(grep -c wrote "$WORK/shots.log") screenshots in $WORK/shots"; } 2>/dev/null; }

fixtures
SET="${*:-logic pickup picture compose sound export scrub}"
[ "$SET" = "all" ] && SET="logic pickup picture compose sound export scrub play close hide snip sniptools theme finish"
for t in $SET; do "t_$t"; done
if [ -n "$FAILED" ]; then echo "FAILED:$FAILED"; exit 1; fi
echo "ALL TESTS PASSED ($SET)"
