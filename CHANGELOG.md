# Changelog

## 2.2 (current)

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
