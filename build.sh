#!/bin/bash
# Builds StillsAndReels.exe (.NET Framework 4.8, runs on any Windows 10/11 PC without installing anything)
set -e
cd "$(dirname "$0")"
mkdir -p build
x86_64-w64-mingw32-windres --preprocessor=cpp --preprocessor-arg=-P app.rc -O res -o build/app.res
mcs -sdk:4.8 -target:winexe -platform:anycpu -optimize+ -nowarn:414,169,649 \
  -out:build/StillsAndReels.exe -win32res:build/app.res \
  -resource:engine/ffmpeg.exe.gz,StillsAndReels.ffmpeg.exe.gz \
  -unsafe -r:System.dll -r:System.Drawing.dll -r:System.Runtime.Serialization.dll -r:System.Xml.dll -r:System.Windows.Forms.dll -r:Microsoft.CSharp.dll -r:System.Core.dll \
  src/*.cs
ls -la build/StillsAndReels.exe
