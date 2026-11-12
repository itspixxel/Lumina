# Lumina

A lightweight, modern screenshot and screen recording tool for Windows - a replacement for Snipping Tool with a fast capture overlay, a floating preview, a non-destructive markup editor, and screen recording with a trim editor.

> **Alpha.** Lumina is early and under active development. Expect rough edges, and please report anything that breaks.

![Lumina's home window with capture modes and recent captures](docs/screenshots/home.png)

## Features

### Capture
- **Four modes**: Region, Window, Full screen and Freeform (lasso).
- **Win + Shift + S** opens Lumina instead of Snipping Tool while it's running (toggle in Settings). Print Screen can be used too.
- **Frozen-screen overlay**: the screen is captured up front, so menus and tooltips stay put while you select.
- **Smart highlighting**: hover a window to capture it in one click; the highlight glides between targets.
- **Precision tools**: magnifier with pixel grid, coordinates and color under the cursor; arrow-key nudging (Shift = 10px); live size readout.
- **Multi-monitor and per-monitor DPI aware.**
- **Capture delay**: 3/5/10 second countdown from the home window.

### After capture
- Copies to the clipboard and saves to your output folder (both optional).
- A **floating preview** slides into the corner: drag it straight into another app, copy, save, edit or delete.
- Customisable file names (`{yyyy}-{MM}-{dd}`, `{app}`, counters...), PNG or JPEG.

### Markup editor
- **Tools:** select/move, pen (with stroke smoothing), highlighter, arrow, line, rectangle, ellipse, text, numbered steps, redact, spotlight and crop.
- **Redact** with mosaic, blur or a solid cover, with adjustable strength.
- **Spotlight** with rectangle or ellipse shapes, adjustable darkness and optional background blur.
- **Crop** with aspect-ratio presets (Free, Original, 1:1, 4:3, 3:2, 16:9, 9:16).
- **Color picker**: presets plus a full HSV picker, hex input, eyedropper and recent colors.
- **Non-destructive**: markup stays editable after saving; reopen a capture to keep working on it.
- Open any image file from the home window to mark it up.
- Smooth zoom (Ctrl + scroll zooms around the cursor), fit-to-window and 1:1, full undo/redo.

![The markup editor with a box, numbered steps, an arrow, text, a highlight and blurred emails](docs/screenshots/editor.png)

### Screen recording
- **Win + Shift + R** (or the Record card / tray menu) picks a region, window or screen with the same overlay, then counts down and records.
- **Sound**: system audio and/or microphone, each with its own device. Pick devices and mute either source right from the overlay's toolbar.
- **Recording bar**: timer, pause/resume, stop, per-source mute and discard. It never steals focus and never shows up in the video.
- **Movable area**: drag the recording outline (or its tab) to follow what you're recording.
- **GPU encoding** with NVIDIA, Intel or AMD when available, falling back to x264; 30 or 60 fps.
- **Trim editor** opens when you stop: drag the handles on the filmstrip, then save, save as MP4 or GIF, export a GIF at a chosen width, or copy the file.
- **Straight to the clipboard**: finished recordings and exported clips are copied as files, ready to paste into a chat or folder (follows the same *Copy to clipboard* setting as screenshots).
- Uses **FFmpeg**: Lumina downloads it for you on first run (about 115 MB, one click), or point it at your own `ffmpeg.exe` in Settings.

![The trim editor with a filmstrip timeline and trim handles](docs/screenshots/trim.png)

### Gallery
- **Recent** on the home window shows your latest screenshots and recordings; *View all* (or `Ctrl + F`) opens the full gallery.
- **Search** file names, the app something was captured from, and the text in your markup. Just start typing.
- **Filters and sorting**: all, screenshots, recordings or edited screenshots; newest, oldest, name or largest first. Captures are grouped by date (Today, Yesterday, this week, by month).
- **Fast with thousands of files**: Lumina keeps an index and a thumbnail cache, picks up files added or changed outside it, and only loads what's on screen.
- **Keyboard friendly**: arrow keys, Home / End and Page Up / Down move around (`Shift` to select a range), Space selects, Enter opens.
- Multi-select (click the corner circle, or Ctrl / Shift + click), copy, rename (F2), reveal in Explorer and delete to the Recycle Bin (Shift + Delete deletes permanently).

<p align="center"><img src="docs/screenshots/settings.png" width="520" alt="Settings: shortcuts, capture options and recording with audio devices"></p>

## Install

Download the latest build from the [Releases](../../releases) page:

- **`Lumina-<version>-win-x64.zip`**: self-contained, no install or runtime needed. Unzip anywhere and run `Lumina.exe`.
- **`Lumina-<version>-win-x64-framework.zip`**: much smaller, but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

From 0.3.2 on, Lumina keeps itself up to date: it downloads new releases in the background and restarts into them when it isn't in use (you can turn this off in Settings, under General).

Lumina lives in the system tray. Settings are stored in `%AppData%\Lumina`. For a fully **portable** setup, create an empty file named `portable` next to `Lumina.exe` and everything will be kept in a `data` folder beside it.

> Windows SmartScreen may warn about an unrecognised app because the builds aren't code-signed yet. Choose *More info*, then *Run anyway*.

## Shortcuts

| Where | Keys | Action |
| --- | --- | --- |
| Anywhere | `Win + Shift + S` | Start a capture |
| Anywhere | `Win + Shift + R` | Start a recording, or stop the one running |
| Overlay | `1` `2` `3` `4` / `R` `W` `S` `L` | Region / Window / Screen / Freeform |
| Overlay | `F` or `Enter` | Capture the screen under the cursor |
| Overlay | Hold `Space` while dragging | Move the selection |
| Overlay | Arrow keys (`Shift` = 10 px) | Nudge the selection |
| Overlay | `Esc` / right-click | Cancel (right-click first clears a selection in progress) |
| Editor | `V` `P` `H` `A` `L` `R` `E` `T` `N` `B` `S` `C` | Select, pen, highlighter, arrow, line, rectangle, ellipse, text, steps, redact, spotlight, crop |
| Editor | `[` / `]` | Smaller / larger brush |
| Editor | `Ctrl + Z` / `Ctrl + Y` | Undo / redo |
| Editor | `Ctrl + C`, `Ctrl + S`, `Ctrl + Shift + S` | Copy, save, save as |
| Editor | `Ctrl + E`, `Ctrl + Delete` | Show in folder, delete |
| Editor | `Ctrl + 0` / `Ctrl + 1` / `Ctrl + scroll` | Fit, actual size, zoom |
| Trim editor | `Space` | Play / pause |
| Trim editor | `Left` / `Right` (`Shift` = 5 s) | Step a frame |
| Trim editor | `I` / `O`, `Home` / `End` | Set trim start / end, jump to start / end |
| Trim editor | `Ctrl + C`, `Ctrl + S`, `Ctrl + Shift + S` | Copy the video, save the trim, save as |
| Trim editor | `Ctrl + G`, `Ctrl + E` | Export a GIF, show in folder |
| Trim editor | `M`, `R`, `Delete` | Sound on / off, reset the trim, delete |
| Home | `1` to `5` | Region / Window / Screen / Freeform / Record |
| Home | `Ctrl + O`, `Ctrl + ,`, `Ctrl + E`, `F5` | Open an image, settings, open folder, refresh |
| Home | `Ctrl + F` | Open the gallery, ready to search |
| Home, gallery | Arrow keys, `Home` / `End`, `Page Up` / `Page Down` | Move between captures (`Shift` selects a range) |
| Home, gallery | `Enter`, `Space`, `F2`, `Delete`, `Ctrl + C`, `Ctrl + A`, `Esc` | Open, select, rename, delete, copy, select all, clear selection |
| Gallery | Type, or `Ctrl + F` | Search |
| Gallery | `Esc` | Clear the search, then the filters |

## Building from source

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/itspixxel/Lumina.git
cd Lumina
dotnet run --project src/Lumina
```

To produce a self-contained single-file build:

```bash
dotnet publish src/Lumina -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

### Project layout

```
src/Lumina/
  Capture/    screen capture, the selection overlay and capture animations
  Editor/     markup document, annotations and the editing canvas
  Recording/  screen recorder, WASAPI audio capture, FFmpeg setup and the recording flow
  Services/   settings, output, clipboard, hotkeys, startup, updates, the library index and thumbnails
  Views/      home, gallery, editor, settings, preview, dialogs and the motion helpers (Fx)
  Themes/     the "Midnight" design system
tools/        icon generator
```

## Roadmap

- [x] Capture overlay, quick preview, markup editor, settings
- [x] Gallery with multi-select
- [x] Screen recording (video and GIF) with a trimming editor
- [x] Full gallery view with search
- [x] Auto-update
- [ ] Search the text inside screenshots (OCR)
- [ ] Optional short-link uploads (temporary, self-hosted storage)
- [ ] Installer

## Status

Lumina is a personal project in alpha. Issues and suggestions are welcome.
