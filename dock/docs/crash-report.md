# Crash report — 2026-09-05

Triggered by Microsoft Partner Center health insights reporting crashes and hangs in production.
This report covers the **crash** findings from a full-codebase audit; see
[`hang-report.md`](hang-report.md) for the companion hang/responsiveness findings. Method and
tooling are described in the [`crash-hang-review`](../.claude/skills/crash-hang-review/SKILL.md)
skill, which this audit also produced.

## Why this class of bug is fatal here

`src/Anchor/App.xaml.cs` installs a global `UnhandledException` handler that logs to
`%Temp%\anchor.log` via `Diag.Log` but deliberately leaves `e.Handled = false`:

> Handled is deliberately left false: swallowing every fault could leave the dock running in a
> corrupt state, so a genuinely unhandled exception still surfaces.

That is the right call architecturally, but it means **every** finding below was a guaranteed,
unrecoverable process crash with no safety net — not a warning, not a degraded mode.

## Scope & method

Four parallel audits, each scoped to a disjoint set of files, read every assigned file in full,
classified findings by concrete failure scenario, fixed genuine bugs directly, and verified with
`dotnet build` + `dotnet test` (233/233 passing throughout). A fifth pass built and ran the actual
published app against a throwaway config to exercise the fixed paths under load. Audited areas:

1. The new drag-and-drop / shell-interop machinery (`DockWindow.DropTargets.cs`, `ShellDrop.cs`,
   `Interop/NativeMethods.cs`, `DockItem.cs`, `IconService.cs`, `DockItemFactory.cs`) — the
   highest-risk surface, being brand-new and uncommitted at audit time.
2. Long-running services and window-lifetime code (`RunningAppMonitor`, `HotkeyService`,
   `TrayIconService`, `MessageWindow`, `WindowChrome`, `AcrylicBackdropManager`,
   `DockWindow.AutoHide/Magnify/Tooltip/FlyoutBar`).
3. Async/threading and external I/O (`UpdateService`, `Launcher`, `ShortcutResolver`,
   `StartupService`, `FolderListing`, `ItemSearch`, `DockWindow.FolderFlyout/ItemHotkey/MoveToDock`)
   — findings from this pass are almost all *hangs*; see the hang report.
4. Config persistence and dialog/settings windows (`DockStore`, `DockConfig`, `DockProfile`,
   `DockManager`, `App.xaml.cs`, every `*Window.xaml.cs`, `Loc`/`LocalizeExtension`,
   `PackagedRuntime`).

## Findings

