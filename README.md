# Mouse Jiggler

A tiny Windows 11 utility that nudges your mouse every N seconds so games and apps don't think you're idle. No installer, no dependencies — compiles to a single `.exe` using tools already built into Windows.

## Files

| File | Purpose |
|------|---------|
| `MouseJiggler.cs` | C# WinForms source code |
| `build.bat` | Compiles the source into `MouseJiggler.exe` |
| `MouseJiggler.exe` | The compiled app (created after you run `build.bat`) |
| `MouseJiggler_icon.png` | High-resolution source artwork for the app icon |
| `make_icon.ps1` | Rebuilds the multi-resolution `.ico` and preview from the source artwork |
| `streamdeck/` | Dedicated Stream Deck plugin, matching power icons, packaging scripts, and tests |

## Build

1. Copy the `MouseJiggler` folder to your Windows 11 PC.
2. Double-click `build.bat`.
3. `MouseJiggler.exe` appears in the same folder.

The build uses `csc.exe` from `C:\Windows\Microsoft.NET\Framework\v4.0.30319\`, which ships with every Windows 11 install. No .NET SDK, Visual Studio, or other tooling required.

## Run

Double-click `MouseJiggler.exe`.

On first launch, Windows SmartScreen may warn that the publisher is unknown (normal for self-built unsigned executables). Click **More info → Run anyway**. It won't warn again.

## Usage

The app automatically follows **Settings → Personalization → Colors → Choose your mode** (the Windows app mode). Light and dark palettes update while the app is running, including when hidden in the tray. The power ring, violet/cyan accents, and animations sit over a softly tinted native Acrylic body where Windows supports it, with an opaque title bar and settings controls for readability. No theme setting is saved by the app.

- **Power button** — the big glowing ring toggles jiggling on and off (it pulses while active).
- **Status label** — shows `ACTIVE` (cyan) or `IDLE` (gray).
- **Interval (s)** — how often the mouse is nudged. Default is 30 seconds; range is 1–600. Click **−**/**+** to step, or click-and-hold to scroll fast.
- **Distance (px)** — how far each nudge moves. Default is 1 pixel.
- **Hotkey** — click the pill, then press a key combo (needs Ctrl/Alt/Shift) to toggle jiggling globally. Default is Ctrl+Alt+J.
- **Start with Windows** — flip the switch to launch the app hidden in the tray (already jiggling) at login.
- **Always on top** — flip the switch to keep the window above other windows. On by default.
- **Pause when active** — holds off nudging until you've been away from the mouse and keyboard for a full interval, so it never fights you mid-drag. On by default; turn it off to jiggle unconditionally.
- **Keep screen awake** — while jiggling, tells Windows not to sleep or blank the display. On by default.
- **Title bar** — drag anywhere on it to move the window; **–** minimizes.
- **Alt + arrow keys** — while Mouse Jiggler has focus, move to the next monitor in that direction. All four arrows support screens arranged beside, above, or below one another. With no screen in that direction, the window stays on its current screen. These shortcuts do not take over arrow keys in other apps.
- **Standard Windows movement** — the custom window retains normal Windows shell styles and title hit testing. This restores the shell behavior needed for **Win + Shift + Left/Right** monitor moves, with position persistence after native movement. Actual dispatch of those Windows shortcuts still needs a manual check. The app remains fixed in size; full maximize and Snap Layout resizing are not supported.
- **Ctrl + Alt + Home** — a global recovery shortcut that shows Mouse Jiggler on the monitor under your pointer, even if hidden, minimized, or left on an unseen screen. If another app has already registered this shortcut, the tray menu reports it as unavailable; use **Show on this monitor** instead.
- **Close button (X)** — hides the window to the system tray. Jiggling continues if it was on.
- **System tray icon** — a filled gradient dot while jiggling, a hollow gray ring when idle. Right-click for:
  - **Show on this monitor** — bring the window to the monitor under your pointer.
  - **Toggle** — start/stop without opening the window.
  - **Exit** — fully quit the app.
- Double-clicking the tray icon also brings the window to the monitor under your pointer.
- **Single instance** — launching the app again brings the existing window to the monitor under your pointer instead of starting a second copy. (`--silent` on an already-running instance exits quietly.)
- **Window position** — where you leave the window is where it reopens. Placement is clamped to a monitor's usable area on startup and when the display arrangement changes, keeping the title bar reachable. Keyboard moves and recovery save the new position.

This repository contains a Windows WinForms app. **Alt** is the Windows equivalent of the requested **Option** modifier (including Option on an Apple keyboard mapped to Alt by Windows); there is no macOS application in this project.

## Stream Deck

The dedicated **Mouse Jiggler → Toggle Mouse Jiggler** action turns jiggling on or off and displays the app's actual state. The power icon is gray for **IDLE**, violet/cyan for **ACTIVE**, and dashed for **OFFLINE**. Changes made through the app, tray, or keyboard shortcut appear on the key within about half a second.

When the app is closed, pressing the key launches it hidden and turns jiggling on. It uses the same saved preferences as the normal app. The button's property inspector also includes a **Test button** control. See [the plugin instructions](streamdeck/README.md) for installation and packaging.

The connection uses a local named pipe restricted to the current Windows user and session. It does not send keyboard shortcuts or require a network listener. The app records connection information and its executable path in `%LOCALAPPDATA%\MouseJiggler\streamdeck.json`; the plugin checks the live connection before showing a running state. `--background` starts hidden and idle for the plugin, while the existing `--silent` Windows-startup option starts hidden and active.

## How it works

Every interval, the app sends a relative mouse-move event via the Win32 `SendInput` API, alternating left and right by the configured distance. The OS (and apps that accept synthetic input) sees mouse input. At the default one-pixel distance, the movement is small.

Because it uses synthetic input rather than just moving the cursor, it works with games that ignore cursor-position changes (`SetCursorPos`) but respond to real input events.

## Customization

Edit `MouseJiggler.cs`, then re-run `build.bat`. Common tweaks:

- **Default interval** — change `pendingInterval = 30` in the `MainForm` field declarations (used until a value is saved).
- **Colors** — everything lives in the `Theme` class at the top of the file (background, accents, text). Edit the light/dark assignments in `Theme.SetPalette()` to restyle the whole app.

## Troubleshooting

**`Could not find csc.exe`** — your Windows install is missing .NET Framework 4, which is very unusual on Windows 11. Install it via *Settings → Apps → Optional features* or from Microsoft's site.

**Game still kicks me for being idle** — some games detect input only when a window has focus, or only count keyboard input. Try lowering the interval, or minimize the game window without stealing focus.

**Windows Defender flags the exe** — unsigned executables that simulate input are occasionally flagged heuristically. The source is right there in `MouseJiggler.cs`; you built it yourself; you can add a folder exclusion if needed.

## License

Do whatever you want with it.

## Theme validation

On Windows, rebuild with `build.bat`, then check both light and dark app modes:

1. Launch in each mode and confirm the window, text, power ring, settings controls, and tray menu are readable.
2. Switch Windows app mode while the app is idle, then while active; the palette should update without changing settings or stopping jiggling.
3. Hide the app to the tray, switch mode, open the tray menu, and show the window again; both should use the new palette.
4. Check the close-button hover (white glyph on red) and the switches in both modes.

On **Windows 11 build 22621 or later**, the app requests native Desktop Acrylic. All custom-painted content, including labels, is first rendered into explicit premultiplied 32-bit surfaces and copied without discarding alpha. This replaces the earlier mixture of transparent-black GDI buffers and native label rendering that could leave dark blocks, halos, or inconsistent backgrounds. A light/dark tint keeps the material subtle, with solid settings surfaces and readable text.

The custom title bar and its two buttons paint fully opaque. Windows still renders its native caption underneath the extended frame; a translucent header could expose a faint second set of minimize/maximize/close glyphs. The opaque header covers those glyphs while preserving the Acrylic body, native window styles, dragging, and monitor movement. Returning zero from [WM_NCCALCSIZE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-nccalcsize) alone does not remove an extended DWM frame. Settings labels also have an 8-pixel gap before the value controls, preventing overlapping child-window bounds.

When Windows Transparency effects are off, High Contrast is on, the session is remote, desktop composition is unavailable, or Windows rejects the material API, the app uses fully opaque themed surfaces. High Contrast uses system colors, including contrasting switch thumbs and title-button hover colors. Windows can also reduce or suppress native Acrylic because of power, activation, or session policies even when the API request succeeds.

This remains a WinForms app; native Windows message boxes retain their operating-system appearance.

### Material checks on Windows

- Place colorful content behind the app and inspect light and dark modes, idle and active, including control hover states. The material should be subtle; text, glow, and control edges should have no black rectangles or halos.
- Switch Windows app mode while the window is shown and hidden, then restore it from the tray.
- Turn Transparency effects off/on and a Windows contrast theme on/off. Verify opaque fallback and return to the material where Windows permits it.
- Check minimize/restore, dragging, focus changes, monitor movement, and a remote session.

### Window recovery checks on Windows

- Focus Mouse Jiggler and use Alt + Left/Right/Up/Down on a layout with horizontal, vertical, and negative-coordinate monitors. Verify the destination follows the direction and the window stays within its usable area.
- Leave the window on another monitor, focus another app, and press Ctrl + Alt + Home with the pointer on the visible monitor. Repeat while Mouse Jiggler is minimized and hidden in the tray.
- Test tray **Show on this monitor**, double-click, and launching another instance with the pointer on another monitor.
- Disconnect a monitor while the window is open or hidden, and restart with a saved position beyond the display edge. Verify the title bar and controls remain reachable.
- Verify interval, distance, startup, always-on-top, pause, and wake settings survive recovery and theme changes.

### Automated checks

Run `tests\Verify-Appearance.ps1` for isolated fallback rendering, native alpha-buffer, control, and theme-message checks and previews under `artifacts/`. Composited alpha previews demonstrate the app's rendering buffers; they are not screenshots of Windows' final Acrylic compositor.

Run `tests\Verify-Startup.ps1` for isolated hidden-startup checks. A `--silent` launch starts jiggling once after initialization without waiting for the first visible window; showing it later leaves its active state unchanged. Normal launch starts idle. The checks stub mouse input, hotkeys, and wake requests and leave real app preferences untouched.

Run `tests\Verify-Window.ps1` for isolated native window/recovery checks. It uses disposable test settings and separate test shortcuts, leaving the real app's settings, startup entry, and shortcuts untouched. Native monitor transfers use the currently connected displays; simulated geometry checks cover additional layouts. Native client-paint checks require positive DWM caption bounds under Acrylic and verify that the entire 44-pixel header stays opaque across theme changes, minimize/restore, hide/show, and handle recreation. These paint checks exclude the final desktop compositor.

The review package includes actual native light/dark screenshots (`previews/native-light.jpg` and `previews/native-dark.jpg`) captured on Windows 11 build 26200. Both show a single custom minimize/close pair. Actual **Win + Shift + Left/Right** dispatch and visual behavior across other activation, DPI, power, or session configurations remain manual checks above.

The source compiles against .NET Framework 4 using Windows' bundled compiler. This remains a Windows application; compilation on another platform does not verify native window behavior.
