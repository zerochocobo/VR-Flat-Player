# VR Flat Player

**English** · [简体中文](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md)

<img src="assets/icon-256.png" width="128" alt="VR Flat Player">

A desktop player for watching **180° / 360° VR video on an ordinary flat
monitor**, comfortably, including local 8K. Optionally it tracks your head with
a plain webcam and turns the view for you.

Version 0.2. Windows only.

Decoding and rendering are mpv + mpv360; this repository is the player window
and the tracking bridge between them.

```
                          VRFlatPlayer.exe
  ┌─────────────────────────────────────────────────────────┐
  │  Media  Playback  Audio  Subtitles  VR  View  Help      │
  ├─────────────────────────────────────────────────────────┤
  │                                                         │
  │   mpv's window  (--wid child, a separate process)       │
  │     mpv360 projection shader · uosc bar · mode panel    │
  │                                                         │
  └─────────────────────────────────────────────────────────┘
        ▲                                    ▲
        │ JSON IPC                           │ left-drag (Win32 mouse polling)
        │                                    │
   ┌────┴─────────────────────────────────────┴────┐
   │  bridge: One Euro filter / gain curve / recentre │
   └───────────────────────┬───────────────────────┘
                           │ UDP (optional)
                  opentrack ◄── webcam
```

## What it does

- **180 / 360 / fisheye / cylindrical / EAC**, mono or stereo, side-by-side or
  over-under. One eye is shown, because a flat monitor has one viewpoint.
- **Detects the layout** from the filename conventions the three common VR
  players established, and from the frame shape when the name says nothing.
  A 2:1 file is genuinely ambiguous — mono 360 and VR180 side-by-side are
  pixel-identical — so the choice is remembered per file and correctable from
  the menu.
- **Webcam head tracking**, off by default. YuNet finds the face, a 68-point
  landmarker locates it, and PnP turns that into a head pose. No markers, no
  extra hardware, no opentrack install required — though opentrack over UDP is
  still supported if you already use it.
- **Drag to look** with the mouse, keyboard look, wheel to zoom.
- **A native menu bar** rather than an overlay, in English, Simplified and
  Traditional Chinese, Japanese and Korean, following the OS language.

## Requirements

- Windows 10 or 11, x64
- A GPU that can do Direct3D 11 (the menu offers Vulkan and OpenGL as fallbacks)
- For head tracking: any webcam
- 8K playback wants a recent discrete GPU; see the notes on decoder choice

## Install

Download the release zip, unpack it anywhere, run `VRFlatPlayer.exe`. Nothing is
installed and nothing is written outside the folder.

To add the player to Explorer's *Open with* list, run
`register-file-types.bat` from the same folder. `unregister-file-types.bat`
removes it. Both write only under `HKEY_CURRENT_USER`, so no administrator
rights are needed.

## Build from source