| # | Severity | File : line | Failure scenario | Status |
|---|----------|--------------|-------------------|--------|
| C1 | **Critical** | `DockStore.cs:75-91` (`Load`) | Any transient read failure — a corrupt/truncated `dock.json`, an antivirus lock at the exact moment of read, a JSON `null` body — fell back to a fresh default config *in memory only*. `DockManager.Start()` then treats that as first-run and immediately **saves the fresh defaults over the still-present original file**, permanently destroying the user's real dock the moment a one-off read glitch occurred. Not a process crash in the traditional sense, but the worst possible outcome next to one: silent, irreversible data loss on every subsequent launch (the user now has an empty dock, forever). | **Fixed** |
| C2 | **Critical** | `DockConfig.cs:227-263` (`Migrate`) | `dock.json` is documented as user-hand-editable. A literal JSON `null` inside a `Docks`, `Items`, or a group's `Children` array deserializes as a null *list entry* (not a parse failure, since these are reference-type lists) and sails through `DockStore.Load()`'s try/catch untouched. `DockWindow`'s constructor walks every item assuming it's real; the first null throws `NullReferenceException` inside `DockManager.Start()` — before any window exists, before `App`'s `UnhandledException` handler has anything useful to log. Since the bad entry is never removed from the file, this is a **crash on every subsequent launch** — the worst-shaped bug there is, a crash loop with no recovery path. | **Fixed** |
| C3 | **Critical** | `DockManager.cs:73-74, 848` (`Start`, `RebuildWindows`) | The per-dock-profile `CreateWindow` call in the startup loop (and in the language-change/import rebuild path) was unguarded. Any exception building *one* dock's window — including future data shapes C2's sanitization doesn't anticipate — took down the entire app before `Diag.Log` could attribute the failure to a specific dock, and irrecoverably (the bad profile stays first in the list on every relaunch). | **Fixed** |
| C4 | **High** | `DockWindow.DropTargets.cs:385-411` (`CreateDropShellWatchdog`) | The new drop-shell watchdog `DispatcherQueueTimer` is created lazily on first use (the first drag ever dragged over the dock), so it isn't present when the window's shared `Closed` handler (which stops every *other* timer) runs — unlike `_pollTimer`/`_slideTimer`/`_dragTimer`/etc. **Concrete repro:** drag a file over a dock, then remove that dock via the right-click menu (`DockManager.RemoveDock` → `window.Close()`) while the drag is still in flight. The watchdog keeps ticking every 200ms against a window whose `AppWindow` no longer exists; its tick calls `CursorOverDock()`, which reads `_appWindow.Position`/`.Size` — throwing on a closed `AppWindow`, unhandled, on the dispatcher thread. | **Fixed** — the timer now stops itself via `Closed += (_, _) => t.Stop()` at creation. |
| C5 | **Medium** | `DockWindow.DropTargets.cs:130` (`Item_DragOver`) | `Item_DragOver` is a synchronous handler with no surrounding try/catch, yet called `e.DataView.Contains(StandardDataFormats.StorageItems)` directly — the same WinRT call that `ShellDrop.HasShellItems`, two lines earlier in the *same method*, already wraps in try/catch (the author evidently knew this specific call can throw on a live drag whose source lets go of its data mid-query, a known OLE drag/drop timing hazard). `DragOver` fires on every pointer move with no deferral protecting it, so any throw here is unhandled and crashes the process mid-drag. | **Fixed** — added `ShellDrop.HasStorageItems` (same try/catch shape as `HasShellItems`) and swapped the raw call for it. |
| C6 | **Medium-High** (found by the runtime stress pass, not the static audits) | `SettingsWindow.xaml.cs:993` (`BuildRow`, subscribe) / `:1191` (incomplete unsubscribe) | Every Apps & Links row subscribes `item.PropertyChanged += OnItemPropertyChanged` to refresh its icon when it resolves asynchronously. The only teardown was `row.Unloaded += (_, _) => item.PropertyChanged -= ...`, which correctly fires when `RebuildApps()` clears the list while the window stays open, but not when the whole window closes without the list ever being cleared — `Unloaded` does not reliably fire on descendants just because the containing `Window` closes. Each `DockItem` is owned by `DockManager.Config`, long-lived and shared across every future Settings open, so every open-then-close of Settings left one dangling closure per row permanently rooted on a live, shared object. A 25-cycle empirical rerun measured **strictly linear** growth: GDI objects +34/cycle, handles +63/cycle, working set +~20MB/cycle, with per-cycle open+close time creeping from ~2.7s to ~5s — at that rate, roughly 260 Settings opens over an always-on session would approach the default per-process GDI-object ceiling (10,000), after which GDI allocation failures cascade into rendering exceptions that this app's `UnhandledException` handler does not swallow. A slow-building crash, but a crash. | **Fixed** — the window's `Closed` handler now also calls `AppsList.Children.Clear()`, driving the same `Unloaded`-based teardown that already worked correctly for the rebuild-while-open case, but now on window close too. A re-verification rerun measured GDI objects still climbing +36.3/cycle and handles +44.3/cycle (working set and the latency creep were resolved) — this fix was real but incomplete; the remaining growth pointed at a second, independent leak (C7). |
| C7 | **Medium** (open — see below) | `SettingsWindow.xaml.cs`, `EditWindow.xaml.cs`, `NewGroupWindow.xaml.cs`, `AddNewWindow.xaml.cs` | These four dialog-style windows are constructed fresh every open and torn down on close (unlike the long-lived `DockWindow`/`SearchWindow`, which manage their backdrop via `AcrylicBackdropManager` and dispose it on `Closed`). All four set `SystemBackdrop = new MicaBackdrop();` with no release, so this was applied as a plausible fix for the GDI/handle growth left over after C6 — but an empirical re-verification found **no measurable effect**: GDI/handle growth on Settings was statistically unchanged from the C6-only measurement, and the same deterministic per-cycle growth (+25 GDI, +31 handles) showed up on `EditWindow`/`AddNewWindow` too, neither tested before. The delta was exactly constant across 55 combined cycles and unaffected by extending the post-close settle time from 250ms to 2000ms — a signature that points away from a GC/timing-dependent managed leak and toward either an uncaught native allocation elsewhere in these windows' shared construction path, or an artifact of this environment's composition/DWM behavior (this dev session has an independently-documented UI-Automation sandbox quirk — see the `project-anchor-dialog-window-gdi-growth-unresolved` memory). `WindowChrome.cs`, shared by these windows and by the two that don't leak, was checked and contains no GDI-allocating calls, ruling it out. | **Partially addressed, not resolved.** The `SystemBackdrop = null` change is harmless and correct per the documented WinAppSDK backdrop-swap pattern, so it stays, but it is not the fix for the remaining growth. Left open rather than chased further with more speculative code changes — see Residual risk below for what the next attempt should do differently. |
| C8 | **High** (reported via Partner Center) | `DockWindow.FlyoutBar.cs:143-152` (`ShowDockBarFlyout`, `flyout.Closed`) | The group/folder fly-out bar's cell-drag watcher (`_barCellDragTimer`, 16ms `WatchCellDrag`) is stopped only by its own release check (`GetAsyncKeyState(VK_LBUTTON)`) or by the whole `DockWindow`'s shared `Closed` handler — never by the fly-out's own `Closed`, unlike the auto-hide pause/resume pair (`PauseAutoHideForDrag`/`ResumeAutoHideAfterDrag`) that the same method already wires to it. **Concrete repro:** open a group or folder bar, press-and-drag a cell past the reorder/drag-out threshold (`dragging = true`), then close the bar by any means other than releasing the mouse button — Escape (light-dismiss), a click outside it, or the window losing focus. The bar (a `Flyout`/popup) closes and its visual tree starts tearing down, but `_barCellDragTimer` keeps ticking every 16ms against the drag's `cell`/`ghost` elements, calling `cell.Opacity = ...` and `Canvas.SetTop/SetLeft(ghost, ...)` — `SetValue` calls on a `DependencyObject` whose native XAML peer is now closed. WinUI's property system throws while resolving the inherited `FlowDirection` property as part of that `SetValue` (`DirectUI::DependencyObject::GetValueByKnownIndex_enum` in `Microsoft.UI.Xaml.dll`), which surfaces on the dispatcher thread as a stowed exception — `App.xaml.cs` deliberately leaves `e.Handled = false`, so this is an unconditional process crash. Matches the reported Partner Center stack (`StowedException` → unresolved native frame → `Microsoft.UI.Xaml.dll!...GetValueByKnownIndex_enum(...FlowDirection_)`) exactly. | **Fixed** — the fly-out's `Closed` handler now also stops `_barCellDragTimer` and runs `_barCellDragCleanup` unconditionally (only one bar can have an active drag at a time, so this is safe regardless of which bar instance is closing), before the existing `_activeBarFlyout`/auto-hide-resume check. |

