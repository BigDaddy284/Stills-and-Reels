#!/bin/bash
# Builds the Windows test kit: one console exe holding the app's sources, the engine tests and the bundled FFmpeg,
# to be run on a real Windows PC (see tests/WinKit.cs). Output: $SR_WORK/winkit/ (WinKit.exe + tests/fixtures).
set -e
cd "$(dirname "$0")/.."
OUT="${SR_WORK:-/tmp/sr-tests}/winkit"
rm -rf "$OUT"; mkdir -p "$OUT/tests/fixtures"
mcs -sdk:4.8 -target:exe -platform:anycpu -optimize+ -unsafe -nowarn:414,169,649 -main:WinKit \
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll -r:Microsoft.CSharp.dll \
  -r:System.Runtime.Serialization.dll -r:System.Xml.dll -r:Accessibility.dll \
  -resource:engine/ffmpeg.exe.gz,StillsAndReels.ffmpeg.exe.gz -resource:app.ico,StillsAndReels.app.ico \
  -out:"$OUT/WinKit.exe" src/*.cs tests/WinKit.cs tests/ComposeTest.cs tests/ProjectTest.cs tests/SoundTest.cs \
  tests/ExportFormatsTest.cs tests/PictureTest.cs tests/Bench.cs
cp tests/fixtures/* "$OUT/tests/fixtures/"
ls -la "$OUT"
