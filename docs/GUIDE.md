# Stills & Reels user guide

This guide covers everything in Stills & Reels 3.0, from first start to exporting a finished video.

**Contents:** [Getting started](#getting-started) · [The four modes](#the-four-modes) · [Pictures](#pictures) · [Edit picture](#edit-picture) · [Snip](#snip) · [Videos](#videos) · [Video snip](#video-snip-screen-recording) · [Video editor](#video-editor) · [Settings](#settings) · [Your files and privacy](#your-files-and-privacy) · [Keyboard shortcuts](#keyboard-shortcuts) · [Taskbar shortcuts](#taskbar-shortcuts-pic-snip-and-vid-snip) · [Troubleshooting](#troubleshooting)

---

## Getting started

1. Right-click `StillsAndReels.zip` and choose **Extract All…**
2. Double-click **`StillsAndReels.exe`**. There's nothing to install.
3. If Windows shows **"Windows protected your PC"**, click **More info**, then **Run anyway**. This appears because the app isn't code-signed, and Windows only asks once.

Stills & Reels needs Windows 10 or 11. It runs on the .NET Framework 4.8, which is already part of Windows.

Now click a picture or a video in any File Explorer window. It appears in Stills & Reels straight away, and it keeps following as you click other files or move through them with the arrow keys. The status bar shows how many File Explorer windows it is watching.

It follows more than File Explorer. Click a picture on a web page or in another app and Pictures mode picks it up from the screen, exactly as shown there (the app never downloads anything; for the full-size original, right-click the picture in your browser and choose **Copy image**, since copied pictures are picked up at full quality). Files you click on the desktop or in any app's Open/Save dialog, and files you copy with Ctrl+C, are followed too: pictures in Pictures mode, videos in Videos mode and the video editor. Each of these can be switched off in **Settings > Following**. Clicked web pictures are also picked up in Edit picture when there are no unsaved edits, but copied pictures aren't (a copy there is for pasting). Nothing is picked up while the Video snip window is open. Desktop icons are followed even when your Desktop is backed up to OneDrive. If a click isn't picked up, **Help > Pick-up log** shows what the app saw.

If you're upgrading from Picture Preview, your settings carry over automatically on first run, and the old app's engine and cache folders are cleaned up. You can delete the old exe once you're happy with the new one.

## The four modes

The bar at the top of the window switches between four modes:

| Mode | What it's for | Shortcut |
|---|---|---|
| **Pictures** | Viewing the picture selected in File Explorer | Alt+1 |
| **Edit** (picture) | Layers, selections, drawing, shapes, text and adjustments | Ctrl+E |
| **Videos** | Playing the video selected in File Explorer | Alt+2 |
| **Edit** (video) | The multitrack video editor | Alt+3 or Ctrl+Shift+E |

Each half also has a **Snip** button: the one beside picture Edit captures part of your screen, and the one beside video Edit records your screen.

You can also open any file directly with **File > Open** (Ctrl+O).

## Pictures

- **Left** and **Right** (or Page Up and Page Down) show the previous and next picture in the folder, in File Explorer's name order. **Home** and **End** jump to the first and last. The next picture is read ahead, so the step is instant, and the status bar shows where you are ("3 of 57").
- Zoom with **Ctrl +** and **Ctrl −**, or with the mouse wheel. **Ctrl+0** fits the picture to the window and **Ctrl+1** shows it at actual size.
- Zoom past 600% and a pixel grid appears (you can turn this off in Settings).
- **F11** shows the picture full screen (Esc leaves it). **F5** starts a slideshow of the folder: Space pauses, Esc stops, and **View > Slideshow speed** sets 2 to 20 seconds a picture.
- **Ctrl+I** opens **Picture details**: name, folder, size in pixels and megapixels, file size and date, and for camera photos the camera, lens, date taken, exposure and place, with a histogram.
- **Ctrl+C** copies the picture, and **Ctrl+R** / **Ctrl+Shift+R** rotate it.
- Photos taken sideways are shown the right way up, using the orientation the camera saved.

**Formats.** As well as PNG, JPEG, GIF, BMP and TIFF, the app opens WebP, HEIC and HEIF (phone photos), AVIF, TGA, PSD, JPEG 2000, DDS, QOI, PCX, EXR, HDR and the PPM family, and its own layered `.srpic` pictures. Camera raw files, SVG, JPEG XL and animated WebP aren't opened.

## Edit picture

Press **Edit** (Ctrl+E) with a picture open. The sidebar on the left has three tabs: **Tools**, **Adjust** and **Layers**. Your original picture is never changed: edits are only ever saved as new files.

### Tools

| Tool | Key | What it does |
|---|---|---|
| Select and move | V | Click a shape, text box, numbered step or pasted picture to move it, resize it, restyle it, duplicate it (Ctrl+D), bring it to the front, send it to the back or delete it |
| Rectangle select, Oval select | M | Drag out an area. Shift adds to the selection and Alt takes away |
| Lasso | L | Draw round an area freehand |
| Magic wand | W | Click to select an area of similar colour. **Tolerance** sets how similar; **Touching areas only** stops it jumping to other parts of the picture |
| Crop | C | Drag the box, then Enter. Presets: Free, Original, 1:1, 4:3, 3:2, 16:9, 3:4, 9:16 |
| Pen, Pencil, Highlighter | P, H | Draw in any colour and size |
| Eraser | E | Removes whole strokes, or rubs out pixels |
| Fill, Colour picker | G, I | Fill an area with the colour; pick a colour from the picture |
| Line, Arrow, Rectangle, Rounded rectangle, Oval | A, R, O | Shapes with an outline colour, a thickness and an optional fill |
| Text | T | Click and type. Font, size, bold, italic, colour and an optional background behind the words. Click the text again to change it |
| Numbered step | | Each click places the next number: 1, 2, 3… |
| Hide | | Drag over something private to pixelate or blur it for good |
| Hand | Space + drag | Move around the picture |

**[** and **]** make the current tool smaller and bigger.

With a selection you can **Copy**, **Cut**, **Clear**, **Crop to** it, **Soften** its edge or **Invert** it, and adjustments apply only inside it. Ctrl+A selects everything, Ctrl+Shift+A selects nothing and Ctrl+Shift+I inverts.

Shapes, text and numbered steps stay editable until you flatten the picture, so you can move an arrow or fix a typo later. **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y) go back up to 200 steps.

