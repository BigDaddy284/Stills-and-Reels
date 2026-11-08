# Stills & Reels: test plan (must all pass before a build is handed over)

Automated, run on Linux (Mono, Xvfb, system FFmpeg). How to build and run each one is in BUILD NOTES.txt.

| # | Test | What it proves | Pass when |
|---|------|----------------|-----------|
| 1 | Build (`build.sh`) | The app compiles into one exe with the video engine inside | No errors |
| 2 | `logic.cs` | Pictures load, edit, save as new files, originals untouched | ALL PASSED |
| 3 | `pickup.cs` | Click pick-up helpers | ALL PASSED |
| 4 | `CloseTest.cs OpenSnip OpenVideoSnip` | Closing the main window with both Snip windows open hides it and leaves them running; closing them ends the app cleanly | closed=True errors=0 |
| 5 | `HideTest.cs picture` / `video` | New snip hides only the Snip window | PASS (both) |
| 6 | `FinishTest.cs` | Lossless recording parts are joined and encoded into an MP4 with progress | MP4 made, parts deleted |
| 7 | `ExportTest.cs` | The editor's export keeps quality | PSNR against the source of at least 38 dB |
| 8 | `AvSyncTest.cs pc` | Recording with PC sound: live screen capture, plus PC sound streamed by the app's own code. Covers 2 recording parts (pause/resume) and the finished MP4. On screen, picture and sound start, stop and start again together | Every check passes: no stall (no gap over 2.5 frames), no dropped frames (at least 95%), every start seen in picture and sound, sync between 40 ms early and 90 ms late |
| 9 | `AvSyncTest.cs pc+mic` | The same with a microphone mixed in | As 8 |
| 10 | `AvSyncTest.cs none` | Recording with no sound still works | As 8 (picture checks) |
| 11 | Old-bug check | Test 8, run against the previous build's recording arguments, must fail. This shows the test catches the frozen start, the sound running ahead, and the sound ending before the picture | FAILs reported |

| 13 | `AvSyncTest.cs pc+slow` | A slow screen-capture start: an extra input opens 0.8 s late between the screen and the sound pipe, so the sound's clock starts 0.8 s after the picture's (what Boss's PC showed). The recorder must measure the lead (time from FFmpeg's "Input #0" line, printed right after the first frame is grabbed, to the sound pipe connecting) and shift the sound by it | As 8, with the finished MP4 within ±45 ms and the sound lasting to the end |
| 12 | Start-up order (inside 8 and 9) | The screen input is opened before the sound pipe and analysed for one frame only, so the picture's clock is fixed before any sound is stamped; a 40 ms start offset (SoundPump.StartOffset) centres the result. With the inputs the other way round the test showed the sound 0.2 s ahead | As 8, with the finished MP4 within ±45 ms |

| 15 | `ScrubTest.cs scrub` | Dragging the playhead shows the frame under it: StillFrames decodes a run of frames ahead of the pointer and caches them | ALL PASSED: all 45 consecutive frames delivered and distinct, a mid-drag frame costs less than half a cold grab, 45 cached frames back cost less than one grab, the sharp full-size frame and a time past the file's end both give a picture |
| 16 | `ScrubTest.cs play` (Xvfb) | The playhead moves while the preview plays, pausing stops it, and 30 live Scrub calls from the UI thread return at once | ALL PASSED: the playhead advances in most 100 ms samples and passes 1.5 s in 2.5 s, 30 Scrub calls take under 500 ms |
| 17 | `ScrubTest.cs coarse` (a 15-minute testsrc2 video, keyframes 2 s apart) | Dragging over a zoomed-out timeline shows keyframes fetched in batches, instead of an exact decode per pixel | ALL PASSED: every step gets a frame, about one distinct frame per keyframe passed, a step costs under 60% of an exact frame (measured 5 ms against 105 ms), dragging back is cached, and the exact frame replaces the keyframe once the drag ends |
| 14 | `SnipWindowsTest.cs windows`, `start pic`, `start vid`, `keep`, `hide`, `timer`, `args` (Xvfb with openbox running, for minimise and maximise) | The Snip and Video snip windows are unowned taskbar windows with minimise and maximise, and minimising or maximising one leaves the others alone; Pic Snip / Vid Snip start with no main window shown and the snip window on screen, and closing the snip window closes the app unless the main window was opened (Paste into editor); closing the main window with snip windows open hides it and leaves them running, and the app ends when the last one closes; the snip timer waits the full delay before the selection appears, keeps the main window visible, is remembered, and Esc, clicking the countdown and closing the window cancel it; `--picsnip` / `--vidsnip` are recognised and passed to an open copy | ALL PASSED for each. About 1 run in 8, openbox maps a window that asked to start minimised as a normal one; the test prints a NOTE and checks the app's own state instead |

Tests 8–10 also check that the sound lasts to within 0.1 s of the last frame, in each part and in the finished MP4.

Tests 8–10 are each run 3 times. They all must pass every time.

What Linux can't cover for 2.4: the WM_TIMER starvation itself (the playhead that stood still is a Windows message-queue effect that Mono's loop doesn't reproduce, so test 16 proves the new path works but not that the old one failed). Those are in "Windows checks.md".

What Linux can't cover for 2.3: the taskbar jump list itself (Windows shell COM), real taskbar entries and grouping, Opacity hiding the Snip window during the timer (Mono under Xvfb has no compositor), and focus handover from a second copy. Those are in "Windows checks.md".

What Linux can't cover: gdigrab itself, Windows loopback capture of real PC sound (WASAPI) and dshow microphones. Those are in "Windows checks.md".
