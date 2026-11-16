# Changelog

## 3.0 (current)

The biggest release so far: both editors were rebuilt for speed and given several times as many tools. Projects saved by 2.6 open unchanged, and originals are still never modified.

### Video editor: speed

- **New renderer.** Frames are now composed on raw pixels across every processor core instead of through GDI+ on one. Exporting the 20-second 1080p test clip takes 13.9 s instead of 35.5 s on the build machine, and the preview has more headroom for layers and effects.
- **Faster effects.** Blur is about 7 times faster; rotation and sharp scaling about twice as fast.
- **Graphics-card encoding.** Export can use the PC's NVIDIA, Intel or AMD encoder for H.264 and HEVC, several times faster than the processor for a somewhat bigger file. The app checks which one really works on the PC by encoding a few frames, and if the card's encoder fails mid-export it starts again on the processor without asking.

### Video editor: picture

- **Blend modes** for any clip: Screen, Multiply, Overlay, Soft light, Hard light, Add, Darken, Lighten and Difference.
- **Masks:** Oval, Box and Split, with a soft edge, or turned inside out to show the outside instead.
- **Frame:** rounded corners, a border in any colour, and a drop shadow.
- **Fit, Fill and Blurred background** for clips that don't match the canvas shape (a phone video on a wide canvas gets a blurred copy of itself behind it), and **Crop**.
- **Transitions: 13 to 30.** New: Blur, Soft wipe left and right, Diagonal wipe, Cover (four directions), Diamond, Clock, Doors, Shutters, Blinds, Zoom out, Spin, Pixelate and Glitch.
- **Filters: 11 to 23.** New: Teal & orange, Golden hour, Sunset, Arctic, Forest, Rose, Pastel, Matte, Bleach, Punch, Technicolor and Night, each with a strength slider.
- **Effects:** Glow, Pixelate, Colour fringes and Mirror join Blur, Sharpen, Vignette, Film grain, Chroma key and Stabilise. Adjust gains **Highlights** and **Shadows**.
- **Movement** that needs no keyframes: Slow zoom in and out, Pan (four directions), Float, Swing, Pulse, Shake and Spin, with a strength slider.
- **Arrive and leave animations** for clips and text: Fade, Slide (four directions), Pop, Grow, Shrink, Spin, Drop, and Typewriter for text, each with its own length.
- **Motion curves.** Each keyframe can be reached Straight, Smooth, with a Slow start or Slow finish, Stay then jump, Overshoot or Bounce (the **Motion** button under the keyframe buttons).

### Video editor: sound

- **Clean up:** Noise reduction (a slider), Voice boost for clearer speech, and Even out the loudness.
- **Tone:** Bass, Treble and Pitch (up or down an octave without changing the speed).
- **Mixing:** "Turn down under other sound" lowers music automatically while speech plays on another clip, with a slider for how far.
- **Volume up to 400%, with volume keyframes.** The keyframe button beside Volume sets a point at the playhead; the line and its points are drawn on the clip's waveform.
- **Fade handles** on the timeline: drag the small handles at the top corners of a selected clip to fade its sound and picture in or out.
- **Level meter** under the preview, showing left and right as the project plays.
- Noise reduction, pitch and even loudness are prepared once in the background and kept; playback never waits for them (the clip plays as it was until the cleaned-up sound is ready), and export always uses the finished version.

### Video editor: timing

- **Speed from 0.1× to 16×** (it stopped at 4×), on a slider that gives slow and fast speeds equal room. Keyframes and volume points stay on the same pictures when the speed changes.
- **Play backwards.** A clip, or just the part used, is written out reversed once (video in pieces small enough for memory, joined last to first; sound turned round to match) and then behaves like any other clip.
- **Trim to the playhead:** **Q** cuts the start of the selected clip to the playhead and **W** the end. **S** splits, as well as Ctrl+B.
- **Ripple trim:** hold Shift while dragging a clip's edge and everything after it moves along.

### Video editor: timeline and projects