### Adjust

Every slider shows its result live; press **Apply** to keep it or **Reset** to go back. If there's a selection, only that part changes.

- **Auto fix** (Ctrl+Shift+L) balances light and colour in one click.
- **Looks:** Black and white, Sepia, Vivid, Warm, Cool, Faded, Dramatic, Soft focus, Sharpen, Vignette and Invert colours.
- **Light:** Exposure, Brightness, Contrast, Highlights, Shadows. **Colour:** Saturation, Vibrance, Warmth, Tint. **Detail:** Sharpen, Blur, Vignette.
- **Levels** (Ctrl+L) and **Curves**, each with a histogram.
- **Size and shape:** rotate left and right, flip, **Resize** (Ctrl+Alt+I), **Canvas size** (Ctrl+Alt+C), **Straighten** to any angle (with "Cut away the empty corners") and **Trim edges**.

The same commands are in the **Picture** menu.

### Layers

A picture can have several layers, stacked like sheets of glass. The Layers tab lists them with an eye to show or hide each one, a thumbnail and a name (double-click to rename). For the selected layer you can set **Opacity** and **Blend** (Normal, Multiply, Screen, Overlay, Soft light, Hard light, Darken, Lighten, Difference, Add), and add, duplicate, move up or down, delete, **Merge down** or **Flatten** everything into one.

### Pasting

A pasted picture arrives as its own layer:

1. Copy a picture in any app (or take a snip), then press **Ctrl+V** or the **Paste** button.
2. Drag it to move it, and drag its handles to resize it.
3. Press **Enter** or click outside it to finish, or **Esc** to cancel.

With no picture open, Ctrl+V starts a new picture from the clipboard. **Ctrl+Alt+V** (or **As new**) always pastes as a new picture.

### Saving

