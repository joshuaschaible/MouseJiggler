# Mouse Jiggler

A tiny Windows 11 utility that nudges your mouse every N seconds so games and apps don't think you're idle. No installer, no dependencies — compiles to a single `.exe` using tools already built into Windows.

## Files

| File | Purpose |
|------|---------|
| `MouseJiggler.cs` | C# WinForms source code |
| `build.bat` | Compiles the source into `MouseJiggler.exe` |
| `MouseJiggler.exe` | The compiled app (created after you run `build.bat`) |

## Build

1. Copy the `MouseJiggler` folder to your Windows 11 PC.
2. Double-click `build.bat`.
3. `MouseJiggler.exe` appears in the same folder.

The build uses `csc.exe` from `C:\Windows\Microsoft.NET\Framework\v4.0.30319\`, which ships with every Windows 11 install. No .NET SDK, Visual Studio, or other tooling required.

## Run

Double-click `MouseJiggler.exe`.

On first launch, Windows SmartScreen may warn that the publisher is unknown (normal for self-built unsigned executables). Click **More info → Run anyway**. It won't warn again.

## Usage

- **Power button** — the big glowing ring toggles jiggling on and off (it pulses while active).
- **Status label** — shows `ACTIVE` (cyan) or `IDLE` (gray).
- **Interval (s)** — how often the mouse is nudged. Default is 30 seconds; range is 1–600. Click **−**/**+** to step, or click-and-hold to scroll fast.
- **Distance (px)** — how far each nudge moves. Default is 1 pixel.
- **Hotkey** — click the pill, then press a key combo (needs Ctrl/Alt/Shift) to toggle jiggling globally. Default is Ctrl+Alt+J.
- **Start with Windows** — flip the switch to launch the app hidden in the tray (already jiggling) at login.
- **Title bar** — drag anywhere on it to move the window; **–** minimizes.
- **Close button (X)** — hides the window to the system tray. Jiggling continues if it was on.
- **System tray icon** — right-click for:
  - **Show** — bring the window back.
  - **Toggle** — start/stop without opening the window.
  - **Exit** — fully quit the app.
- Double-clicking the tray icon also shows the window.

## How it works

Every interval, the app sends two relative mouse-move events via the Win32 `SendInput` API: one pixel right, then one pixel back. The cursor ends up exactly where it started, but the OS (and any game or anti-idle system) sees real mouse input. The movement is too small to notice visually.

Because it uses synthetic input rather than just moving the cursor, it works with games that ignore cursor-position changes (`SetCursorPos`) but respond to real input events.

## Customization

Edit `MouseJiggler.cs`, then re-run `build.bat`. Common tweaks:

- **Default interval** — change `intervalBox.Value = 30` in the `MainForm` constructor.
- **Always on top** — change `TopMost = true;` to `false`.
- **Colors** — everything lives in the `Theme` class at the top of the file (background, accents, text). Change `Violet`/`Cyan` to restyle the whole app.

## Troubleshooting

**`Could not find csc.exe`** — your Windows install is missing .NET Framework 4, which is very unusual on Windows 11. Install it via *Settings → Apps → Optional features* or from Microsoft's site.

**Game still kicks me for being idle** — some games detect input only when a window has focus, or only count keyboard input. Try lowering the interval, or minimize the game window without stealing focus.

**Windows Defender flags the exe** — unsigned executables that simulate input are occasionally flagged heuristically. The source is right there in `MouseJiggler.cs`; you built it yourself; you can add a folder exclusion if needed.

## License

Do whatever you want with it.
