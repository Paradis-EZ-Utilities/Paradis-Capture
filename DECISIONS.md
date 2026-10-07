# Architecture decisions

Why Paradis Capture is built the way it is, and what the alternatives would have cost. Written for
whoever next changes this code (including me, later).

---

## 1. One clock for everything

**Decision.** A single `RecordingClock` defines the output timeline. Every timestamp in the
recording — each video frame, each audio sample — is derived from it. Capture timestamps
(`Direct3D11CaptureFrame.SystemRelativeTime` and WASAPI's QPC packet positions) are both
QueryPerformanceCounter values in 100 ns units, so they share one origin and are converted into
"recording time" by one function.

**Why.** Audio/video drift over long recordings is the classic failure of home-made recorders, and
it almost always comes from audio and video being timestamped against different clocks: the audio
device's crystal for audio, the compositor or wall clock for video. Over a 3-hour lecture, a 100
ppm mismatch is about a second of lip-sync error.

With one clock, drift is structurally impossible: audio and video can only be wrong *relative to
the clock*, never relative to each other. The remaining job — making a device that thinks it runs
at 48000.5 Hz fit into a 48000 Hz track — is handled in exactly one place (below).

**Cost.** Pause/resume maths lives in the clock and is fiddly (data captured *inside* a pause has
to be partially skipped, not just shifted). That logic is small and heavily unit-tested rather
than spread through the pipeline.

## 2. Audio is resampled to follow the clock, not the device

**Decision.** `AudioSourceTimeline` compares where a packet's timestamp says it belongs against
where the previous packet ended, smooths that error, and nudges the resampling ratio by at most
±0.2 % (±2000 ppm) to close it. Errors above 100 ms are treated as a discontinuity and re-synced
outright.

**Why.** Audio clocks are never exactly what they claim. A device running 150 ppm fast delivers
~26 extra samples per minute, ~4700 over three hours — a tenth of a second of accumulated error
per hour if you just append packets back-to-back. Appending is what most simple recorders do, and
it's why their long recordings drift.

Correcting continuously by a tiny amount is inaudible (0.2 % is about 3.5 cents of pitch, and the
correction normally sits in the tens of ppm). The alternative — inserting or dropping samples when
the error grows — produces an audible click every few minutes.

**Verified.** `AudioSyncTests` simulates 3-hour recordings at +150 ppm, −200 ppm and a 44.1 kHz
device, with timestamp jitter, and asserts the timing error never exceeds 3 ms, that there are no
hard re-syncs, and that no sample-to-sample discontinuity large enough to click ever appears.

## 3. The mixer is clocked, not data-driven

**Decision.** `AudioMixer` emits audio for the interval the *clock* has advanced through, not for
"however much audio arrived". Sources write into ring buffers addressed by absolute output frame
position; anything never written reads as silence.

**Why.** This makes three awkward cases free:

- **WASAPI loopback delivers nothing while nothing is playing.** Not silence — no packets at all.
  A data-driven mixer would produce a short audio track and desynchronised video. Here the
  unwritten region is silence at exactly the right place, and when sound resumes it lands at its
  true timestamp.
- **A microphone disconnects mid-recording.** That source simply stops writing; the recording
  continues with the other source and silence for the missing one.
- **Two sources with independent clocks** can't fight over who drives the output rate.

The audio track therefore always contains exactly as many samples as the recording is long, which
is what keeps sync exact rather than approximate.

**Trade-off.** The mixer must stay slightly behind real time (250 ms) so late packets still land
in their slots. That's encoder latency, not output latency: it doesn't shift sync, and the file is
identical. If a source's writes ever did fall behind the mixer, the timeline re-syncs forward
instead of silently discarding the audio — losing sound is much worse than one small jump.

## 4. Constant frame rate output, with the newest frame chosen per slot

**Decision.** Video is written at a constant 30 or 60 FPS. Frame *k* is timestamped at exactly
*k*/fps (integer maths, so no rounding accumulates). For each slot, the encoder picks the captured
frame whose capture time is the newest one not later than that slot's timestamp.

**Why.** `Windows.Graphics.Capture` is change-driven: a static lecture slide produces no frames
for minutes, and a game may produce 240 a second. Writing frames as they arrive gives a
variable-frame-rate file, which is correct but which some editors and players handle badly, and
which makes a long static recording's timeline fragile.

Choosing by capture time rather than arrival time means a frame that took a while to reach us
still lands where it actually happened. Encoding the previous frame again when nothing changed
costs almost nothing: H.264 compresses an identical frame to a few bytes.

**Trade-off.** A 60 Hz source recorded at 30 FPS has its frames selected, not blended. That's
standard and what you want for screen content.

## 5. Everything stays on the GPU

**Decision.** `Windows.Graphics.Capture` → `ID3D11VideoProcessor` → Media Foundation hardware
encoder, all on one shared D3D11 device. No frame is read back to system memory on the normal
path.

**Why.** Dylan's brief calls for low overhead while recording games and playtests. A 1080p60
recording is 500 MB/s of raw BGRA; copying that to CPU memory and back is the single biggest cost
a naive recorder pays, and it's what makes some recorders unusable while gaming.

One device for all three stages means no cross-device sharing and no synchronisation beyond
D3D11's multithread protection plus one lock around our own multi-step context use.

**The video processor earns its place** by doing four things in a single GPU operation:

- BGRA → NV12 colour conversion (what H.264 needs),
- BT.709 limited-range tagging, which is what players expect (getting this wrong is the usual
  cause of "my recording looks washed out"),
- letterboxing when a window is resized mid-recording — the output size is fixed at Record time,
  so a resized window is fitted into it with black bars rather than stretched,
- downscaling when a source exceeds the ~4096 px hardware encoder limit.

A pixel shader could do the same, but the video processor is what the driver optimises for video
and needs no shader compilation.

**Fallbacks.** `MediaWriter.Create` tries three paths in order: GPU textures, system memory with
hardware encoding, then software encoding. Some drivers reject texture samples, and Windows "N"
editions have no H.264 encoder at all until the Media Feature Pack is installed — in which case
the user gets a sentence saying so rather than a COM error.

## 6. Media Foundation, not FFmpeg

**Decision.** H.264, AAC and MP4 all come from Windows.

**Why.** Dylan asked for this explicitly, and the reasons hold up: no redistribution or licensing
complexity, no 80 MB of native binaries, hardware encoding on every modern GPU for free, and
nothing to keep updated. The output is ordinary H.264 High profile + AAC-LC in MP4 — the same
thing FFmpeg would produce, from the same hardware encoder.

**Cost.** Less control over the encoder, and quality depends on the GPU vendor's encoder rather
than x264. For screen recording at these bitrates that difference is not visible. Media
Foundation's error reporting is also raw HRESULTs, which is why `FriendlyErrors` exists.

## 7. Four named presets; bitrate is computed, never asked about

**Decision.** Settings offers **Compact**, **Standard** (the default), **High** and (since
1.0.1) **Maximum**.
`EncoderSettings` turns the preset, the output size and the frame rate into a mean video bitrate,
a keyframe interval and an AAC bitrate. The encoder runs in unconstrained VBR at that mean.

| | Video at 1080p30 | Scaling with size | Keyframe | Audio |
| --- | --- | --- | --- | --- |
| Compact | 0.9 Mbit/s | `(pixels / 1080p)^0.8` | 4 s | 96 kbit/s |
| Standard | 1.8 Mbit/s | `(pixels / 1080p)^0.8` | 4 s | 128 kbit/s |
| High | 5.0 Mbit/s | linear (0.08 bits/pixel/frame) | 2 s | 192 kbit/s |
| Maximum | 11.2 Mbit/s | linear (0.18 bits/pixel/frame) | 2 s | 192 kbit/s |

All four weight frame rate as `(fps / 30)^0.75`, because consecutive frames at 60 FPS are more
alike than at 30.

**Why.** "I should not need to understand bitrate to get a good recording", and real recordings
from v0.1 (a single High-ish setting) ran 1+ GB per hour for a window, which is too much for a
three-hour class.

- **Resolution never drops.** Making Compact smaller by downscaling would blur exactly what a
  lecture recording is for: slide text, code, UI labels. Screen content compresses extremely well
  when it isn't moving, so a low mean bitrate costs little legibility. What it does cost is
  sharpness during fast motion (scrolling, embedded video), which recovers within a second or two.
- **Unconstrained VBR** is what makes a low mean bitrate workable: a still slide uses a fraction
  of it, a scroll briefly uses more. A constant bitrate would waste bits on still slides and
  starve scrolling.
- **Sub-linear size scaling for Compact/Standard.** A 4K screen of text doesn't carry four times
  the information of a 1080p one, so they scale by `pixels^0.8`. High and Maximum are for
  motion, where every pixel changes, so they scale linearly with the pixel count.
- **1.0.1 retune.** Real-world 1.0.0 use showed Standard is right for the main use (1080p30
  gameplay ~0.9 GB/h, regions ~0.5 GB/h, text readable), so Compact and Standard are kept
  bit-for-bit (a unit test pins the 1.0.0 formula). Users wanted a genuinely high-fidelity
  option like the original prototype, whose default was 0.12 and top level ("Very high") 0.18
  bits/pixel/frame. **Maximum** takes the prototype's top level, 0.18, so it is at least as
  good as anything the prototype recorded. **High** moved from 0.12 to 0.08 (the prototype's
  lowest level, ~2.8× Standard at 1080p30) so the four presets step up evenly
  (1.8 → 5.0 → 11.2 Mbit/s) instead of High and Maximum being near neighbours. A 1.0.0 settings
  file that says High now gets the new High.
- **Longer keyframe interval for Compact/Standard.** On a mostly still screen, keyframes are a
  large share of the file; 4 s instead of 2 s saves a lot. The costs are coarser seeking and up to
  ~4 s lost if the PC dies mid-recording (see §8), both fine for a lecture.
- **Floors** (0.3 / 0.6 / 1.0 / 2.0 Mbit/s) keep a tiny region from getting a uselessly small bitrate.
- **AAC at 96 / 128 / 192 kbit/s**: the Media Foundation AAC encoder accepts only 96, 128, 160 and
  192 kbit/s. If it ever rejects the lower rates, `MediaWriter` falls back to 192 kbit/s, which
  every earlier build used.

**Alternative considered: quality-based VBR** (`eAVEncCommonRateControlMode_Quality` with
`CODECAPI_AVEncCommonQuality`). It would track content better in theory, but hardware encoders
differ in whether they support it and in what a given quality number means, so file sizes would
vary unpredictably between PCs; the existing rate-control path is already proven on real
hardware. A mean-bitrate ceiling keeps "about this many GB per hour" true everywhere. This was
reconsidered for Maximum in 1.0.1 and rejected for the same reason: if a GPU's encoder rejects
the quality mode, `MediaWriter` falls back to the encoder's defaults, which would silently make
"Maximum" the worst preset on that PC.

## 8. Fragmented MP4 while recording, regular MP4 at the end

**Decision.** Record into `<name>.recording.mp4` as a fragmented MP4, then convert
(stream-copy, no re-encoding) into `<name>.mp4` when the user stops.

**Why.** A regular MP4's index is written when the file is closed. Lose power during a 3-hour
recording and a regular MP4 is a 10 GB file with no index — recoverable only with repair tools. A
fragmented MP4 is valid and playable at every fragment boundary (one per keyframe: 2 s on High,
4 s on Compact and Standard), so the worst case is losing the last few seconds. That was worth having for
recordings this long.

Converting at the end gives the fully compatible file every editor likes. It's pure I/O at disk
speed, and it never re-encodes, so quality is untouched.

**Trade-offs, and how they're handled.**

- It needs room for a second copy. If free space is short, the fragmented file is kept under the
  final name (it plays fine) and the user is told.
- If conversion fails for any other reason, same fallback — the recording is never lost to a
  failed conversion step.
- The temporary file is visibly different from a finished recording, which is deliberate: a
  leftover `.recording.mp4` tells you something was interrupted.

## 9. Region selection in physical pixels

**Decision.** The region overlay places one window per monitor using `SetWindowPos` with physical
pixel coordinates, converts mouse positions to physical pixels using that monitor's own scale
factor, and the app manifest declares PerMonitorV2 DPI awareness.

**Why.** WPF works in device-independent units relative to the primary display. On a 150 % laptop
screen next to a 100 % external monitor, a region selected in DIPs is wrong on at least one of
them — and "the region I dragged isn't the region that got recorded" is the bug a user notices
immediately. Capture and cropping happen in physical pixels, so selection does too.

A region is cropped from one monitor's capture, so a selection can't span two monitors. Cropping
also means the recording is the source's own pixels at 1:1 — no scaling, no softness.

## 10. Only the recording bar is excluded from capture, and only while recording

**Decision.** The recording bar sets `WDA_EXCLUDEFROMCAPTURE` when it is created (it only exists
while a recording runs) and resets it to `WDA_NONE` when the recording ends. Every other window
(main window, Settings, About, the region overlay, dialogs) is never excluded. The main window
hides itself just before the recording starts and comes back when it ends.

**Why.** The brief says the recorder's own controls must not appear in the recording. Hiding the
bar would work but leaves the user with nothing to click. `WDA_EXCLUDEFROMCAPTURE` makes a window
invisible to capture while fully visible on screen, so the bar can sit on top of what's being
recorded without being in it. The tray option stays, for people who want nothing on screen at all.

Up to 1.0.1 the main window and region overlay were excluded too. That made the app invisible in
screenshots and other recorders, which made demos and bug reports impossible (1.0.2 fix). The
main window doesn't need the flag because it is hidden during recording anyway, and the overlay
only exists before a recording starts.

Where the Windows build doesn't support the flag, the bar says so and suggests the tray instead
rather than silently recording itself.

## 11. A separate platform-neutral core project

**Decision.** `ParadisCapture.Core` holds the timing, audio timeline, mixing, geometry, naming and
error-mapping logic and references no Windows APIs. `ParadisCapture` holds everything that touches
Windows.

**Why.** The bugs that would hurt most here — drift over hours, a pause that leaves a gap,
a region off by a scale factor, audio that silently stops — are all in logic that doesn't need a
screen to test. Keeping it in a plain library means it can be tested exhaustively and quickly
(3-hour recordings simulate in seconds), and that a change to the timing can be verified without
recording anything.

It also drew a sharp line: Windows APIs do capture, conversion and encoding; our own code does
timing and decisions.

## 12. Dark templates for every control that pops up (1.0.1)

**Decision.** `Theme.xaml` gives `ComboBox`, `ComboBoxItem`, `ToolTip`, `ContextMenu` and
`MenuItem` their own dark templates.

**Why.** The theme's implicit `TextBlock` style makes all text light. WPF applies it to text
inside the stock control templates too, so the stock ComboBox (light box, light drop-down) showed
light text on a light background: the 1.0.0 Settings dropdowns were close to blank. The same
applied to tooltips and the text-box right-click menu. Scoping the TextBlock style instead would
mean touching every window; giving the handful of light-by-default controls dark surfaces fixes
the cause in one file. Disabled dropdowns dim to 60% opacity, so they read as inactive but stay
legible; the audio device dropdowns are disabled while their source is switched off.

## 13. Threads

**Decision.** Capture is driven by WGC's own free-threaded frame-arrived callback; each audio
endpoint gets a dedicated thread; one thread paces and encodes video; one thread mixes and writes
audio; the UI thread does none of it.

**Why.** The requirement is that nothing blocks anything else and the UI stays responsive. Giving
the video pacing loop its own thread (with a high-resolution waitable timer) is what keeps frame
timing even — a shared thread pool or a UI timer would jitter under load. Audio capture threads
run at above-normal priority because WASAPI buffers are small and overruns are audible.

Shared state is deliberately tiny: ring buffers with a lock, a lock around our own use of the
D3D11 immediate context, and interlocked fields for state and counters.

---

## What to check in 1.0.2

1.0.2 changes only which window is excluded from capture (`MainWindow`, `RegionSelectorWindow`,
`RecordingBarWindow`) plus hiding the main window just before, instead of just after, recording
starts. Capture, audio, timing and encoding code are untouched.

1. **Idle.** Win+Shift+S or another recorder shows the main window, Settings and About normally.
2. **Recording.** The recording bar is on screen but not in the recording; the main window is
   hidden and doesn't appear in the first frames.
3. **After stopping.** Everything is capturable again.

## What to check in 1.0.1

1.0.0 was tested extensively on real Windows (window, monitor and region capture, system audio,
pause/resume, A/V sync, a 76-minute 1080p30 gameplay recording). 1.0.1 changes only:

1. **Settings styling.** Frame rate, Quality, output device and microphone dropdowns: closed,
   open, hovered, selected and disabled (turn off "Record computer audio" and its device
   dropdown greys out). Tooltips on the main window and the right-click menu on the save-folder
   box should also be dark with light text.
2. **Presets.** Compact and Standard produce identical encoder settings to 1.0.0. High is now
   5.0 Mbit/s at 1080p30 (was 7.5) and Maximum is new at 11.2 Mbit/s. The log line
   `Output 1920x1080 @ 30 fps, Maximum: video 11.20 Mbit/s, audio 192 kbit/s, keyframe every 60
   frames…` shows what was used. The capture, timing, audio and encoding code is unchanged.
3. **About.** Version 1.0.1, the Paradis EZ Utilities block, and **More from Paradis EZ
   Utilities**, which opens https://github.com/Paradis-EZ-Utilities in the default browser. If
   that fails, the address is shown under the link and copied to the clipboard.
4. **Version resource.** File and product version 1.0.1.
5. **Wordmark.** The scratched-out E in the main window header and in About is now a red Z
   drawn over the E (`UI/Wordmark.xaml`), sized to the E's cap height. Check it reads as both a
   crossed-out E and "EZ" at both sizes, and that the Z doesn't touch "CAPTURE".

## What to check in 1.0.0

The recording engine was tested on real Windows hardware before 1.0.0: window, monitor and region
capture, computer audio, pause/resume and A/V sync all behaved as intended.

1.0.0 changed only these things, all compiled and unit-tested but not yet run on Windows:

1. **Quality presets.** The bitrate, keyframe interval and AAC bitrate passed to the encoder now
   come from the preset. The capture, timing and audio paths are untouched. The log line
   `Output 1920x1080 @ 30 fps, Compact: video 0.90 Mbit/s, audio 96 kbit/s, keyframe every 120
   frames…` shows what was used. If the AAC encoder refused 96 or 128 kbit/s the log says
   `AAC encoder rejected … using 192 kbit/s`.
2. **Rename.** Executable, assembly, namespaces, window titles, the single-instance mutex, and
   the settings/log folder (`%LocalAppData%\ParadisCapture`). On first launch, settings from
   `%LocalAppData%\SimpleRecorder` are imported (save folder, devices, hotkeys, frame rate); the
   quality starts at Standard because the old preset names don't map onto the new ones.
3. **Branding.** The wordmark (`UI/Wordmark.xaml`) on the main window and in Settings → About,
   the new `app.ico` (exe, taskbar, Alt-Tab, shortcuts) and `recording.ico` (tray while
   recording), and the version resource (1.0.0, "Paradis Capture", D.Y. Paradis).
4. **Settings window.** Now scrolls if it's taller than the screen, and has an About view.

The log at `%LocalAppData%\ParadisCapture\Logs` records the adapter, the chosen encoder path, the
preset, output size and bitrates, every warning, skipped frames and encoder drops.