- **Markers** (Shift+M, or right-click the ruler): named flags on the ruler that clips and the playhead snap to.
- **Marked range** (I and O): a band on the ruler. Export can write just that part, and "Play the range round and round" in the ruler's menu repeats it during playback.
- **Box select and group move.** Shift or Ctrl and drag across empty timeline to select every clip the box touches; dragging one of several selected clips moves them all.
- **Crash recovery.** While a project has unsaved changes a copy is kept every 45 seconds, together with the recordings, freeze frames and reversed clips that only live in the cache. If the app or the PC stops unexpectedly, the next start offers the project back.
- **Recent projects** in File > Recent video projects and on the empty editor's start panel.

### Export

- **New formats:** HEVC (H.265) in MP4, WebM (VP9 with Opus), animated GIF (with a palette made for the video, at 240 to 720 px wide and 10 to 25 frames a second), and sound only as MP3, M4A (AAC) or WAV.
- **Presets** for common uses: YouTube and TV, phones and social apps, small for email, smallest that still looks good, websites, GIFs and sound only.
- **Export part of the project** when a range is marked.
- The window shows an estimate of the file size, says which encoder was used when it finishes, and remembers your choices for next time.

### Videos mode

- **Frame by frame** with , and . when paused, **Repeat** (R), **Save this frame as a picture** (S), and playback speeds from 0.25× to 4×.

### Pictures and picture Edit

- **Much faster on big photos.** A 24-megapixel photo paints in about 15 ms fitted to the window (was about 700 ms) and about 12 ms at 100% (was about 550 ms). Going into Edit takes about 50 ms (was 150 ms), rotating is twice as fast, and saving a flat copy prepares four times as fast.
- **Undo that doesn't run out of memory.** Lines, shapes and text are kept as objects and cost nothing to undo; pixel changes keep only the 256-pixel tiles they touched. Undo holds 200 steps within a memory budget (a quarter of the PC's memory, between 512 MB and 2 GB).
- **The Edit sidebar has three tabs: Tools, Adjust and Layers.**
- **Tools:** Select and move (V) for anything you've added; selections with Rectangle select (M), Oval select, Lasso (L) and Magic wand (W), where Shift adds and Alt takes away, plus All, None, Invert, Crop to, Soften, Clear, Copy and Cut; Crop (C) with presets (Free, Original, 1:1, 4:3, 3:2, 16:9, 3:4, 9:16); Pen (P), Pencil, Highlighter (H) and Eraser (E); Fill (G) and Colour picker (I); Line, Arrow (A), Rectangle (R), Rounded rectangle and Oval (O) with outline, thickness and optional fill; Text (T) with font, size, bold, italic, colour and an optional background; Numbered step; Hide, which pixelates or blurs something private for good; and Hand. [ and ] change the size of the current tool.
- **Adjust,** with a live preview and Apply or Reset, inside the selection if there is one: Auto fix (Ctrl+Shift+L); Looks (Black and white, Sepia, Vivid, Warm, Cool, Faded, Dramatic, Soft focus, Sharpen, Vignette, Invert colours); Exposure, Brightness, Contrast, Highlights, Shadows, Saturation, Vibrance, Warmth, Tint, Sharpen, Blur and Vignette; Levels (Ctrl+L) and Curves with a histogram; Resize (Ctrl+Alt+I), Canvas size (Ctrl+Alt+C), Straighten to any angle and Trim edges.
- **Layers:** a list with show and hide, thumbnail, name, Opacity and Blend (Normal, Multiply, Screen, Overlay, Soft light, Hard light, Darken, Lighten, Difference, Add); add, duplicate, reorder, delete, Merge down and Flatten. A paste arrives as its own layer.
- **Save with layers** writes a `.srpic` file that keeps every layer, shape and text box editable. Opening one shows it like any picture, going into Edit brings the layers back, and Ctrl+S then saves straight back into it. It is the one file the app ever saves over.
- **A Picture menu** with the adjustments and layer commands, and selection commands in the Edit menu.
- **More formats.** Opens WebP, HEIC and HEIF, AVIF, TGA, PSD, JPEG 2000, DDS, QOI, PCX, EXR, HDR and the PPM family through the built-in engine (local files only), also as media in the video editor. Phone HEIC photos stored as tile grids are put back together the right way up. Saves PNG, JPEG, WebP, BMP, GIF, TIFF and `.srpic`.
- **Save options.** JPEG and WebP ask for a quality (JPEG shows a live guess of the file size; WebP can keep every pixel exact). Photo details (date, camera, place) are left out of saved copies unless you tick **Keep photo details**; when kept, the small preview picture inside the details is wiped so nothing you hid can show through.

