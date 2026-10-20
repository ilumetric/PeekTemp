# PeekTemp

A compact widget for the Windows 11 taskbar. It shows CPU and GPU temperature and load:

```
CPU  52°  12%
GPU  61°  95%
```

The widget is embedded directly in the taskbar, not in the tray. It moves with the taskbar, hides when the taskbar auto-hides, and disappears in fullscreen games.

## Controls

| Action | What it does |
|---|---|
| Click | details panel: power, video memory and graphs for the last 4 minutes (120 samples) |
| Right-click | settings menu |
| Drag | moves the widget along the taskbar; the position is remembered |

The menu has:
- view: two rows or one, background, yellow/red temperature highlighting, °C or °F;
- position: next to the tray or at the left edge;
- refresh rate;
- graphics card selection (the discrete GPU is picked by default);
- interface language: system default, Russian or English;
- start with Windows.

## CPU temperature

GPU temperature and load, as well as CPU load, are read with no extra requirements. **CPU temperature** needs the [PawnIO](https://pawnio.eu/) driver and administrator rights: Ryzen and Core sensors are only accessible from kernel mode.

1. Menu → "Install PawnIO driver…". This runs `winget install --id namazso.PawnIO -e`; the installer asks for UAC confirmation.
2. Menu → "Start with Windows". This creates a Task Scheduler task with highest privileges, so at sign-in the widget starts as administrator without a UAC prompt.
3. When the task exists, launching `PeekTemp.exe` manually also goes through it, without UAC. If there is no task but PawnIO is installed, the app asks for administrator rights. Turn this off with `"AutoElevate": false` in the settings.

If the temperature is unavailable, the widget shows "—" instead of a number, and the reason is shown in the menu and in the details panel.

## Settings

File: `%APPDATA%\PeekTemp\settings.json` (menu → "Open settings file"). Changes are picked up on the fly. Highlight thresholds are in °C:

```json
"CpuWarm": 80, "CpuHot": 90,
"GpuWarm": 75, "GpuHot": 85
```

Log: `%LOCALAPPDATA%\PeekTemp\peektemp.log`.

## Building

Requires the .NET 9 SDK.

```bash
dotnet build src/PeekTemp/PeekTemp.csproj
```

A single executable in `publish\PeekTemp.exe` (the target PC needs the .NET 9 Desktop Runtime):

```bash
powershell -File build.ps1
```

For debugging: `PeekTemp.exe --preview <folder>` takes real readings for about 10 seconds and renders every widget and panel variant to PNG without touching the taskbar (add `--english` for English text). `--no-elevate` starts without asking for administrator rights.

## How it works

- `UI/TaskbarWidget.cs`: a child window of `Shell_TrayWnd` with per-pixel alpha (`WS_EX_LAYERED` + `UpdateLayeredWindow`). Its position is anchored to the left edge of `TrayNotifyWnd` (or to the left edge of the taskbar) plus an offset set by dragging.
- `App.cs`: a hidden host window and the message loop. It handles `TaskbarCreated` (Explorer restart), theme and DPI changes, and checks every second that the widget is still in place.
- `Sensors/`: polls [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) on a background thread. The library uses PawnIO rather than WinRing0, which Defender blocks.
- `Strings.cs`: Russian and English UI strings.
- Drawing is GDI+ into a DIB section; menus are native Win32, so they look like Windows 11 and support dark mode.

## Limitations

- The widget appears only on the primary monitor's taskbar.
- Explorer doesn't know about the widget. With a lot of open windows, their buttons can slide under it; drag the widget away or switch to one row.
- Embedding relies on Windows 11 taskbar internals (`Shell_TrayWnd`, `TrayNotifyWnd`). A major Windows update could break it.
