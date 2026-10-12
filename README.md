# TrackSwipe GPU 0.2

Fluid, finger-tracking touchpad gestures for Windows 11 precision touchpads. Swipe four fingers sideways and your whole desktop, live windows and all, slides away under your fingers while the next one slides in. You can reverse mid-swipe, and on release a spring commits or cancels. A quick flick counts too. The slide replaces Windows' own switch animation rather than playing before it. Swipe up for a macOS-style Mission Control.

This is a fork of TrackSwipe that renders through **DirectComposition**, the API DWM itself composes with. Live window thumbnails are visuals in a composition tree, so each frame's moves, fades, clips and restacking reach the screen as one atomic batch, composed on the GPU. See [GPU composition](#gpu-composition). It shares the original's single-instance lock, so only one of the two runs at a time: they react to the same swipes.

## What's in it

| Piece | Where | Notes |
|---|---|---|
| Gesture engine | `src/TrackSwipe.Core` | Pure library, no Windows APIs. State machine, finger-tracked progress on both axes, velocity, flick/commit rule, spring settle, rubber-banding, staggered-lift handling. Also the Mission Control window-spread layout. |
| Engine tests | `tests/TrackSwipe.Core.Tests` | Synthetic 125 Hz traces (commit, cancel, flick, reversal, rubber band, wrong finger count, jitter, diagonal, early lift, timeout) and layout checks (fits, no overlap, aspect ratios kept). |
| Touchpad reader | `src/TrackSwipe.App/Touchpad` | Raw Input (`RIDEV_INPUTSINK`) plus HID parsing. Handles parallel and hybrid report modes and reads the pad's physical size so thresholds are in millimetres. |
| Desktop slide | `src/TrackSwipe.App/Overlay` | At gesture start, lists the windows on the current and both neighbouring desktops (the shell cloaks other desktops' windows, but DWM keeps rendering them). Each monitor gets a never-activated window whose DirectComposition tree holds live thumbnails of those windows plus the wallpaper. Each desktop's layer sits at (layer − progress) × screen width; the taskbar stays fixed. |
| Composition | `src/TrackSwipe.App/Interop/Composition.cs`, `Overlay/ThumbnailSurface.cs` | A hardware D3D11/Direct2D device, shared DWM thumbnail visuals, and the per-monitor surfaces and sprites built on them. `Overlay/SpriteState.cs` works out what each placement changes. |
| Mission Control | `src/TrackSwipe.App/Overlay/MissionControl.cs` | The same live thumbnails, interpolated between where windows are and an overview: windows spread out without overlapping, live miniatures of every desktop along the top, wallpaper dimmed. Labels, highlights and the "+" button are drawn once with Direct2D into GPU composition surfaces; their fades run in the compositor. |
| Window hand-off | `src/TrackSwipe.App/Overlay/WindowHandoff.cs` | Closing Mission Control onto a window: it flies to exactly where the real window will be (restored size and position, maximized if it was), then, still under the overlay, the desktop switches, the window restores (Windows' restore animation turned off, or waited out) and comes to the front. A GPU texture copy covers the frame where DWM blanks a restoring window's thumbnail, with no CPU screenshot readback. The overlay only comes off once the real window has stopped moving and repainted; if it rests somewhere unexpected (a snapped window), the thumbnail glides there first. |
| Instant switch | `src/TrackSwipe.App/Desktops` | On commit, the target layer is exactly where the real windows will be, so the app switches desktops through Explorer's internal `SwitchDesktop`, which has no animation. It keeps the overlay up until the switch is on screen (the target desktop's windows uncloak or, on an empty desktop, the old desktop's windows cloak), then removes it. |
| App shell | `src/TrackSwipe.App` | Tray icon, pause, spring preset, input monitor, crash-safe reset, local log. |

**Gestures (default 4 fingers; choose 3 or 4 in the tray menu):**

- **Left/right:** switch desktops (tracked, reversible, no native animation afterwards). Rubber-bands when there is no desktop in that direction.
- **Up:** Mission Control, tracking your fingers and reversible. Once open:
  - every app window on the desktop is there, minimized ones included; hover one to highlight it and see its title, then click it to focus (and restore) it;
  - swipe sideways to pan to the neighbouring desktop's windows, which switches to that desktop;
  - click a desktop miniature to switch to it; hover the preview to reveal its top-right **×**, in a square white container at 10% opacity with a brighter button hover state. Clicking it closes that desktop and moves its windows to the one on its left (or the next desktop when closing the first). The last desktop cannot be closed;
  - the compact **+** at the far right adds a desktop. New previews grow and fade in; closed previews shrink and fade out, and the remaining previews slide into place. These animations run in the compositor;
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

Options: `--fingers 3|4|5`, `--distance <mm>` (finger travel for one full desktop, default 55), `--smooth` (softer spring), `--preview` (play a demo swipe on start), `--diagnose` (log what gestures would see, hold the slide at 0, ½ and 1, then Mission Control at 0, ½ and open, then quit), `--diagnose-mission` (open Mission Control, pan to a neighbouring desktop and back, close; add `--switch 1` or `--switch -1` to pan once and really switch there), `--diagnose-focus <window handle>` (open Mission Control and click that window), `--diagnose-swipe <window handle>` (point at it and swipe down: once abandoned, then for real), `--diagnose-desktops` (add and close temporary desktops, including one holding a test window, then restore the original desktop), `--diagnose-open [cycles]` (synthetic swipes up and dismissals, two by default, then log memory and quit), `--diagnose-slide 1|-1` (a synthetic sideways swipe that really switches to the next or previous desktop).

**First run:** choose **Gesture fingers → Three fingers** or **Four fingers** in the tray menu. Four is the default; your menu choice is remembered between launches. Open *Settings → Bluetooth & devices → Touchpad → Three- and four-finger gestures* and set **swipes for your selected finger count to "Nothing"**. Otherwise Windows handles the same swipe too. The app shows a reminder if it's still on. `--fingers` overrides the saved choice for that launch.

## Tray menu

- **Gestures enabled:** pause/resume.
- **Gesture fingers:** choose three or four fingers. Changes apply immediately, cancel any active gesture, and are saved for next time.
- **Spring:** Snappy or Smooth.
- **Preview animation:** a synthetic swipe out to 60% and back through the real engine and slide. Never switches desktops.
- **Input monitor:** live contacts (blue finger, orange palm, outline lifting), report rate, phase, progress and velocity. Use this to check Phase 0 question 1 and to tune.

## GPU composition

Each monitor's surface is a window with no redirection bitmap (`WS_EX_NOREDIRECTIONBITMAP`); it draws nothing itself. Its only content is a DirectComposition tree. DWM provides live shared window visuals; a hardware D3D11/Direct2D device supplies decorations, cached solid backings, highlight rings and restore covers. Each window is mirrored by a shared thumbnail visual (`DwmpCreateSharedThumbnailVisual`, undocumented, dwmapi ordinal 147), which sits inside a container visual that holds its position, clip and opacity. A black swatch at the bottom of the tree shows wherever no thumbnail covers.

- **One atomic commit per frame.** A frame changes visual properties, then commits once, and DWM applies the whole batch together. Changes made outside a frame (clicks, timers) are committed once the current message has been handled. Restacking (bringing a clicked window to the front) is a reorder within that batch, where the original re-registered thumbnails in a careful order to avoid a wrong frame.
- **Sizing stays in the composition batch.** Thumbnails keep their source raster throughout opening, the settled overview and closing; a child visual scales them with linear GPU interpolation. Scale, fractional position, clip and opacity move together. Entering or leaving the overview never switches between small and full-size DWM rasters, which could race the composition commit and briefly show the wrong scale when clicking an app. Only initial setup or an actual source-size change (such as restoring a minimized window) requires `DwmUpdateThumbnailProperties`, using the undocumented `DWM_TNP_ENABLE3D` flag.
- **Settles run in the compositor.** Release, click-to-close and restore-position correction submit their complete spring trajectories once. Windows evaluates the curves at display refresh even if the app's UI thread pauses. Finger tracking still reads input and submits a visual batch each frame.
- **Clipping is a clip, not a crop.** A window is drawn whole at its size and clipped to its desktop or miniature, so the pixels that remain never shift or rescale.
- **Earlier measurements** on a Surface Laptop 7 at 120 Hz using four synthetic Mission Control cycles: warm opening work fell from about 0.7–1.1 ms in the previous Debug build to 0.4–0.5 ms in the updated Release build. Thumbnail resizes averaged about one per opening frame (setup and final filtered sizing included), rather than 7–10, and rounded to zero while closing. The current build also keeps the source raster at rest, eliminating those final overview resizes. These figures measure app callbacks and CPU work, not unique frames presented by the display; they do not establish parity with native Task View.

Window thumbnails are clipped to their own desktop during slides and to their own miniature in Mission Control, including windows partly outside a monitor. Desktop assignment uses Windows' desktop ID for visible and shell-cloaked windows, with an owner lookup for auxiliary windows.

## Rendering and performance

Hidden thumbnails receive no per-frame updates. Repeated sideways gestures reuse thumbnails when the window positions, stacking, desktops and monitors are unchanged; a change rebuilds the slide. The slide and Mission Control each have their own surfaces, so neither disturbs the other's cache. Previews remain live, so compositor load still grows with the number of visible windows.

**Frame pacing.** A dedicated thread waits on the Windows 11 compositor clock (`DCompositionWaitForCompositorClock`), which ticks every display refresh, and starts each frame on the UI thread through a sent message so it never queues behind other work. The frame replies at once: until it does, COM refuses calls out of the UI thread, and the instant desktop switch, a call into Explorer, would fail and fall back to the shortcut. A frame never starts inside another (a call to another process lets sent messages through); that tick is skipped. Animations are evaluated for the frame's predicted display time (from DWM's vsync timing), so motion stays even if a wake-up is late. While animating, `DCompositionBoostCompositorClock` lifts a dynamic-refresh display to its top rate (120 Hz on a Surface Laptop 7); the boost is released as soon as the animation ends. On Windows 10 it falls back to `DwmFlush` pacing without the boost.

**Input.** The touchpad is read on its own thread and touches are queued; each frame drains them right before drawing. Tracking is resampled at the predicted display time, using a bounded forecast of up to 20 ms and a 4 ms correction, so mismatched touch and display rates don't repeatedly draw the same position. Stops and reversals use the newest report. Gesture thresholds and commit decisions still use raw movement, and release starts its spring at the last displayed position.

**Start-up.** When the gesture's finger count lands, before the swipe is recognised, TrackSwipe captures the window list, prepares both the slide and Mission Control hidden, and starts the frame clock (and 120 Hz boost) for up to half a second. When the swipe begins, it only has to show what's ready. If nothing was prepared, Mission Control registers what the first frame needs and adds the rest (neighbouring desktops, miniatures, the ring) a few milliseconds per frame. Labels are rendered into GPU surfaces on the UI thread during preparation or when their content changes.

**Lightweight.** Solid backings are only added for windows that use a system backdrop. Idle, TrackSwipe does nothing per frame: the clock sleeps and the touchpad thread only queues reports. TrackSwipe's code is compiled on a low-priority thread at startup; for the leanest start on slower machines, publish it ahead-of-time compiled (ReadyToRun):

```bash
dotnet publish src/TrackSwipe.App -c Release -r win-arm64
```

(`-r win-x64` on Intel/AMD.) Each animation logs a `Frames:` line: frame rate, worst gap, work per frame, DWM resizes and visual changes per frame, and the frame where the overlay appeared. Gaps before that frame happen while fingers rest and the overlay is prepared, so they're never seen. `--diagnose-open` runs synthetic swipes up and quits, for measuring; with a cycle count (say 20) it shows whether anything accumulates. Earlier thumbnail-only builds kept memory and handle counts flat over 20 cycles; use this diagnostic to check resource use after rendering changes. DWM's memory grows while thumbnails are in use, as with the original, and is released after TrackSwipe exits.

For compositor independence checks, `TRACKSWIPE_DIAGNOSE_BLOCK_UI_MS=350` deliberately pauses the UI thread after submitting a click-close animation; this is a diagnostic setting only. Captured frames verified motion continued during that pause.

For visual checks with `--diagnose`, `TRACKSWIPE_DIAGNOSE_SLOWDOWN=8` lengthens each stage eightfold. `TRACKSWIPE_DIAGNOSE_VISIBLE=1` makes the overlay surface discoverable by window screenshot tools; normal runs keep it out of Alt+Tab and the taskbar.

## Privacy

Touch data stays in memory and is never written to disk. The log (`%LOCALAPPDATA%\TrackSwipe-GPU\trackswipe.log`) records only startup settings, the touchpad's description and errors. No network access, no telemetry.

## Known limits of this version

- **Undocumented composition API.** Shared thumbnail visuals (dwmapi ordinal 147) and the flag their updates need aren't documented. They're verified on Windows 11 build 26200. If a Windows update removes them, TrackSwipe GPU logs why and won't start; the original TrackSwipe still works.
- **Undocumented switch API.** The instant switch uses Explorer's internal virtual desktop interface (Windows 11 24H2/25H2 IDs). The app verifies it against the registry at startup and logs the result. If a Windows update changes it, swipes fall back to <kbd>Ctrl</kbd>+<kbd>Win</kbd>+<kbd>←/→</kbd>, and Windows' own animation plays after ours.
- **Thumbnail fidelity.** Window shadows aren't part of DWM thumbnails, so they disappear while you swipe. Nor is the system Mica/Acrylic backdrop many apps use (WhatsApp, browsers, Electron apps), so each window gets a solid Mica-coloured backing (dark or light, to match the app); it's close to real Mica but without the wallpaper tint. If you use a different wallpaper on each desktop, every desktop shows the current one during the swipe. Apps that stop drawing while on a hidden desktop show their last frame.
- **GPU restore capture.** Desktop duplication must be available on the rendering adapter. If an output cannot be captured or a frame times out, the live thumbnail and backing remain in use; there is no CPU screenshot fallback.
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