- **Ctrl+S** saves a copy and **Ctrl+Shift+S** lets you choose the name and format: PNG, JPEG, WebP, BMP, GIF or TIFF.
- JPEG and WebP ask for a **quality**. JPEG shows a live guess of the file size; WebP can also "Keep every pixel exact".
- **Photo details** (date, camera, place) are left out of saved copies unless you tick **Keep photo details** (JPEG to JPEG). When they're kept, the small preview picture stored inside them is wiped, so nothing you hid or cropped away can show through.
- **Save with layers…** (Layers tab or Picture > Layers) writes a `.srpic` file that keeps every layer, shape and text box editable. Opening a `.srpic` shows it like any picture, going into Edit brings the layers back, and Ctrl+S then saves straight back into it. This is the one file the app ever saves over, and it's the app's own.

## Snip

Open **Snip** with the button next to picture Edit, or **Ctrl+Shift+N**. The Snip window opens beside the main window. It's a window of its own, with its own taskbar entry, so you can minimise or maximise it (or the main window) without the other following.

1. Choose a mode: **Rectangle**, **Freeform**, **Window** or **Full screen**. Your choice is remembered.
2. Choose what to snip from: **Screen** (anything on your monitors) or **Picture** (the picture open in Stills & Reels).
3. To snip something that only shows while you hover or click (a menu, a tooltip), choose a delay with the timer button: **No delay**, **3**, **5** or **10 seconds**. Your choice is remembered.
4. Press **New snip**. Only the Snip window steps aside, so the main Stills & Reels window stays on screen and can be snipped too. With a delay, a countdown shows in the corner of the screen ("Snip in 3 · click to cancel"); open your menu while it runs. The countdown never takes the focus, so the menu stays open, and it isn't in the snip. Press Esc or click the countdown to cancel.
5. **Rectangle** and **Freeform:** drag over the area you want. A magnifier shows the exact pixel under the pointer and its position, and the arrow keys move the pointer one pixel at a time (Shift: ten). **Window:** move over a window so it lights up, then click. **Full screen:** the whole of the screen the pointer is on is taken straight away. Esc or a right click cancels.

The Snip window comes back with your snip in it, and the snip is already copied to the clipboard. From there you can:

- mark it up with the tools on the Snip bar: **Arrow**, **Box**, **Text**, **Numbered step** and **Hide** (pixelate), as well as Pen, Highlighter and Eraser, each with its own colour and size. **Select** moves or restyles anything you've added
- **Copy** it again, or **Save** it as a new file, including WebP, or with its layers (`.srpic`) so the arrows and text stay editable
- **Paste into editor** (Ctrl+E) to drop it into Edit picture as a movable layer

A window snip shows what was on screen, so a window partly covered by another shows the covering window too.

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
| , / . | Back / forward one frame (when paused) |
| R | Repeat: start again at the end |
| S | Save the frame you're looking at as a picture |

The buttons on the control bar do the same, and the speed button offers 0.25× to 4×. A saved frame is a new PNG or JPEG at the video's full size.

Press **Edit** next to Videos and the video you're watching goes straight onto the editor's timeline.

## Video snip (screen recording)

Open **Video snip** with the Snip button next to video Edit, or **Ctrl+Alt+R**. Like Snip, it's a window of its own with its own taskbar entry, minimise and maximise.

1. Choose **Area** (a part of the screen you drag out) or **Full screen** (a whole monitor).
2. Pick the frames per second, whether to show the mouse pointer, and a microphone if you want your voice recorded. **PC sound** (whatever plays on your default speakers or headphones: videos, music, games, calls) is recorded by default; the PC sound button turns it off. It can be recorded on its own or mixed with the microphone. Your choices are remembered.
3. Press **Record** (Ctrl+R). For Area, drag over what you want to record. The main Stills & Reels window stays visible so you can record it.
4. After a short countdown, recording starts. A small bar on screen lets you **Pause**, **Resume** and **Stop**. The bar and the area outline are kept out of the recording.

When you stop, "Finishing the recording…" shows for a moment while the recording is encoded, then it opens in the Video snip window. You can **Save as…** an MP4 file, **Add to video editor**, or **Discard** it.

