# Shadowing Player

Small Visual Studio wrapper app for experimenting with custom player features without modifying mpv internals first.

## How it works

- The app is a C# WinForms project.
- It launches `mpv.exe` and embeds the video output into the app window.
- It controls playback through mpv's JSON IPC pipe.

## Requirements

1. Visual Studio with the .NET desktop development workload.
2. .NET Framework 4.8 Developer Pack.
3. mpv for Windows.

If `mpv.exe` is not on PATH, set `SHADOWING_MPV_PATH` to the full path:

```powershell
setx SHADOWING_MPV_PATH "C:\path\to\mpv.exe"
```

Restart Visual Studio after changing environment variables.

## Run

Open `ShadowingPlayer.sln` in Visual Studio and press F5.

The first version supports:

- Open video
- Play/pause
- Seek backward/forward 5 seconds
- Stop
- Playback speed

Good next features for shadowing practice:

- A/B loop
- Repeat current subtitle line
- Slow down while repeating
- Subtitle capture and notes
- Keyboard shortcuts