```
git clone <this repository>
cd VRHeadTrackingPlayer

tools\install-mpv360.bat      # mpv360 shader, uosc, fonts
tools\install-models.bat      # the two ONNX models

dotnet run --project tests/VideoFormatTests/VideoFormatTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

`publish.ps1` produces `dist\VR Flat Player\` and a versioned zip. It needs an
mpv.exe to bundle and will find one that is installed, or take `-MpvExe <path>`.

.NET 8 SDK is required to build. The published exe is self-contained, so users
do not need .NET.

### The ONNX models are not in this repository

Head tracking needs two models, together about 14 MB. They are not committed —
binaries do not belong in source history, and both are published elsewhere under
their own licences. `tools\install-models.bat` fetches them into `models\`:

| File | Model | Source | Licence |
| --- | --- | --- | --- |
| `face_detection_yunet.onnx` | YuNet | [opencv/opencv_zoo](https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx) | MIT |
| `face_landmark_peppa_wutz.onnx` | peppa_wutz 68-point landmarker | [facefusion/facefusion-assets](https://github.com/facefusion/facefusion-assets/releases/download/models-3.0.0/peppa_wutz.onnx) | MIT |

Without them the player still works; only head tracking is unavailable.

### mpv is not in this repository either

mpv is a separate GPL program, bundled with releases rather than linked. The
repository holds only our own configuration and scripts under `mpv/`:
`mpv.conf`, `input.conf`, `vrmenu.lua` and our fork of the mpv360 shader in
`mpv/shaders-src/`.

Everything vendored from upstream — `mpv.exe`, `mpv360.lua`, uosc, fonts, the
compiled shader — is fetched by `tools\install-mpv360.bat` and ignored by git.

## Keys

| Key | Action |
| --- | --- |
| `Home` | Recentre — make the current head pose the new neutral |
| `Alt` + arrows | Look, 5° a press |
| Mouse wheel | Field of view, 5° a notch |
| Left-drag | Look |
| `Tab` | Mode panel |
| `Ctrl+E` | 360 mode on/off |
| `Ctrl+Shift+P` | Cycle projection |
| `Ctrl+Shift+E` | Swap eye |
| `Ctrl+Shift+↑ / ↓` | Field of view |
| `Ctrl+Shift+V` | Reset the view without moving the head reference |
| `Ctrl+Shift+H` | Head tracking on/off |
| `Ctrl+[` / `Ctrl+]` | Tracking gain down / up |
| `Ctrl+Shift+I` | Playback statistics |
| `F` | Fullscreen |

Ordinary mpv keys — space, arrows, volume — work as usual.

## Configuration

`bridge.config.json` sits next to the exe and is written as you change settings
in the menus. Deleting it restores the defaults. A fresh copy can be produced
with `VRFlatPlayer.exe --config=path.json --write-config`.

The values worth knowing:

| Setting | Default | Why |
| --- | --- | --- |
| `yaw.outputRangeDegrees` | 70 | View degrees at full head turn |
| `yaw.stickyDegrees` | 1.0 | Head movement ignored before the view follows |
| `pitch.inputRangeDegrees` | 12 | People pitch their head far less than they turn it |
| `video.fallback` | `vr180` | What a 2:1 file with no clues is opened as |
| `source.camera.landmarkFps` | 30 | Lower it if the landmarker is starving the decoder |

Window position, per-file VR modes and the run log are kept in separate files
(`window-state.json`, `mode-memory.json`, `mpv-last-run.log`) so that clearing
one does not clear the others.

## Troubleshooting

`mpv-last-run.log`, next to the exe, holds one run: the player's startup
diagnostics and mpv's output interleaved. It records which VR mode was chosen
and why, the file's resolution and codec, the decoder and renderer in use, and
what mpv was actually left set to — which is usually enough to tell a detection
mistake from a rendering one.

If the picture is black, try Playback → Renderer; the likeliest cause is a
driver that cannot do the default backend.

## Project layout

```
src/HeadTrackBridge/     the player: window, menus, IPC, tracking, mapping
  Host/                  WinForms window and menu bar
  Mpv/                   IPC client, mode controller, format detection
  Tracking/              camera, landmarks, pose solving
  Mapping/               filter, gain curve, view composition
mpv/                     our mpv configuration, scripts and shader source
tests/VideoFormatTests/  608 assertions, runs in seconds
tools/                   install scripts, icon generator, publish
prompt/                  development handover notes (Chinese)
```

`AGENTS.md` holds the working rules for this repository, each written down
because breaking it cost real time.

## Licence and credits

This player is free software under the **GNU General Public License v3.0 or
later**. See [LICENSE](LICENSE).

GPLv3 rather than something permissive because releases bundle mpv, which is
GPLv2-or-later: the "or later" is what makes the two compatible, and keeping
this player under the same terms keeps the combined download unambiguous.

It stands on:

- **[mpv](https://mpv.io/)** (GPLv2+) — decoding, rendering, playback. Bundled
  with releases as a separate executable, unmodified.
- **[mpv360](https://github.com/kasper93/mpv360)** (MIT) — the projection
  shader. Our fork adds side-by-side stereo 360 and mono fisheye, and renders
  at output resolution instead of source resolution.
- **[uosc](https://github.com/tomasklaen/uosc)** (LGPL-2.1) — the control bar.
- **[YuNet](https://github.com/opencv/opencv_zoo)** (MIT) — face detection.
- **peppa_wutz** (MIT) — 68-point landmarks.
- **One Euro Filter** — Casiez, Roussel and Vogel, CHI 2012.
- **[opentrack](https://github.com/opentrack/opentrack)** — optional UDP source.