**How recordings are made.** Frames come straight from the graphics card (Windows Desktop Duplication), so playing video doesn't tear, and they are stored losslessly while you record, then encoded once in high quality when you stop. Small text and thin lines stay crisp, and pausing costs no quality. The lossless parts can be large while you record (a full-screen video can take about 1 GB a minute); they're kept in the cache and deleted when the recording is finished, and recording stops by itself, keeping what it has, if the disk gets nearly full. Over Remote Desktop, or for an area that spans two screens, the older capture method is used.

Sound and picture are kept in step: the app times how long after the first frame the sound began and shifts the sound to match, so it runs right up to the last frame. Each recording also writes a short technical log to `%LOCALAPPDATA%\StillsAndReels\recording-log.txt` (what FFmpeg reported and the sound timing). It contains no picture or sound, and you can delete it at any time.

## Video editor

Open it with **Edit** next to Videos, **Alt+3** or **Ctrl+Shift+E**. With nothing on the timeline, the start panel lists your **recent projects**.

**Left panel**
- **Media:** import files (Ctrl+I), or just click videos in File Explorer to add them. Drag from here onto the timeline. Pictures in any format the app opens can be used too.
- **Text:** add titles and captions (T).
- **Transitions**, **Filters** and **Effects:** drag them onto clips.

**Centre: preview.** Drag the selected clip to move it, drag a corner to resize it, and drag the round handle to rotate it. Under the preview are the play and frame buttons and a **level meter** that shows how loud the left and right channels are as the project plays; if it reaches the top, turn something down.

**Right panel: properties** of the selected clip, in tabs: Basic, Adjust, Effects, Audio, Speed and Text.

**Bottom: timeline.**
- Drag clips to move them, including onto a new track. Drag a clip's ends to trim it; hold **Shift** while trimming and everything after it moves along (a ripple trim).
- Drop a clip below the last track to add a new video or audio track.
- Hold **Shift** or **Ctrl** and drag across an empty part of the timeline to select every clip the box touches. Dragging one of several selected clips moves them all together.
- A selected clip shows small **fade handles** at its top corners: drag one inwards to fade the clip in or out.
- Ctrl and the mouse wheel zoom the timeline.
- Drag the playhead along the ruler to scrub: the preview shows the frame under it as you drag (quickly while dragging, sharp when you let go; zoomed out to minutes it shows the nearest keyframe while you drag), and while the preview plays the playhead moves with it.

**Editing keys:** Space plays, ← / → step one frame (Shift: one second), ↑ / ↓ jump to the previous or next cut, **S** or **Ctrl+B** splits, **Q** and **W** trim the start or end of the selected clip to the playhead, **Del** deletes, **Shift+Del** deletes and closes the gap, **Ctrl+C / X / V** copy, cut and paste, **Ctrl+D** duplicates, and **Ctrl+Z** undoes.

### Markers and the marked range

- **Shift+M** puts a **marker** at the playhead (press it again there to remove it). Right-click the ruler to name a marker, go to it or remove it. Clips and the playhead snap to markers.
- **I** and **O** mark the start and end of a **range**, shown as a band on the ruler; **Ctrl+Shift+X** clears it. Export can then write just that part, and **Play the range round and round** in the ruler's right-click menu repeats it while you work on it.

### Position, size and look (Basic)

- **Position, Scale, Rotation and Opacity**, with mirror, flip and 90° turns.
- **Fit** shows the whole picture inside the canvas; **Fill** enlarges it until it covers the canvas. **Blurred background** fills the empty space around a clip that doesn't match the canvas (a phone video on a wide canvas) with a blurred copy of itself. **Crop** cuts the edges off.
- **Blending:** how the clip mixes with what's under it: Normal, Screen, Multiply, Overlay, Soft light, Hard light, Add, Darken, Lighten or Difference.
- **Mask:** show only an Oval, a Box or one side of a Split, with a **Soft edge**, or **Show the outside instead**.
- **Frame:** Rounded corners, a Border in any colour, and a Shadow.

### Keyframes and motion

Move the playhead, press **Ctrl+K** (or the keyframe button) and change the clip's position, size, rotation or opacity; do the same at another time and the clip moves between the two. The **Motion** button under the keyframe buttons chooses how the clip travels to a keyframe: Straight, Smooth, Slow start, Slow finish, Stay then jump, Overshoot or Bounce.

