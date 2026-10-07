# Paradis Capture

**Simple Screen & Audio Recorder** · version 1.0.2 · Windows 10 (2004+) and 11, 64-bit

Part of [**Paradis EZ Utilities**](https://github.com/Paradis-EZ-Utilities): small, simple, free.
EZ.

Paradis Capture is a lightweight Windows utility for recording:

- an application window
- a full monitor
- a selected region of the screen
- computer audio
- microphone audio

The result is a normal `.mp4` file (H.264 video, AAC audio) that plays anywhere. You can pause
and resume, record at 30 or 60 FPS, and pick a **Compact**, **Standard**, **High** or
**Maximum** quality preset in Settings depending on whether you're recording a lecture or a game.

It runs entirely on your PC. There's no account, no telemetry, no uploads, no recording time
limit and no watermark. No streaming, scenes or overlays either: it records and saves the file.

---

## For normal use

### Getting it running

Unzip `ParadisCapture-v1.0.2.zip` anywhere (your Desktop, Documents, a USB stick) and run
`ParadisCapture.exe`. There's nothing to install: it carries its own copy of .NET. To build it
yourself, see the [developer instructions](#for-developers).

It runs as a normal user and never asks for administrator rights. The first time, Windows
SmartScreen may say it "protected your PC" because the exe isn't code-signed; choose
**More info → Run anyway**.

To uninstall, delete the exe. Settings and logs live in `%LocalAppData%\ParadisCapture` if you
want to remove those too.

### Recording something

1. **Choose what to record.**
   - **Window** — opens Windows' own "choose what to share" picker. The selected window is
     recorded even when other windows cover it.
   - **Region** — the screen dims, you drag a rectangle, and that area becomes the recording.
     The dimensions are shown while you drag. Press `Esc` to cancel.
   - **Monitor** — records one whole display. With more than one connected, you pick which.

   Once chosen, the box underneath says exactly what will be recorded, e.g.
   `Window: Unity - MyGame`, `Region: 1920 × 1080`, `Monitor: Display 2`.

2. **Choose your audio.** Two independent switches:
   - **Computer audio** — everything you can hear coming out of your PC.
   - **Microphone** — off by default.

   With both on, both end up in the file, mixed.

3. **Press RECORD.** The main window disappears and a small bar appears at the bottom of your
   main screen: a blinking red dot, the elapsed time, **Pause** and **Stop**. The bar never
   appears in the recording.

4. **Press Stop** when you're done. Paradis Capture tells you where the file went and offers
   **Open Folder** and **Play**.

### While recording

- **Pause / Resume** — the paused stretch simply isn't in the file; there's no gap and no second
  file.
- **Hotkeys** work even while a game has focus. By default:
  - `Ctrl+Shift+F9` — start / stop
  - `Ctrl+Shift+F10` — pause / resume

  Both can be changed or switched off in Settings.
- **The — button** hides the bar to the system tray. Double-click the tray icon to bring it back,
  or right-click it to pause or stop. Handy for full-screen games.
- Your PC won't go to sleep or blank the screen mid-recording.

### Where recordings go

`C:\Users\<you>\Videos\Recordings`, created automatically. Change it from the main window
(**Change**) or in Settings; the choice is remembered.

Files are named for when they were recorded, and for the application when one can be identified:

```
Recording_2026-10-05_10-42-31.mp4
Unity-6_2026-10-05_10-42-31.mp4
```

An existing recording is never overwritten — a second file with the same name becomes
`… (2).mp4`.

While a recording is in progress you'll see a `.recording.mp4` file next to it. That's the
recording being written; it's converted into the final `.mp4` when you press Stop. If the PC
loses power mid-recording, that file is still playable (see
[Crash safety](#crash-safety-and-interrupted-recordings)).

### Settings

Small on purpose:

| Setting | Default | Notes |
| --- | --- | --- |
| Save folder | `Videos\Recordings` | |
| Frame rate | 30 FPS | 60 FPS for games and playtests |
| Quality | Standard | Compact / Standard / High / Maximum, see below |
| Include the mouse cursor | On | |
| Computer audio device | Follows Windows | |
| Microphone device | Follows Windows | |
| Hotkeys | `Ctrl+Shift+F9`, `Ctrl+Shift+F10` | Click the box, press the keys; `Del` clears |
| Hide the bar to the tray when recording starts | Off | |

Settings are saved in `%LocalAppData%\ParadisCapture\settings.json` and remembered between
launches. The main window shows the current quality and frame rate next to the Settings link;
frame rate and quality are only changed in Settings, so the main window stays simple. At the
bottom of Settings are links to **About** (version, credits and **More from Paradis EZ
Utilities**) and the log folder.

### Quality presets and file size

**Standard is the right choice for most people**, and it's the default. You only pick a preset
in Settings; there are no bitrates or encoder options to understand.

| Preset | Pick it for | Trade-off |
| --- | --- | --- |
| **Compact** | Classes, slides, Zoom/Teams, coding, mostly-still screens | Smallest files. Text stays sharp; fast motion looks softer |
| **Standard** (default) | Everyday recording: classes, meetings, tutorials, workflows | Balanced. Small files, readable text and UI |
| **High** | Games, playtests, animation, fast scrolling | Noticeably less smearing and blockiness in motion; bigger files |
| **Maximum** | When picture quality matters more than disk space | Highest visual fidelity; largest files |

**Frame rate:** 30 FPS is plenty for classes and desktop work. Use 60 FPS for smoother gameplay
and motion; every preset automatically gives 60 FPS about 70% more bandwidth, so it isn't starved.

The presets never change the resolution: a window or monitor is always recorded at its native
size. They change how hard the video is compressed, how often a keyframe is written, and the
audio bitrate. The video bitrate is an *average* ceiling, not a fixed rate: the encoder spends
less on a still screen and more during motion, so real files usually come out smaller than below.
It also scales with what you record, so a small region gets proportionally less than a full
1080p screen.

Rough upper bounds per hour, full screen:

| Recording | Compact | Standard | High | Maximum |
| --- | --- | --- | --- | --- |
| 1080p 30 FPS | ~0.45 GB | ~0.9 GB | ~2.3 GB | ~5.1 GB |
| 1080p 60 FPS | ~0.7 GB | ~1.4 GB | ~3.9 GB | ~8.6 GB |
| 1440p 30 FPS | ~0.7 GB | ~1.3 GB | ~4.1 GB | ~9.0 GB |

<details>
<summary>The numbers behind the presets</summary>

| Preset | Video at 1080p30 / 1080p60 | Scaling with size | Audio | Keyframe |
| --- | --- | --- | --- | --- |
| Compact | 0.9 / 1.5 Mbit/s | `(pixels / 1080p)^0.8` | 96 kbit/s | every 4 s |
| Standard | 1.8 / 3.0 Mbit/s | `(pixels / 1080p)^0.8` | 128 kbit/s | every 4 s |
| High | 5.0 / 8.4 Mbit/s | linear, 0.08 bits/pixel/frame | 192 kbit/s | every 2 s |
| Maximum | 11.2 / 18.8 Mbit/s | linear, 0.18 bits/pixel/frame | 192 kbit/s | every 2 s |

H.264 High profile, unconstrained VBR at that mean. Maximum uses the original Simple Recorder
prototype's top quality level.
</details>

For a 1–3 hour class, **Compact or Standard at 30 FPS** is the one to pick. Recording stops
cleanly if the disk gets close to full, and warns you before that.

### When something goes wrong

Paradis Capture tries to explain the problem in one sentence and keep whatever was recorded:

- The window you were recording closes → the recording stops and is saved.
- Your microphone is unplugged → the recording continues without it and reconnects if it comes
  back.
- Windows switches audio output (you plug in headphones) → it follows the new device.
- A monitor is disconnected → you're told to pick another target.
- The disk fills → the recording stops and the file is kept.

The full technical detail goes to `%LocalAppData%\ParadisCapture\Logs`. Logs are capped at 5 MB
each with the 10 newest kept, so they never grow without bound. Nothing is ever sent anywhere.

### Crash safety and interrupted recordings

While recording, the file is written as a *fragmented* MP4 — it is complete and playable at
every keyframe (every 2–4 seconds of recording). If Windows crashes or the power goes out, open the leftover
`<name>.recording.mp4`: everything up to the last few seconds is there. Rename it to
`.mp4` if your player is fussy.

When you press Stop, that file is converted to a regular MP4 (no re-encoding, so it's quick) and
the temporary file is deleted.

### Known limitations

- Windows 10 version 2004 (build 19041) or later; built and intended for Windows 11. 64-bit only.
- **Some windows can't be captured.** DRM-protected players and some anti-cheat-protected games
  deliberately refuse screen capture; Windows gives us black frames. Record the monitor instead,
  or expect black.
- **A minimized window produces no frames.** Windows doesn't render it, so the recording shows
  black for as long as it stays minimized. Audio keeps recording normally.
- **A region must be inside one monitor.** Dragging across a monitor edge isn't supported, since
  the region is cropped from that one display.
- Changing a monitor's resolution, or disconnecting it, invalidates a selected region or monitor
  target; pick it again.
- **A capture border may be visible.** Windows draws a yellow border around what's being
  captured. Paradis Capture asks Windows to turn it off at startup; if your Windows version
  doesn't allow that, the border is cosmetic and isn't recorded.
- **No webcam, no editing, no screenshots.** Not planned.
- **Per-application audio** (recording only Teams, not a Discord ping) is not implemented yet —
  see [Not built yet](#not-built-yet).
- Resolutions above 4096 pixels in either direction are scaled down, because that's where
  hardware H.264 encoders stop.

---

## For developers

### Requirements

- **.NET 10 SDK** (`dotnet --version` ≥ 10.0)
- **Windows 11** (or Windows 10 2004+) to run it; Visual Studio 2022/2026 with the *.NET desktop
  development* workload is the comfortable option, but the `dotnet` CLI is enough.
- No external tools. No FFmpeg. Everything comes from Windows itself.

The projects compile on non-Windows machines too (`EnableWindowsTargeting`), which is useful for
CI; the app obviously only runs on Windows.

### Build and run

```powershell
git clone <this repo>
cd ParadisCapture
dotnet build
dotnet run --project src/ParadisCapture
```

Or open `ParadisCapture.sln` in Visual Studio and press F5.

### Tests

```powershell
dotnet test
```

49 tests covering the parts where a bug is invisible until you watch a three-hour recording:
pause/resume timeline maths, audio clock drift over a simulated 3 hours at several device rates,
silence gaps in loopback capture, surround downmixing, PCM formats, output sizing, file naming and the quality presets.
They are plain .NET and run on any OS.

### Publish

```powershell
dotnet publish src/ParadisCapture -p:PublishProfile=win-x64-self-contained
```

Produces a single self-contained `publish/win-x64/ParadisCapture.exe` (~75 MB, version 1.0.2) that runs on a PC
with no .NET installed. For a smaller, framework-dependent build:

```powershell
dotnet publish src/ParadisCapture -c Release -r win-x64 --self-contained false
```

### Project structure

```
ParadisCapture.sln
Directory.Build.props              shared build settings
src/
  ParadisCapture.Core/             platform-neutral recording logic (unit-tested, no Windows APIs)
    Timing/RecordingClock.cs       the single time base; pause/resume maths
    Audio/AudioRing.cs             lock-protected ring addressed by absolute frame position
    Audio/SampleConverter.cs       any PCM/float capture format -> stereo float, surround downmix
    Audio/AudioSourceTimeline.cs   places packets on the timeline; corrects device clock drift
    Audio/AudioMixer.cs            sums sources, emits 16-bit PCM blocks on the clock
    Video/VideoGeometry.cs         output sizing, letterboxing, region normalisation
    Video/FrameTiming.cs           constant-frame-rate schedule, frame selection
    Video/EncoderSettings.cs       quality presets: bitrate, keyframe interval, audio bitrate
    Util/FileNaming.cs             file names from window titles, uniqueness
    Util/FriendlyErrors.cs         HRESULT -> one sentence a person can act on
  ParadisCapture/                  the Windows application
    Interop/                       the handful of P/Invoke and WinRT-COM bridges needed
    Capture/GraphicsDevice.cs      the one D3D11 device shared by capture, conversion, encoder
    Capture/ScreenCaptureSource.cs Windows.Graphics.Capture session -> timestamped GPU frames
    Capture/MonitorInfo.cs         displays in physical pixels, with per-monitor DPI
    Capture/CaptureTarget.cs       window / monitor / region, and how each resolves
    Capture/CapturePickerService.cs  Windows' own "choose what to share" picker
    Video/VideoConverter.cs        GPU colour conversion, scaling and letterboxing (NV12)
    Audio/WasapiCaptureSource.cs   one WASAPI endpoint (loopback or mic) with reconnection
    Encoding/MediaWriter.cs        Media Foundation sink writer: H.264 + AAC in MP4
    Encoding/VideoFrameEncoder.cs  GPU-texture encoding, with a system-memory fallback
    Encoding/Remuxer.cs            fragmented MP4 -> regular MP4, no re-encoding
    Recording/RecordingSession.cs  one recording: owns the pipeline and its threads
    Services/                      settings, logging, hotkeys, version info
    UI/                            main window, wordmark, recording bar, region overlay, settings, tray
    Assets/                        app.ico, recording.ico (tray), logo-256.png
tests/
  ParadisCapture.Core.Tests/       xUnit tests for ParadisCapture.Core
```

### Windows APIs used

| Job | API |
| --- | --- |
| Screen / window capture | `Windows.Graphics.Capture` (WinRT) + Direct3D 11 |
| Colour conversion, scaling, letterboxing | `ID3D11VideoProcessor` (GPU) |
| H.264 and AAC encoding, MP4 muxing | Media Foundation Sink Writer |
| Final container conversion | Media Foundation Source Reader (passthrough) |
| Computer audio | WASAPI loopback capture (shared mode) |
| Microphone | WASAPI capture (shared mode) |
| Global hotkeys | `RegisterHotKey` |
| Keeping the recording bar out of recordings | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |
| Per-monitor DPI | `GetDpiForMonitor`, PerMonitorV2 in the app manifest |
| Frame pacing | `CreateWaitableTimerEx(HIGH_RESOLUTION)` |

Three NuGet packages, all permissive — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

### How it fits together

```
Windows.Graphics.Capture ──▶ ScreenCaptureSource ──▶ VideoConverter ──▶ VideoFrameEncoder ──┐
   (GPU textures, QPC          (ring of timestamped    (BGRA→NV12,        (GPU texture to     │
    timestamps)                 GPU frames)             letterbox)         the encoder)       │
                                                                                             ▼
                                              RecordingClock ◀── one time base ──▶     MediaWriter
                                                                                      (H.264 + AAC,
WASAPI loopback  ──┐                                                                    MP4 on disk)
                   ├─▶ AudioSourceTimeline ──▶ AudioRing ──▶ AudioMixer ─────────────────┘
WASAPI microphone ─┘      (drift correction)                 (clock-driven)
```

The design decisions behind this — why there's one clock, why fragmented MP4, why the GPU path —
are in [DECISIONS.md](DECISIONS.md).

### What's verified and what isn't

The recording engine (window, monitor and region capture, computer audio, pause/resume and A/V
sync) and version 1.0.0 as a whole have been tested on real Windows hardware, including a
76-minute gameplay recording. Version 1.0.1 changes only the Settings styling, the quality
presets (Compact and Standard are bit-for-bit unchanged) and the About view; capture, audio,
timing and encoding code are untouched. Version 1.0.2 only narrows which window is hidden from
captures. See [DECISIONS.md](DECISIONS.md#what-to-check-in-102) for what to look at.

### Branding

The wordmark reads PARADIS**E** CAPTURE with a red, hand-drawn **Z** over the final E: the E
is crossed out (the author's surname is Paradis, not Paradise), and the overlapping E/Z is a nod
to Paradis EZ Utilities. It is drawn in XAML with Segoe UI and vector strokes
(`UI/Wordmark.xaml`), so it needs no image or font files. The product name everywhere else (files, folders, metadata) is plain
"Paradis Capture" / `ParadisCapture`. The icon is a P whose bowl is a recording dot; it was drawn
for this project and is covered by the same licence as the code.

### Paradis EZ Utilities

Paradis Capture is part of [Paradis EZ Utilities](https://github.com/Paradis-EZ-Utilities), a
small collection of free Windows tools. *Small. Simple. Free. EZ.* The product itself is still
called Paradis Capture, and the exe is still `ParadisCapture.exe`.

### Version history

- **1.0.2** — Paradis Capture now shows up in screenshots and other screen recorders. The main
  window, Settings, About and the region overlay are never hidden from capture; only the small
  recording bar is, and only while a recording is running. The main window now hides just
  before recording starts rather than just after.
- **1.0.1** — Readable dark dropdowns, tooltips and menus in Settings. New **Maximum** preset;
  **High** retuned to sit between Standard and Maximum; Standard and Compact unchanged. Device
  dropdowns grey out when their audio source is off. About now shows Paradis EZ Utilities and a
  **More from Paradis EZ Utilities** link. The wordmark's scratched-out E is now an overlapping
  E/Z mark.
- **1.0.0** — Renamed from Simple Recorder; Compact / Standard / High presets; new icon and
  wordmark.

### Not built yet

- **Per-application audio capture** (record only the app you're recording). Windows 10 2004+
  supports it via `ActivateAudioInterfaceAsync` with
  `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. It would slot in as another
  `WasapiCaptureSource`-shaped class feeding its own `AudioSourceTimeline`: the mixer already
  takes any number of sources, so nothing else needs to change. Deliberately left out of the
  first version.
- Recovering an interrupted `.recording.mp4` from inside the app (today you rename it by hand).
