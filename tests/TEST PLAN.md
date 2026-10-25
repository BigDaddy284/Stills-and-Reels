# Stills & Reels: test plan (must all pass before a build is handed over)

Automated, run on Linux (Mono, Xvfb, system FFmpeg). How to build and run each one is in BUILD NOTES.txt.

| # | Test | What it proves | Pass when |
|---|------|----------------|-----------|
| 1 | Build (`build.sh`) | The app compiles into one exe with the video engine inside | No errors |
| 2 | `logic.cs` | Pictures load, edit, save as new files, originals untouched | ALL PASSED |
| 3 | `pickup.cs` | Click pick-up helpers | ALL PASSED |
| 4 | `CloseTest.cs OpenSnip OpenVideoSnip` | The app closes cleanly with both Snip windows open | errors=0 |
| 5 | `HideTest.cs picture` / `video` | New snip hides only the Snip window | PASS (both) |
| 6 | `FinishTest.cs` | Lossless recording parts are joined and encoded into an MP4 with progress | MP4 made, parts deleted |
| 7 | `ExportTest.cs` | The editor's export keeps quality | PSNR against the source of at least 38 dB |
| 8 | `AvSyncTest.cs pc` | Recording with PC sound: live screen capture, plus PC sound streamed by the app's own code. Covers 2 recording parts (pause/resume) and the finished MP4. On screen, picture and sound start, stop and start again together | Every check passes: no stall (no gap over 2.5 frames), no dropped frames (at least 95%), every start seen in picture and sound, sync between 40 ms early and 90 ms late |
| 9 | `AvSyncTest.cs pc+mic` | The same with a microphone mixed in | As 8 |
| 10 | `AvSyncTest.cs none` | Recording with no sound still works | As 8 (picture checks) |
| 11 | Old-bug check | Test 8, run against the previous build's recording arguments, must fail. This shows the test catches the frozen start, the sound running ahead, and the sound ending before the picture | FAILs reported |

| 13 | `AvSyncTest.cs pc+slow` | A slow screen-capture start: an extra input opens 0.8 s late between the screen and the sound pipe, so the sound's clock starts 0.8 s after the picture's (what Boss's PC showed). The recorder must measure the lead (time from FFmpeg's "Input #0" line, printed right after the first frame is grabbed, to the sound pipe connecting) and shift the sound by it | As 8, with the finished MP4 within ±45 ms and the sound lasting to the end |
| 12 | Start-up order (inside 8 and 9) | The screen input is opened before the sound pipe and analysed for one frame only, so the picture's clock is fixed before any sound is stamped; a 40 ms start offset (SoundPump.StartOffset) centres the result. With the inputs the other way round the test showed the sound 0.2 s ahead | As 8, with the finished MP4 within ±45 ms |

Tests 8–10 also check that the sound lasts to within 0.1 s of the last frame, in each part and in the finished MP4.

Tests 8–10 are each run 3 times. They all must pass every time.

What Linux can't cover: gdigrab itself, Windows loopback capture of real PC sound (WASAPI) and dshow microphones. Those are in "Windows checks.md".
