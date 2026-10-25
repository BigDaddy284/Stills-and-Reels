# Stills & Reels user guide

This guide covers everything in Stills & Reels 2.2, from first start to exporting a finished video.

**Contents:** [Getting started](#getting-started) · [The four modes](#the-four-modes) · [Pictures](#pictures) · [Edit picture](#edit-picture) · [Snip](#snip) · [Videos](#videos) · [Video snip](#video-snip-screen-recording) · [Video editor](#video-editor) · [Settings](#settings) · [Your files and privacy](#your-files-and-privacy) · [Keyboard shortcuts](#keyboard-shortcuts) · [Troubleshooting](#troubleshooting)

---

## Getting started

1. Right-click `StillsAndReels.zip` and choose **Extract All…**
2. Double-click **`StillsAndReels.exe`**. There's nothing to install.
3. If Windows shows **"Windows protected your PC"**, click **More info**, then **Run anyway**. This appears because the app isn't code-signed, and Windows only asks once.

Stills & Reels needs Windows 10 or 11. It runs on the .NET Framework 4.8, which is already part of Windows.

Now click a picture or a video in any File Explorer window. It appears in Stills & Reels straight away, and it keeps following as you click other files or move through them with the arrow keys. The status bar shows how many File Explorer windows it is watching.

It follows more than File Explorer. Click a picture on a web page or in another app and Pictures mode picks it up from the screen, exactly as shown there (the app never downloads anything; for the full-size original, right-click the picture in your browser and choose **Copy image**, since copied pictures are picked up at full quality). Files you click on the desktop or in any app's Open/Save dialog, and files you copy with Ctrl+C, are followed too: pictures in Pictures mode, videos in Videos mode and the video editor. Each of these can be switched off in **Settings > Following**. Nothing is picked up from web pages or the clipboard while you are in Edit picture, or while the Video snip window is open.

If you're upgrading from Picture Preview, your settings carry over automatically on first run, and the old app's engine and cache folders are cleaned up. You can delete the old exe once you're happy with the new one.

## The four modes

The bar at the top of the window switches between four modes:

| Mode | What it's for | Shortcut |
|---|---|---|
| **Pictures** | Viewing the picture selected in File Explorer | Alt+1 |
| **Edit** (picture) | Drawing on, cropping, rotating and pasting into a picture | Ctrl+E |
| **Videos** | Playing the video selected in File Explorer | Alt+2 |
| **Edit** (video) | The multitrack video editor | Alt+3 or Ctrl+Shift+E |

Each half also has a **Snip** button: the one beside picture Edit captures part of your screen, and the one beside video Edit records your screen.

You can also open any file directly with **File > Open** (Ctrl+O).

## Pictures

- Zoom with **Ctrl +** and **Ctrl −**, or with the mouse wheel. **Ctrl+0** fits the picture to the window and **Ctrl+1** shows it at actual size.
- Zoom past 600% and a pixel grid appears (you can turn this off in Settings).
- **Ctrl+C** copies the picture, and **Ctrl+R** / **Ctrl+Shift+R** rotate it.
- Photos taken sideways are shown the right way up, using the orientation the camera saved.

## Edit picture

Press **Edit** (Ctrl+E) with a picture open. The sidebar on the left holds every tool:

- **Tools:** Pen, Pencil, Highlighter, Eraser, Crop and Move. Hold **Space** and drag to move around the picture while you draw.
- **Colour:** pick a swatch, or **+** for any colour.
- **Size:** the width of the pen, pencil, highlighter or eraser.
- **Rotate and flip:** rotate left or right, flip horizontally or vertically.
- **History:** Undo (Ctrl+Z), Redo (Ctrl+Y), and Clear all ink.
- **Paste:** see below.
- **Keep a copy:** save your edited picture as a new file. **Ctrl+S** saves a copy and **Ctrl+Shift+S** lets you choose the name and format (PNG, JPG, BMP, GIF or TIFF).

Your original picture is never changed. Edits are only ever saved as new files.

### Pasting as a layer

Like paint.net, a pasted picture floats on top as a layer you can place:

1. Copy a picture in any app (or take a snip), then press **Ctrl+V** or the **Paste** button.
2. Drag the layer to move it, and drag its handles to resize it.
3. Press **Enter** or click outside it to place it, or **Esc** to cancel.

With no picture open, Ctrl+V starts a new picture from the clipboard. **Ctrl+Alt+V** (or **As new**) always pastes as a new picture.

## Snip

Open **Snip** with the button next to picture Edit, or **Ctrl+Shift+N**. The Snip window opens beside the main window.

1. Choose a shape: **Rectangle** or **Freeform**.
2. Choose what to snip from: **Screen** (anything on your monitors) or **Picture** (the picture open in Stills & Reels).
3. Press **New snip**. Only the Snip window steps aside, so the main Stills & Reels window stays on screen and can be snipped too.
4. Drag over the area you want. Esc cancels.

The Snip window comes back with your snip in it, and the snip is already copied to the clipboard. From there you can:

- draw on it with the pen, highlighter and eraser, in any colour and size
- **Copy** it again, or **Save** it as a new file
- **Paste into editor** (Ctrl+E) to drop it into Edit picture as a movable layer

## Videos

Click a video in File Explorer and it plays here (you can turn off auto-play in Settings).

| Key | Action |
|---|---|
| Space or K | Play / pause |
| ← / → | Back / forward 5 seconds |
| J / L | Back / forward 10 seconds |
| ↑ / ↓ | Volume |
| M | Mute |
| F or F11 | Full screen (Esc leaves it) |

Press **Edit** next to Videos and the video you're watching goes straight onto the editor's timeline.

## Video snip (screen recording)

Open **Video snip** with the Snip button next to video Edit, or **Ctrl+Alt+R**.

1. Choose **Area** (a part of the screen you drag out) or **Full screen** (a whole monitor).
2. Pick the frames per second, whether to show the mouse pointer, and a microphone if you want your voice recorded. **PC sound** (whatever plays on your default speakers or headphones: videos, music, games, calls) is recorded by default; the PC sound button turns it off. It can be recorded on its own or mixed with the microphone. Your choices are remembered.
3. Press **Record** (Ctrl+R). For Area, drag over what you want to record. The main Stills & Reels window stays visible so you can record it.
4. After a short countdown, recording starts. A small bar on screen lets you **Pause**, **Resume** and **Stop**. The bar and the area outline are kept out of the recording.

When you stop, "Finishing the recording…" shows for a moment while the recording is encoded, then it opens in the Video snip window. You can **Save as…** an MP4 file, **Add to video editor**, or **Discard** it.

**How recordings are made.** Frames come straight from the graphics card (Windows Desktop Duplication), so playing video doesn't tear, and they are stored losslessly while you record, then encoded once in high quality when you stop. Small text and thin lines stay crisp, and pausing costs no quality. The lossless parts can be large while you record (a full-screen video can take about 1 GB a minute); they're kept in the cache and deleted when the recording is finished, and recording stops by itself, keeping what it has, if the disk gets nearly full. Over Remote Desktop, or for an area that spans two screens, the older capture method is used.

Sound and picture are kept in step: the app times how long after the first frame the sound began and shifts the sound to match, so it runs right up to the last frame. Each recording also writes a short technical log to `%LOCALAPPDATA%\StillsAndReels\recording-log.txt` (what FFmpeg reported and the sound timing). It contains no picture or sound, and you can delete it at any time.

## Video editor

Open it with **Edit** next to Videos, **Alt+3** or **Ctrl+Shift+E**.

**Left panel**
- **Media:** import files (Ctrl+I), or just click videos in File Explorer to add them. Drag from here onto the timeline.
- **Text:** add titles and captions (T).
- **Transitions**, **Filters** and **Effects:** drag them onto clips.

**Centre: preview.** Drag the selected clip to move it, drag a corner to resize it, and drag the round handle to rotate it.

**Right panel: properties** of the selected clip, in tabs: Basic, Adjust, Effects, Audio, Speed and Text.

**Bottom: timeline.**
- Drag clips to move them, including onto a new track. Drag a clip's ends to trim it.
- Drop a clip below the last track to add a new video or audio track.
- Add keyframes (Ctrl+K) to animate a clip's position, size, rotation and opacity over time.
- Change a clip's speed in the Speed tab.
- Ctrl and the mouse wheel zoom the timeline.

**Editing keys:** Space plays, ← / → step one frame (Shift: one second), ↑ / ↓ jump to the previous or next cut, **Ctrl+B** splits, **Del** deletes, **Shift+Del** deletes and closes the gap, **Ctrl+C / X / V** copy, cut and paste, **Ctrl+D** duplicates, and **Ctrl+Z** undoes.

### Saving projects

**File > Save video project** (Ctrl+S) saves a `.srproj` file that remembers where your media is. Recordings, freeze frames and pasted pictures are copied into a `<project name> media` folder next to the project. Older `.ppvproj` projects from Picture Preview still open.

### Exporting

Press **Export** at the top right, or **Ctrl+M**:

- **MP4** (H.264 video with AAC sound), at the project's exact size and frame rate (chosen by default) or from 480p up to 4K at 24 to 60 frames per second, with four quality levels: Smaller file, Standard, High (the default) and Best. Colours are written as standard HD (BT.709) and labelled, so players show what you edited
- **MP3**, the sound only

The first video you put on a new project sets the canvas size and frame rate to match it, so a 4K or 60 fps clip, or a screen recording, keeps its size. Videos are decoded with sharp scaling, and a clip that fills the canvas is copied pixel for pixel.

## Settings

Open **File > Settings** (Ctrl+,):

- Dark mode (the default) or light mode
- Follow the selection in File Explorer
- Show the pixel grid when zoomed in to 600% or more
- Start playing when a video is selected
- Keep videos you view in the video editor's Media
- Always on top
- Show the status bar

## Your files and privacy

- **Originals are never changed.** Edits, snips, recordings and exports are only ever saved as new files.
- **Pictures are read from a temporary copy**, which is deleted as soon as you move to another picture.
- **The media cache:** every video you view (if that setting is on) and every file you import is copied into a private cache folder for the editor. The copies stay until you remove them from Media or close the app, and then they're deleted.
- **Nothing is sent anywhere.** The app has no network code.

### The video engine

The first time you use a video feature, Stills & Reels unpacks its built-in video engine (FFmpeg 7.1) to `%LOCALAPPDATA%\StillsAndReels\Engine`. That's about 85 MB, takes a few seconds and happens only once. The app runs it in the background, so you never run it yourself. It's only given local files (and, when you record, your screen and the microphone you choose), so it never uses the network. To remove it, delete that folder.

## Keyboard shortcuts

**Help > Keyboard shortcuts** shows these in the app too.

| Pictures | |
|---|---|
| Ctrl + / Ctrl − | Zoom in / out |
| Ctrl+0 / Ctrl+1 | Fit to window / actual size |
| Ctrl+E | Edit picture |
| Ctrl+Shift+N | Snip |
| Ctrl+V | Paste as a movable layer |
| Ctrl+Alt+V | Paste as a new picture |
| Ctrl+R / Ctrl+Shift+R | Rotate right / left |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+C | Copy |
| Ctrl+S / Ctrl+Shift+S | Save a copy / save as |
| Space + drag | Move around while editing |

| Videos | |
|---|---|
| Space or K | Play / pause |
| ← / → | Back / forward 5 s |
| J / L | Back / forward 10 s |
| ↑ / ↓ | Volume |
| M | Mute |
| F or F11 / Esc | Full screen / leave full screen |

| Video editor | |
|---|---|
| Space | Play / pause |
| ← / → | One frame (Shift: one second) |
| ↑ / ↓ | Previous / next cut |
| Ctrl+B | Split |
| Del / Shift+Del | Delete / delete and close the gap |
| Ctrl+C / X / V | Copy / cut / paste |
| Ctrl+D | Duplicate |
| Ctrl+K | Keyframe |
| T | Add text |
| Ctrl+I | Import |
| Ctrl+S | Save project |
| Ctrl+M | Export |
| Ctrl + wheel | Zoom the timeline |

| Anywhere | |
|---|---|
| Alt+1 / Alt+2 / Alt+3 | Pictures / Videos / Video editor |
| Ctrl+Shift+E | Edit video |
| Ctrl+Alt+R | Video snip |
| Ctrl+O | Open a file |
| Ctrl+, | Settings |

## Troubleshooting

- **"Windows protected your PC"** when starting the app: click **More info**, then **Run anyway**. The app isn't code-signed, so Windows asks once.
- **Nothing appears when I click a file in File Explorer:** check **Follow the selection in File Explorer** is on in Settings, and look at the status bar to see how many File Explorer windows are being watched.
- **The first video takes a few seconds to open:** that's the video engine unpacking. It only happens once.
- **My recording has no computer sound:** check the **PC sound** button in the Video snip bar is on. PC sound records whatever plays on the default speakers or headphones at the time you press Record.
- **A recording's sound and picture don't match:** the app corrects start-up timing automatically. If a recording still looks off, `%LOCALAPPDATA%\StillsAndReels\recording-log.txt` shows what happened in that recording.
- **A picture clicked on a web page looks small:** pictures from web pages are taken from the screen as shown. Right-click the picture and choose **Copy image** to pick it up at full size.
- **A file won't open:** some formats, such as HEIC photos, aren't supported. The app tells you when a file can't be shown.