For movement without keyframes, the Effects tab has **Movement** (Slow zoom in or out, Pan left, right, up or down, Float, Swing, Pulse, Shake, Spin, with a strength slider) and **Animation**: how the clip arrives and how it leaves (Fade, Slide in four directions, Pop, Grow, Shrink, Spin, Drop, and Typewriter for text), each with its own length.

### Transitions, filters and effects

- **30 transitions:** Dissolve, Fade to black or white, Blur, Wipes (four directions, soft and diagonal), Slides, Covers, Circle, Diamond, Clock, Doors, Shutters, Blinds, Zoom, Zoom out, Spin, Pixelate and Glitch. Drag one onto a clip; it plays as the clip comes in, and its length is set in the clip's properties.
- **23 filters**, from Mono, Noir and Sepia to Teal & orange, Golden hour, Arctic and Night, with a strength slider.
- **Adjust:** Exposure, Brightness, Contrast, Highlights, Shadows, Saturation, Temperature and Tint.
- **Effects:** Blur, Sharpen, Glow, Pixelate, Colour fringes, Mirror, Vignette, Film grain, **Remove a colour** (chroma key, for green screens) and **Steady shaky video**.

### Sound (Audio)

- **Volume** from 0 to 400%. The keyframe button beside it sets a volume point at the playhead, so the volume can change over the clip; the line and its points are drawn on the clip's waveform. **Fade in** and **Fade out** set the lengths of the fades.
- **Clean up:** **Noise reduction** (a slider: more removes more hiss and hum), **Voice boost** for clearer speech, and **Even out the loudness** for a clip that is quiet in places and loud in others.
- **Tone:** **Bass**, **Treble**, and **Pitch** up or down without changing the speed.
- **Mixing:** tick **Turn down under other sound** on a music clip and it drops automatically while any other clip is making sound (speech, usually), then comes back. **How far down** sets the drop.
- **Detach audio** moves a video's sound to its own track.

Noise reduction, pitch and even loudness take a moment to prepare the first time. The clip plays as it was until they're ready, then switches over by itself, and an export always waits for the finished sound.

### Speed and backwards (Speed)

- **Speed** from 0.1× to 16×, on a slider or the preset buttons. The sound keeps its pitch.
- **Play backwards** writes a reversed copy of the part of the clip you're using and swaps it in. It takes a little while for long clips (the status line shows how far it has got), and afterwards the clip behaves like any other. The reversed copy is kept with the project when you save.

### Saving projects

**File > Save video project** (Ctrl+S) saves a `.srproj` file that remembers where your media is. Recordings, freeze frames, reversed clips and pasted pictures are copied into a `<project name> media` folder next to the project. Projects from 2.6 and older `.ppvproj` projects from Picture Preview still open. **File > Recent video projects** lists the last ten.

**If something goes wrong.** While a project has unsaved changes, the app keeps a recovery copy every 45 seconds in `%LOCALAPPDATA%\StillsAndReels\Recovery`. If the app or the PC stops unexpectedly, the next start asks whether to bring the project back. The copy is deleted when you save or close the project normally.

### Exporting

Press **Export** at the top right, or **Ctrl+M**. The **Presets** button fills everything in for common uses (YouTube and TV, phones and social apps, small for email, websites, GIFs, sound only), or choose for yourself:

| Export | Formats | Notes |
|---|---|---|
| **Video** | MP4 (H.264), MP4 (HEVC), WebM | H.264 plays everywhere. HEVC gives a smaller file for the same quality but older devices can't play it. WebM (VP9 with Opus sound) is for websites and is the slowest to make |
| **Animated GIF** | GIF | No sound. Small, Medium, Large or Very large (240 to 720 px wide) at 10 to 25 frames a second. GIFs get big quickly: keep them short |
| **Sound only** | MP3, M4A, WAV | WAV is uncompressed, for more editing elsewhere |