### Viewer

- **Step through the folder** with Left and Right (or Page Up and Page Down), Home and End, in File Explorer's name order. The next picture is read ahead, and the status bar shows the place in the folder ("3 of 57").
- **Full screen** (F11), **Slideshow** (F5; Space pauses, Esc stops; 2 to 20 seconds a picture in View > Slideshow speed) and **Picture details** (Ctrl+I): size, megapixels, file size, date, and for camera photos the camera, lens, exposure and place, with a histogram.

### Snip

- **Window** mode (hover to light up a window, click to snip it) and **Full screen** mode (the whole of the screen the pointer is on, straight away), next to Rectangle and Freeform.
- A **magnifier** on rectangle snips shows the exact pixel and its position; the arrow keys move the pointer one pixel (Shift: ten).
- **Annotation tools** on the Snip bar: Select, Arrow, Box, Text, Numbered step and Hide, alongside Pen, Highlighter and Eraser.
- A snip can be saved as WebP, or with its layers so arrows and text stay editable.

### Fixes

- **Export on PCs set to UTF-8.** When Windows' "use Unicode UTF-8" setting is on (or the app is started from a console set to UTF-8), the .NET Framework puts three stray bytes into the encoder's input ahead of the first frame, which shifted every pixel and turned the exported picture into coloured noise. This was true of every earlier version too. The app now measures how many bytes arrive ahead of its own, once, and has the engine skip them.
- The status bar no longer gets switched off for the next start when the app is closed from a snip-only start or while the main window is hidden.

### Known limits in 3.0

- A reversed clip of a video with transparency loses the transparency, and a video with an uneven frame rate is reversed at a steady one.
- Graphics-card encoding, HEIC photos from a phone and the recovery prompt could only be checked on Windows by hand (see the Windows checks that come with the build).
- In picture Edit, selections aren't part of undo, and Straighten turns shapes and text into pixels. Camera raw files, SVG, JPEG XL and animated WebP aren't opened.
- There are still no automatic captions, and Stabilise applies in playback and export only.

## 2.6

