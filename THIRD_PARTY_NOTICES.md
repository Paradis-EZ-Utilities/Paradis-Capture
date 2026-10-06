# Third-party notices

Paradis Capture uses three NuGet packages. All are permissively licensed (MIT), and all of their
transitive dependencies are too. Nothing here is paid, subscription-based, trial, cloud-hosted or
licence-key gated, and nothing phones home.

Everything else — screen capture, video and audio encoding, MP4 muxing, audio capture, the UI —
comes from Windows itself or from the .NET runtime.

---

## Direct dependencies

### NAudio.Wasapi 2.2.1 — MIT
Author: Mark Heath · https://github.com/naudio/NAudio

Managed wrappers for the Windows Core Audio (WASAPI) interfaces. Used to enumerate audio
endpoints and to capture computer audio (loopback) and the microphone.

Pulls in **NAudio.Core 2.2.1** (MIT, same project), whose `WdlResampler` — originally from Cockos'
WDL, also MIT — performs the sample-rate conversion and the small continuous rate corrections that
keep long recordings in sync.

### Vortice.MediaFoundation 3.8.3 — MIT
Author: Amer Koleci · https://github.com/amerkoleci/Vortice.Windows

Managed bindings for Media Foundation. Used for H.264 and AAC encoding, MP4 muxing (the Sink
Writer), and the final stream-copy conversion (the Source Reader).

### Vortice.Direct3D11 3.8.3 — MIT
Author: Amer Koleci · https://github.com/amerkoleci/Vortice.Windows

Managed bindings for Direct3D 11, including `ID3D11VideoProcessor`. Used for the GPU capture,
colour-conversion and scaling pipeline.

## Transitive dependencies

All MIT, all from the two projects above:

| Package | Version | Licence |
| --- | --- | --- |
| NAudio.Core | 2.2.1 | MIT |
| Vortice.DirectX | 3.8.3 | MIT |
| Vortice.DXGI | 3.8.3 | MIT |
| Vortice.Mathematics | 2.1.0 | MIT |
| SharpGen.Runtime | 2.4.2-beta | MIT |
| SharpGen.Runtime.COM | 2.4.2-beta | MIT |

## Microsoft components

- **.NET 10** and **Windows Presentation Foundation** — MIT (https://github.com/dotnet/runtime,
  https://github.com/dotnet/wpf). A self-contained build redistributes the .NET runtime, which the
  .NET licence permits.
- **Microsoft.Windows.SDK.NET.Ref** — the Windows SDK projection used at build time to call
  `Windows.Graphics.Capture`. Covered by the Windows SDK licence; it is a reference assembly and
  the API itself is part of Windows.
- **Windows platform APIs** — Windows.Graphics.Capture, Direct3D 11, Media Foundation, WASAPI,
  Win32. Part of the operating system; nothing is redistributed.

## Licence text (MIT)

Every third-party package listed above is under the MIT licence:

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

Copyright is held by the respective authors named above.

## A note on H.264 and AAC patents

Paradis Capture does not include an H.264 or AAC codec: it calls the encoders that are part of
Windows, as any Windows application may. No codec is redistributed with this application.

## Icon, logo and fonts

The application icon, tray icon and the PARADIS~~E~~ CAPTURE wordmark were made for this project
and are not third-party assets. The wordmark is drawn with Segoe UI, a font that ships with
Windows; no font is bundled with the application.