- **Size and frame rate:** the project's own (chosen by default) or 480p to 4K at 24 to 60 frames per second.
- **Quality:** Smaller file, Standard, High (the default) and Best.
- **Part:** when a range is marked, export the whole project or just the range.
- **Speed:** when the PC's graphics card has a video encoder (NVIDIA, Intel or AMD), the **Use the graphics card** switch makes H.264 and HEVC exports several times faster, for a somewhat bigger file. If the card's encoder fails, the export carries on with the processor by itself.

The window shows a rough file size before you start, and which encoder was used when it finishes. Colours are written as standard HD (BT.709) and labelled, so players show what you edited. Your choices are remembered for next time.

The first video you put on a new project sets the canvas size and frame rate to match it, so a 4K or 60 fps clip, or a screen recording, keeps its size. Videos are decoded with sharp scaling, and a clip that fills the canvas is copied pixel for pixel.

## Settings

Open **File > Settings** (Ctrl+,):

- Dark mode (the default), light mode or Graphite (charcoal greys with a sky-blue accent). You can also switch in **View > Theme** or the **⋯** menu.
- Follow the selection in File Explorer
- Show the pixel grid when zoomed in to 600% or more
- Start playing when a video is selected
- Keep videos you view in the video editor's Media
- Always on top
- Show the status bar

## Your files and privacy

- **Originals are never changed.** Edits, snips, recordings and exports are only ever saved as new files. The only files the app saves over are its own: video projects and layered `.srpic` pictures.
- **Pictures are read from a temporary copy**, which is deleted as soon as you move to another picture.
- **The media cache:** every video you view (if that setting is on) and every file you import is copied into a private cache folder for the editor. The copies stay until you remove them from Media or close the app, and then they're deleted.
- **Recovery copies** of an unsaved video project are kept in `%LOCALAPPDATA%\StillsAndReels\Recovery` and deleted when the project is saved or closed. The lists of recent projects and your last export choices are small text files in `%APPDATA%\StillsAndReels`.
- **Nothing is sent anywhere.** The app has no network code.

### The video engine

The first time you use a video feature, Stills & Reels unpacks its built-in video engine (FFmpeg 7.1) to `%LOCALAPPDATA%\StillsAndReels\Engine`. That's about 85 MB, takes a few seconds and happens only once. The app runs it in the background, so you never run it yourself. It's only given local files (and, when you record, your screen and the microphone you choose), so it never uses the network. To remove it, delete that folder.

## Keyboard shortcuts

**Help > Keyboard shortcuts** shows these in the app too.

| Pictures | |
|---|---|
| ← / → (or Page Up / Page Down) | Previous / next picture in the folder |
| Home / End | First / last picture in the folder |
| F11 / F5 | Full screen / slideshow |
| Ctrl+I | Picture details |
| Ctrl + / Ctrl − | Zoom in / out |
| Ctrl+0 / Ctrl+1 | Fit to window / actual size |
| Ctrl+E | Edit picture |
| Ctrl+Shift+N | Snip |
| Ctrl+R / Ctrl+Shift+R | Rotate right / left |
| Ctrl+C | Copy |

| Edit picture | |
|---|---|
| V | Select and move |
| M / L / W | Rectangle select / lasso / magic wand |
| C | Crop |
| P / H / E | Pen / highlighter / eraser |
| G / I | Fill / colour picker |
| A / R / O / T | Arrow / rectangle / oval / text |
| [ / ] | Smaller / bigger tool |
| Ctrl+A / Ctrl+Shift+A / Ctrl+Shift+I | Select all / none / invert |
| Ctrl+X / Del | Cut / clear the selection |
| Ctrl+D | Duplicate the selected item |
| Ctrl+L / Ctrl+Shift+L | Levels / auto fix |
| Ctrl+Alt+I / Ctrl+Alt+C | Resize / canvas size |
| Ctrl+V / Ctrl+Alt+V | Paste as a layer / as a new picture |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+S / Ctrl+Shift+S | Save a copy / save as |
| Space + drag | Move around the picture |

| Videos | |
|---|---|
| Space or K | Play / pause |
| ← / → | Back / forward 5 s |
| J / L | Back / forward 10 s |
| , / . | Back / forward one frame |
| ↑ / ↓ | Volume |
| M | Mute |
| R | Repeat |
| S | Save this frame as a picture |
| F or F11 / Esc | Full screen / leave full screen |