- **Graphite theme.** A third theme alongside Dark and Light: soft charcoal greys with a sky-blue accent (the Graphite theme's own colours: bar #1b1c1f, window #2a2b2f, panels #222327, accent #2580ee to #8cc4ff). Choose it in View > Theme, the ⋯ menu or Settings > Appearance; the choice is saved as . Surfaces that were fixed navy colours in the code (timeline, tracks, preview surround, effect tiles, text boxes, media thumbnails, timeline tooltips, the snip bars, countdown and badges, the Windows 11 window border) now come from the theme, so Graphite is grey throughout. In Dark and Light they look exactly as before.
- **New app icon** in the Sky Classic style: a glowing sky-blue frame with a camera and a play mark side by side in the middle on a dark tile. The .ico holds 16, 20, 24, 32, 40, 48, 64 and 256 px images, with a simplified, bolder drawing for 16 to 24 px so it stays sharp in title bars. It is the exe's icon and is also embedded as a resource, and every window uses it: the main window, Snip, Video snip, Settings and Export (Settings and Export used to show the default Windows icon).

## 2.5

- **Snip windows on their own.** Pic Snip and Vid Snip from the taskbar (and `--picsnip` / `--vidsnip`) no longer start the main window: only the snip window appears, with its own taskbar button. The main window opens when you choose **Paste into editor** or **Add to video editor**, and closing the last snip window ends the app. (The main window used to wait minimised in the taskbar.)
- **Windows close separately.** Closing the main window while a snip window is open hides it instead of closing the app, after the usual questions about unsaved pictures and projects, so the snip window carries on; the app ends when the last window closes. **File > Exit** closes everything at once, as does Windows shutting down.

## 2.4

Video editor: playback, scrubbing and speed.

- **The playhead follows playback.** While the preview plays, the timeline marker and the time readout now move with every frame. (They were driven by a timer that Windows never ran while the preview kept repainting, so the marker stood still until you pressed pause.)
- **Scrubbing shows every frame.** Dragging the playhead now shows the frame under it as you go. Each grab decodes a short run of frames ahead of the pointer and keeps them, so the next frames are already there; the frame under the pointer is drawn quickly while you drag and sharply at full size when you let go. Dragging back over the same stretch is instant. The old code started a fresh FFmpeg for every pixel the pointer moved and killed it as soon as the pointer moved on, so the picture rarely changed until you stopped.
- **Scrubbing a zoomed-out timeline.** When the timeline is zoomed so that a pixel is half a second or more of video (a long recording shown in minutes), decoding frames ahead can't keep up with the pointer, and each exact frame means decoding from the previous keyframe. While you drag at that zoom the keyframe at or before the pointer is shown instead, fetched 16 at a time in one FFmpeg run (tens of seconds of video per run) and cached, so the picture keeps up with the pointer; the exact frame is drawn when you let go.
- **The picture keeps up with the mouse.** Windows only repaints a window when its message queue is empty, and a moving mouse keeps the queue full (a 1000 Hz mouse sends a move every millisecond), so while the playhead was dragged the timeline, the time readout and the preview all stood still until the mouse paused, however quickly the frames were decoded. They are now painted straight away on every move, and playback frames likewise.
- **Frame stepping** (, and ., or the arrows under the preview) is quicker: a few frames ahead are decoded with each one.
- **Lighter timeline.** Following the playhead repaints only the strips of the timeline that changed, and clips outside the repainted area are skipped, so a busy timeline no longer slows playback down.
- **Sound ready sooner.** Imported videos (and finished recordings) get their sound extracted on its own thread, in parallel with the timeline thumbnails, so "the sound is still being prepared" shows for a shorter time.
- **Export** composes the next frame while the previous one is being handed to the encoder (two buffers on a writer thread).
- Reviewed the screen recorder's capture pipeline against the alternatives: the lossless capture it uses (x264 RGB, ultrafast, QP 0) measured fastest of every lossless encoder in the bundled FFmpeg at both 1080p and 4K, so it stays as it is. See the 2.4 release notes for the numbers.

## 2.3

- **Snip windows of their own.** The Snip and Video snip windows each have their own taskbar entry and their own minimise and maximise buttons. Minimising the main window leaves them on screen, and minimising either of them leaves the main window alone.
- **Pic Snip and Vid Snip in the taskbar.** Pin the app to the taskbar and right-click its icon: **Pic Snip** and **Vid Snip** open straight into that snip tool. If the app is already open, the snip window opens there instead of in a second copy. Started on its own, the main window waits minimised, and closing the snip window closes the app unless the main window was opened meanwhile (Paste into editor and Add to video editor open it). The same shortcuts work from a command line as `StillsAndReels.exe --picsnip` and `--vidsnip`.
- **Snip timer.** A delay button in the Snip bar offers No delay, 3, 5 or 10 seconds, like Snip & Sketch. New snip then hides the Snip window and counts down in the corner of the screen before the screen is frozen for snipping, so you can open a menu or tooltip first. Esc or clicking the countdown cancels; the countdown never takes the focus and isn't in the snip. The choice is remembered.
- A snip window opened while the main window is minimised appears in the middle of the screen instead of off screen beside it.

## 2.2

- The app is now called **Stills & Reels** (it used to be Picture Preview).
- Your settings carry over from Picture Preview automatically on first run.
- The old Picture Preview video engine and cache folders are cleaned up on first run (as long as the old app isn't open), so you don't keep two copies of the engine.
- Video projects now save as `.srproj`. Existing `.ppvproj` projects still open, and saving one keeps its original name and extension.
- **Fix:** pressing **New snip** now hides only the Snip window. The main Stills & Reels window stays on screen so you can snip or record it, and the Snip window comes back when you finish or cancel. This applies to both the picture Snip and the Video snip.

### Video quality

- **Sharper screen recordings.** Frames are captured losslessly while you record and encoded once, in high quality, when you press Stop ("Finishing the recording… 42%" shows while that runs). Small text and thin lines stay crisp, and pausing no longer costs quality.
- **No tearing.** The screen is captured with Windows Desktop Duplication: whole frames straight from the graphics card, so playing video doesn't tear and far fewer frames are dropped. Over Remote Desktop, or for an area spanning two screens, the older capture method is used.
- **True colours.** Exports and recordings are written as standard HD colour (BT.709) and labelled so, and untagged HD video is read as BT.709, so players no longer shift reds, oranges and greens.
- **Sharper decoding in the editor.** Videos are scaled with Lanczos filtering and full-resolution colour, and a clip that fills the canvas is copied pixel for pixel instead of being resampled.
- **The canvas matches your video.** The first video you put on a new project sets the canvas size and frame rate, so a 4K or 60 fps clip (or a 1366 × 768 screen recording) is no longer squeezed to 1080p 30.
- **Better export choices.** Export offers the project's exact size and frame rate (chosen by default), and the quality choices are now Smaller file, Standard, High (default) and Best. The export test measures over 45 dB PSNR against the source.
- While recording, the lossless parts live in the cache and can be large (a full-screen video can take about 1 GB a minute). They are deleted once the recording is finished, and recording stops by itself, keeping what it has, if the disk gets nearly full.

### Sound recording

- **PC sound.** Video snip records the PC's own sound (videos, music, games, calls) by default; the PC sound button in the Video snip bar turns it off. It works on its own or mixed with a microphone, and records whatever plays on your default speakers or headphones. Your Video snip choices (PC sound, microphone, frame rate, pointer) are remembered.
- **Sound stays in step with the picture.** The app times how long after the first frame the sound actually began and shifts the sound by exactly that, so a slow start-up no longer leaves the sound ahead of the picture or ending before it. Sound now runs right up to the last frame.
- Each recording writes a short technical log to `%LOCALAPPDATA%\StillsAndReels\recording-log.txt` (what FFmpeg reported and the measured sound timing). It contains no picture or sound, and you can delete it at any time.
- Recording changes are covered by a test plan (`tests/TEST PLAN.md`) that records a live screen with PC sound through the app's own code and checks for stalls, dropped frames, sync and sound lasting to the end.

### Following clicks anywhere

- Pictures mode also picks up a picture you click on a web page or in another app. It is copied from the screen exactly as shown there, because the app never downloads anything. For the full-size original, copy the image: copied pictures are picked up at full quality.
- Files you click on the desktop or in an Open/Save dialog of any app are followed like File Explorer clicks, and so are files you copy (Ctrl+C).
- Pictures on web pages are found even when the page hides them from accessibility tools (photo sites, OneDrive), and browsers get a moment to build their accessibility tree. Web pictures are also picked up in Edit picture when there are no unsaved edits.
- Desktop icons are followed even when the Desktop is backed up to OneDrive.
- **Help > Pick-up log** shows what the app saw when a click wasn't picked up. It stays on your PC.
- Turn each of these on or off in Settings > Following. Copied pictures are not picked up while you are in Edit picture, and nothing is picked up while the video Snip window is open. Clicks are only watched, never blocked or changed, and the app doesn't record which keys you press or which pages you visit.

## 2.1

- **Paste into Edit picture** (like paint.net): Ctrl+V or the Paste button in the sidebar. The picture floats as a layer: drag it, drag its handles to resize, press Enter or click outside to place it, or press Esc to cancel. With no picture open, Ctrl+V starts a new picture. Ctrl+Alt+V always pastes as a new picture.
- **Snip** copies every capture to the clipboard straight away, and has a **Paste into editor** button (Ctrl+E) that drops the snip into Edit picture.
- **Video editor:** Edit > Edit video (Ctrl+Shift+E), or the Edit button next to Videos. A video playing in the viewer goes straight onto the timeline.
- **Video snip** (screen recording): Edit > Video snip (Ctrl+Alt+R), or Snip next to the video Edit button.
