# TrackSwipe

Fluid, finger-tracking touchpad gestures for Windows 11 precision touchpads. Swipe four fingers sideways and your whole desktop, live windows and all, slides away under your fingers while the next one slides in. You can reverse mid-swipe, and on release a spring commits or cancels. A quick flick counts too. The slide replaces Windows' own switch animation rather than playing before it. Swipe up for a macOS-style Mission Control.

This is the **basic version**: one gesture done end to end, built so the rest of the plan can grow on top of it.

## What's in it

| Piece | Where | Notes |
|---|---|---|
| Gesture engine | `src/TrackSwipe.Core` | Pure library, no Windows APIs. State machine, finger-tracked progress on both axes, velocity, flick/commit rule, spring settle, rubber-banding, staggered-lift handling. Also the Mission Control window-spread layout. |
| Engine tests | `tests/TrackSwipe.Core.Tests` | Synthetic 125 Hz traces (commit, cancel, flick, reversal, rubber band, wrong finger count, jitter, diagonal, early lift, timeout) and layout checks (fits, no overlap, aspect ratios kept). |
| Touchpad reader | `src/TrackSwipe.App/Touchpad` | Raw Input (`RIDEV_INPUTSINK`) plus HID parsing. Handles parallel and hybrid report modes and reads the pad's physical size so thresholds are in millimetres. |
| Desktop slide | `src/TrackSwipe.App/Overlay` | At gesture start, lists the windows on the current and both neighbouring desktops (the shell cloaks other desktops' windows, but DWM keeps rendering them). Each monitor gets a never-activated window filled with live DWM thumbnails of those windows plus the wallpaper. Each desktop's layer sits at (layer − progress) × screen width; the taskbar stays fixed. Paced by `DwmFlush`. |
| Mission Control | `src/TrackSwipe.App/Overlay/MissionControl.cs` | The same live thumbnails, interpolated between where windows are and an overview: windows spread out without overlapping, live miniatures of every desktop along the top, wallpaper dimmed. Labels, highlights and the "+" button are drawn on a transparent layer above. |
| Window hand-off | `src/TrackSwipe.App/Overlay/WindowHandoff.cs` | Closing Mission Control onto a window: it flies to exactly where the real window will be (restored size and position, maximized if it was), then, still under the overlay, the desktop switches, the window restores (Windows' restore animation turned off, or waited out) and comes to the front. A still image covers the frame where DWM blanks a restoring window's thumbnail. The overlay only comes off once the real window has stopped moving and repainted; if it rests somewhere unexpected (a snapped window), the thumbnail glides there first. |
| Instant switch | `src/TrackSwipe.App/Desktops` | On commit, the target layer is exactly where the real windows will be, so the app switches desktops through Explorer's internal `SwitchDesktop`, which has no animation. It keeps the overlay up until the target windows actually uncloak, then removes it. |
| App shell | `src/TrackSwipe.App` | Tray icon, pause, spring preset, input monitor, crash-safe reset, local log. |

**Gestures (default 4 fingers; choose 3 or 4 in the tray menu):**

- **Left/right:** switch desktops (tracked, reversible, no native animation afterwards). Rubber-bands when there is no desktop in that direction.
- **Up:** Mission Control, tracking your fingers and reversible. Once open:
  - every app window on the desktop is there, minimized ones included; hover one to highlight it and see its title, then click it to focus (and restore) it;
  - swipe sideways to pan to the neighbouring desktop's windows, which switches to that desktop;
  - click a desktop miniature to switch to it, or **+** to add a desktop;
  - click empty space, press <kbd>Esc</kbd>, or swipe down to close. Swiping down with the pointer over a window closes onto that window, as clicking it would: your fingers carry it to its real position and it ends up in front.
- **Down** (from the desktop): show desktop (<kbd>Win</kbd>+<kbd>D</kbd>).

## Build and run

Needs the .NET 8 SDK. Builds native ARM64 on Snapdragon, x64 elsewhere.

```bash
dotnet test
```

```bash
dotnet run --project src/TrackSwipe.App
```

Options: `--fingers 3|4|5`, `--distance <mm>` (finger travel for one full desktop, default 55), `--smooth` (softer spring), `--preview` (play a demo swipe on start), `--diagnose` (log what gestures would see, hold the slide at 0, ½ and 1, then Mission Control at 0, ½ and open, then quit), `--diagnose-mission` (open Mission Control, pan to a neighbouring desktop and back, close), `--diagnose-focus <window handle>` (open Mission Control and click that window), `--diagnose-swipe <window handle>` (point at it and swipe down: once abandoned, then for real).

**First run:** choose **Gesture fingers → Three fingers** or **Four fingers** in the tray menu. Four is the default; your menu choice is remembered between launches. Open *Settings → Bluetooth & devices → Touchpad → Three- and four-finger gestures* and set **swipes for your selected finger count to "Nothing"**. Otherwise Windows handles the same swipe too. The app shows a reminder if it's still on. `--fingers` overrides the saved choice for that launch.

## Tray menu

- **Gestures enabled:** pause/resume.
- **Gesture fingers:** choose three or four fingers. Changes apply immediately, cancel any active gesture, and are saved for next time.
- **Spring:** Snappy or Smooth.
- **Preview animation:** a synthetic swipe out to 60% and back through the real engine and slide. Never switches desktops.
- **Input monitor:** live contacts (blue finger, orange palm, outline lifting), report rate, phase, progress and velocity. Use this to check Phase 0 question 1 and to tune.

## Privacy

Touch data stays in memory and is never written to disk. The log (`%LOCALAPPDATA%\TrackSwipe\trackswipe.log`) records only startup settings, the touchpad's description and errors. No network access, no telemetry.

## Known limits of this version

- **Undocumented switch API.** The instant switch uses Explorer's internal virtual desktop interface (Windows 11 24H2/25H2 IDs). The app verifies it against the registry at startup and logs the result. If a Windows update changes it, swipes fall back to <kbd>Ctrl</kbd>+<kbd>Win</kbd>+<kbd>←/→</kbd>, and Windows' own animation plays after ours.
- **Thumbnail fidelity.** Window shadows aren't part of DWM thumbnails, so they disappear while you swipe. Nor is the system Mica/Acrylic backdrop many apps use (WhatsApp, browsers, Electron apps), so each window gets a solid Mica-coloured backing (dark or light, to match the app); it's close to real Mica but without the wallpaper tint. If you use a different wallpaper on each desktop, every desktop shows the current one during the swipe. Apps that stop drawing while on a hidden desktop show their last frame.
- **Mission Control.** The desktop bar appears only on the primary monitor; other monitors spread their own windows without one. Windows can't be dragged between desktops yet, and there's no keyboard navigation besides <kbd>Esc</kbd>. Desktop miniatures show app windows only.
- **Always-on-top windows** (including some apps in full-screen mode) stay above a window you click in Mission Control, exactly as Windows keeps them; the clicked window lands behind them rather than pretending otherwise. Windows that block screen capture (recording toolbars, protected video) are left out of gestures, since they would only mirror as black boxes.
- Desktop count and position come from Explorer's registry values (undocumented). If they can't be read, both directions are allowed and nothing rubber-bands.
- Touch input isn't delivered while an elevated window has focus, and shortcuts can't be sent to one either (UIPI).
- No auto-disable for full-screen games yet, no settings UI, no autostart, no trace recorder.

## Next steps (from the plan)

1. **Phase 0 check on real hardware:** run the input monitor on the Surface pad, confirm contacts, palm flags and rate, and confirm the overlay is click-through and smooth.
2. **Phase 1:** trace recorder and replayer, so engine tests run on real recorded swipes instead of synthetic ones.
3. **Phase 3 polish:** draw window shadows and per-desktop wallpapers, and support windows pinned to all desktops.
4. **Phase 4/5:** full-screen detection, settings UI with per-gesture mapping, autostart, ARM64 + x64 publish.
