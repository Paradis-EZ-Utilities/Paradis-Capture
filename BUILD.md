# Build and publish

Quick reference. Full details, including project layout, are in [README.md](README.md).

## Prerequisites

- **.NET 10 SDK** — check with `dotnet --version` (needs 10.0 or later).
  Install from https://dotnet.microsoft.com/download/dotnet/10.0, or with
  `winget install Microsoft.DotNet.SDK.10`.
- **Windows 10 version 2004 (build 19041) or later** to run it. Windows 11 is the target.
- Optional: Visual Studio 2022 (17.14+) or 2026 with the *.NET desktop development* workload,
  for F5 debugging and the designer.

No other tools, SDKs or redistributables. No FFmpeg.

## Build

```powershell
dotnet build
```

Or open `ParadisCapture.sln` and build in Visual Studio.

## Run

```powershell
dotnet run --project src/ParadisCapture
```

## Test

```powershell
dotnet test
```

Runs the 49 unit tests for `ParadisCapture.Core`. They take a couple of minutes, mostly in the
simulated three-hour audio-drift tests. They need no Windows and no audio or video hardware.

## Publish a self-contained executable

This is the one to hand to someone who doesn't have .NET installed:

```powershell
dotnet publish src/ParadisCapture -p:PublishProfile=win-x64-self-contained
```

Output: `publish/win-x64/ParadisCapture.exe` — a single ~75 MB file, x64, no installer, no
dependencies. Copy it anywhere and run it.

## Publish a small, framework-dependent executable

Needs the .NET 10 Desktop Runtime on the target PC, but is a few hundred KB:

```powershell
dotnet publish src/ParadisCapture -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Building on a non-Windows machine

`dotnet build` and `dotnet test` work on Linux and macOS: the app project sets
`EnableWindowsTargeting`, so it compiles against the Windows reference assemblies. The resulting
executable only runs on Windows.

## Notes

- The app is x64 only. ARM64 would need `-r win-arm64` and is untested.
- `Platforms` is set to `x64`; Visual Studio's configuration dropdown should say **x64**, not
  *Any CPU*.
- The published app requires no administrator rights and no special capabilities.