| Video editor | |
|---|---|
| Space | Play / pause |
| ← / → | One frame (Shift: one second) |
| ↑ / ↓ | Previous / next cut |
| Home / End | Start / end of the project |
| S or Ctrl+B | Split |
| Q / W | Trim the start / end to the playhead |
| Del / Shift+Del | Delete / delete and close the gap |
| Ctrl+C / X / V | Copy / cut / paste |
| Ctrl+D | Duplicate |
| Ctrl+A | Select every clip |
| Ctrl+K | Keyframe |
| I / O | Start / end of the marked range |
| Ctrl+Shift+X | Clear the range |
| Shift+M | Marker at the playhead |
| Shift + drag a clip's edge | Ripple trim |
| Shift or Ctrl + drag on empty timeline | Box select |
| T | Add text |
| M | Mute the preview |
| F | Bigger preview |
| + / − | Zoom the timeline (also Ctrl + wheel) |
| Ctrl+0 | Fit the project in view |
| Ctrl+I | Import |
| Ctrl+S | Save project |
| Ctrl+M | Export |

| Anywhere | |
|---|---|
| Alt+1 / Alt+2 / Alt+3 | Pictures / Videos / Video editor |
| Ctrl+Shift+E | Edit video |
| Ctrl+Alt+R | Video snip |
| Ctrl+O | Open a file |
| Ctrl+, | Settings |

## Taskbar shortcuts: Pic Snip and Vid Snip

Pin the app to the taskbar: start it, right-click its taskbar icon and choose **Pin to taskbar**. Right-click the pinned icon and you'll find **Pic Snip** and **Vid Snip** under Tasks. Each opens straight into that snip tool:

- If Stills & Reels is already open, the snip window opens in it.
- If not, only the snip window appears, in the middle of the screen, with its own taskbar button; there is no main window until you choose **Paste into editor** or **Add to video editor**, which open it. Closing the last snip window ends the app.

The windows close separately too: closing the main window while a snip window is open leaves the snip window running (the main window asks about unsaved work as usual), and the app ends when the last window closes. **File > Exit** closes everything at once.

The shortcuts are written each time the app starts, so they keep working if you move the exe (start it once from its new place). They run `StillsAndReels.exe --picsnip` and `--vidsnip`, which you can also use in your own shortcuts.

## Troubleshooting

- **"Windows protected your PC"** when starting the app: click **More info**, then **Run anyway**. The app isn't code-signed, so Windows asks once.
- **Nothing appears when I click a file in File Explorer:** check **Follow the selection in File Explorer** is on in Settings, and look at the status bar to see how many File Explorer windows are being watched.
- **Pic Snip and Vid Snip don't show when I right-click the taskbar icon:** start the app once from the exe you pinned (the shortcuts are written when it starts). If you pinned an older copy, unpin it and pin the one you use now.
- **The first video takes a few seconds to open:** that's the video engine unpacking. It only happens once.
- **My recording has no computer sound:** check the **PC sound** button in the Video snip bar is on. PC sound records whatever plays on the default speakers or headphones at the time you press Record.
- **A recording's sound and picture don't match:** the app corrects start-up timing automatically. If a recording still looks off, `%LOCALAPPDATA%\StillsAndReels\recording-log.txt` shows what happened in that recording.
- **A picture clicked on a web page looks small:** pictures from web pages are taken from the screen as shown. Right-click the picture and choose **Copy image** to pick it up at full size.
- **A file won't open:** camera raw files, SVG, JPEG XL and animated WebP aren't supported. The app tells you when a file can't be shown.
- **"Use the graphics card" isn't offered in Export:** the app tests the card's encoder the first time the Export window opens and only offers it if a short trial encode works. Updating the graphics driver often brings it back. Exports work without it; they just take longer.
- **A clip with noise reduction, a pitch change or even loudness sounds unchanged for a moment:** that sound is being prepared in the background. It switches over by itself, and exports always use the finished sound.
- **The app closed in the middle of a video project:** start it again and choose to bring the project back when asked.
