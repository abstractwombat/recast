# Recast Windows Recorder (C#)

A minimal Windows-native recorder that exposes the VNC control/session API and a local control UI. It launches Chrome sessions (manual/automated stub) via Selenium and ensures TightVNC is running so you can view/control the desktop.

Status: MVP for VNC + session control. Audio/HLS recording and management-server integration are not yet implemented here.

## Prerequisites

- Windows 11
- .NET SDK 8.x: https://dotnet.microsoft.com/download
- Google Chrome (stable)
- TightVNC Server (or compatible). Configure your password and allow connections.
  - Optional: set environment `VNC_PORT=5901` to force a port.
  - Otherwise, the recorder reads `RfbPort` from registry:
    - `HKLM\SOFTWARE\TightVNC\Server` or `HKLM\SOFTWARE\WOW6432Node\TightVNC\Server`

## Build and run

```powershell
# From the repo root
cd windows_recorder
# Restore/build
dotnet build -c Release
# Run (listens on 0.0.0.0:5001)
dotnet run -c Release
```

Open the control UI:

- http://localhost:5001/control

## Endpoints

- `GET /status` – simple recorder status
- `GET /control` – control page UI
- `GET /api/vnc/status` – VNC + controller status
- `POST /api/vnc/start` – ensure TightVNC is running
- `POST /api/vnc/stop` – stop TightVNC (best effort)
- `POST /api/session/launch` – body:
  ```json
  {"controller":"youtube","url":"https://www.youtube.com","mode":"manual","keep_open_on_error":true}
  ```
- `POST /api/session/stop` – body: `{ "controller": "youtube" }`

## Notes

- The app binds to port 5001 (same as the Python recorder UI) for convenience.
- `SessionManager` uses Selenium Manager to auto-resolve chromedriver for your Chrome.
- The `automated` mode is a stub. You can add site-specific logic (e.g., YouTube fullscreen/unmute) similar to your Python controllers.
- `VncManager` tries:
  1) Start TightVNC service (`sc start "tvnserver"` / `"TightVNC Server"`).
  2) Launch `tvnserver.exe -run` in user session when service control fails.
  3) Detect port from registry or env.

## Running as a Windows service (optional)

Use NSSM or a scheduled task for now, e.g.:

```powershell
nssm install RecastWindowsRecorder "C:\Program Files\dotnet\dotnet.exe" "C:\path\to\recast-recorder-windows\bin\Release\net8.0\Recast.WindowsRecorder.dll"
```

## Roadmap ideas

- Implement management server integration (register/heartbeat/job polling) to match the Linux recorder.
- Add recording (ffmpeg) and audio capture (WASAPI) for local MP4/HLS.
- Per-controller automation similar to Python (YouTube/VideoJS).
- Expose LAN-friendly connect hints and optional CORS settings if reverse-proxied.