## Investigated and ruled out (no fix needed)

Recorded so a future pass doesn't re-walk the same ground:

- **`ShellDrop.CanShellResolve` / `IconService.ShellIconFromParsingNameAsync`** — `SHParseDisplayName`'s `ppidl` out-parameter is documented to be nulled on every failure path, and the `finally { if (pidl != 0) FreeCoTaskMem }` guard is the standard shell-interop idiom. No path frees a garbage pointer.
- **HICON lifetime in `IconService`** — every icon-load path (including the new Start-menu/pidl one) destroys its `HICON` in a `finally`; confirmed no double-free or leak on the shared `FromIconHandleAsync` helper.
- **`_dragPayload` stale-read race** (`DockWindow.DropTargets.cs`) — real but narrow: a slow-to-resolve payload read can, in rare timing, be consulted for a caption a few milliseconds later than ideal. Never produces a crash; the only consumer is a caption-selection read that self-corrects on the next drag. Left as documented, accepted behavior.
- **`Items.Move`/`Items.Insert` bounds** in the drop-shell slot logic — every index is clamped against a freshly-read `Items.Count`; empty-dock and single-item cases are already special-cased.
- **`DockItemFactory.SuggestName`'s prefix slice** — safe by construction (`StartsWith` guarantees sufficient length before the slice runs).
- **Fire-and-forget (`_ = ...Async()`) call sites** in the drag-drop scope — both wrap their entire body in try/catch internally.
- **Native-call thread affinity** (`GetCursorPos`, `GetAsyncKeyState`, `SHParseDisplayName`, `SHGetFileInfoW`/pidl variant) — confirmed UI-thread-affine throughout; no `ConfigureAwait(false)`/`Task.Run` ahead of any of them in this scope.
- **`DockStore.Save()`** — already write-to-temp-then-atomic-move; failures already caught and logged without propagating.
- **`DockManager.UpdateChecksSupported` / `PackagedRuntime.Resolve()`** — package-identity lookup already wrapped in try/catch, defaults to "unpackaged" on any failure.
- **`Loc.cs` / `LocalizeExtension.cs`** — `Loc.Get` never throws (falls back to English, then the key itself); `LoadTable` catches per-language deserialization failures. No crash path for a missing/malformed translation file, including during the pre-window XAML binding pass at startup.
- **`HotkeyGesture.TryParse` / `HotkeyCaptureButton`** — malformed/legacy gesture strings return `false` rather than throwing.
- **`DockMetrics.cs` / `DockItemAnimations.cs`** — geometry math already guards the one real division (`MagnificationAt`'s `reach <= 0` check); no other unguarded division or NaN path.
- **`EditWindow`/`NewGroupWindow` operating on a concurrently-deleted item** — degrades to a silent no-op on save (edits an in-memory object no longer linked to persisted state) rather than throwing. A UX gap, not a crash; left unchanged per scope.
- **`SearchWindow`** — zero-docks/zero-items and empty-result-set paths are already guarded.
- **`IImageList` RCW never explicitly released** in `IconFromSystemImageList` — pre-existing pattern, relies on RCW finalization; not a new leak, low real-world impact, left as-is.

## Verification

- `dotnet build src/Anchor/Anchor.csproj -c Debug -p:Platform=x64` — 0 warnings, 0 errors, after all fixes combined.
- `dotnet test tests/Anchor.Tests/Anchor.Tests.csproj` — 233/233 passing (3 new tests added for `DockConfig.SanitizeItems`, covering a null dock, null item, and null-vs-missing `Children`).
- Runtime stress pass: a published build was launched against a throwaway `ANCHOR_DATA_DIR` (never the real config) seeded with ~22 items across two docks, two groups, an item on a missing drive, and a folder item with 550 entries, then driven through ~220 UI-Automation actions across four separate launches. Zero crashes, zero hangs, zero `UNHANDLED:` lines in `anchor.log`. See [`hang-report.md`](hang-report.md#empirical-runtime-results) for the full scenario-by-scenario breakdown — this pass is also what surfaced C6 above, which none of the four static audits had covered.
- C6/C7 specifically: three empirical reruns of the same ~25-cycle open/close methodology bracket this pair of fixes. Before any fix (Settings): linear GDI +34/handles +63/memory +20MB per cycle, latency creeping 2.7s→5s. After the C6 fix alone (Settings): GDI +36.3/handles +44.3/memory +5.25MB per cycle, latency flat — memory and latency resolved, GDI/handles essentially unchanged. After the C7 fix (Settings, plus EditWindow and AddNewWindow tested for the first time): GDI/handle growth statistically unchanged again on Settings (+37/+44.5), and the same deterministic per-cycle growth appeared on EditWindow (+25.3 GDI/+31.4 handles) and AddNewWindow (+25.0 GDI/+30.9 handles) — confirming C7 made no measurable difference on any of the three windows tested, while the memory/latency wins from C6 held across all three.

## Residual risk / recommendations

- **Update 2026-10-01 — C7 re-diagnosed** in [`performance/2026-10-01-review`](performance/2026-10-01-review/REPORT.md) (findings PR-001 and PR-002), by heap dumps and
  a window-lifetime probe rather than a third guess. It is two causes. (1) Closed windows were never freed: every window subscribed lambdas to its own `Activated` / `Closed`
  events, and `SettingsWindow` rows subscribed `DockItem.PropertyChanged` with closures that kept the window alive (12 of 12 `SettingsWindow`s alive after a forced GC) — fixed.
  C6's row teardown was incomplete for the same reason, and the `SystemBackdrop = null` change in C7 could not have mattered. (2) The residual ~30 GDI objects and ~40 handles per
  dialog are the Windows App SDK custom title bar (`ExtendsContentIntoTitleBar` + `SetTitleBar`), paid per window whether or not it is collected: **still open**, a framework cost.
  The "environment artifact" suspicion was wrong: the probe reproduces it in a bare WinUI app. The paragraph below is the 2026-09-05 state.
- **C7 is open, not resolved.** Two targeted fixes (C6's event-unsubscription fix, and C7's
  backdrop-disposal fix) together cut Settings' per-open memory growth by ~74% and eliminated a
  creeping per-open slowdown entirely — real, measured wins — but a deterministic, timing-
  independent GDI-object/handle growth (~+25 to +37 per cycle) persists across `SettingsWindow`,
  `EditWindow`, and `AddNewWindow` alike (`NewGroupWindow` untested but shares the same
  construction pattern). At the measured rate this is a slow-burn concern (roughly 270-400 dialog
  opens before approaching the default 10,000-object GDI ceiling), not an active crash or hang — it
  is being left open deliberately rather than chased with a third speculative fix. The next attempt
  should start with an actual GDI-handle-type breakdown (GDIView, Process Explorer's handle diff,
  or an ETW composition trace) rather than guessing at another disposal pattern, and should ideally
  re-run the same empirical harness on a real, non-virtualized machine first — this specific dev
  environment has an independently-documented UI-Automation quirk (see the
  `project-anchor-dialog-window-gdi-growth-unresolved` memory), so ruling environment artifacts in
  or out matters before attributing the remainder to app code. Full detail in that memory file.
- `EditWindow`/`NewGroupWindow` silently no-op-ing on a concurrently-deleted item (see above) is a correctness gap worth a follow-up, though it isn't a crash.
- The `_dragPayload` stale-read race is cosmetic today; if the drag-drop payload model grows more state in the future, revisit with a generation counter rather than assuming it stays this narrow.
- Re-run this checklist (`crash-hang-review` skill) whenever new native-interop, timer, or drag-and-drop code lands — that is exactly where 5 of these 5 crash fixes lived.
