# Hang report — 2026-09-05

Triggered by Microsoft Partner Center health insights reporting crashes and hangs in production.
This report covers the **hang / unresponsiveness** findings from a full-codebase audit; see
[`crash-report.md`](crash-report.md) for the companion crash findings. Method and tooling are
described in the [`crash-hang-review`](../.claude/skills/crash-hang-review/SKILL.md) skill, which
this audit also produced.

## What counts as a hang here

Anchor's dock windows run on a single UI-thread dispatcher, same as any WinUI app. A "hang" is
anything that stops that thread from pumping messages: a blocking synchronous call, a deadlock
between an awaited continuation and the dispatcher's synchronization context, an unbounded wait on
something that may never complete, or work whose cost scales with data the app doesn't control (a
folder's size, a stalled network share) done directly on that thread. Anchor's items can point at
**any** file, folder, shortcut, or app a user has ever added — including targets on removable or
network drives that can later disappear, unmount, or simply stop responding. Every finding below
traces back to that: an operation that is instant on a healthy local disk but can block for the
OS's own SMB/mount timeout (tens of seconds, sometimes much longer) against one that isn't.

## Scope & method

See [`crash-report.md`](crash-report.md#scope--method) for the full audit structure (four parallel
static-analysis passes plus a runtime stress pass). The hang findings below came almost entirely
from the async/threading audit (scoped to `UpdateService`, `Launcher`, `ShortcutResolver`,
`StartupService`, `FolderListing`, `ItemSearch`, `DockWindow.FolderFlyout/ItemHotkey/MoveToDock`),
plus one responsiveness defect from the long-uptime audit (`DockWindow.FlyoutBar.cs`).

## Findings

| # | Severity | File : line | Failure scenario | Status |
|---|----------|--------------|-------------------|--------|
| H1 | **High** | `ShortcutResolver.cs` (`Resolve`) | `Resolve()` runs **synchronously on the UI thread on every running-app poll** — `RunningAppMonitor.IsRunning`/`TryFocus` call it roughly every 2 seconds, for every `.lnk` item on every dock. It ran `File.Exists`, `File.GetLastWriteTimeUtc`, and a COM `IPersistFile.Load` unconditionally, even on a cache hit. **Concrete repro:** pin a shortcut whose target lives on a network share or removable drive, then have that volume go unresponsive (unplugged, VPN drop, server down) while Anchor keeps running. The 2-second poll freezes the entire dock — every dock, since the poll is shared — and keeps re-freezing every cycle for as long as the volume stays down. This is the single highest-severity hang found: it's periodic, it affects the whole app, and it's triggered by an everyday environmental condition (an unplugged USB drive), not an edge case. | **Fixed** — the actual lookup now runs on a thread-pool task; `Resolve()` waits up to 300ms then reports "not resolved for now" rather than blocking further. In-flight lookups are de-duplicated per path (via `InFlight`) so a persistently stalled target accumulates at most one stuck thread-pool thread, not a growing pile of them across successive polls. The lookup itself isn't abandoned — it keeps running and populates the cache whenever it does finish. |
| H2 | **Medium** | `FolderListing.cs:59-68` (`Read`) | `.EnumerateFileSystemInfos().Where(...).OrderBy(...).ThenBy(...).Take(max)` — `OrderBy` must buffer its **entire** input before yielding a single result, so the trailing `Take(60)` did not bound the enumeration at all; a folder with tens of thousands of entries was walked and sorted in full before the top 60 were picked. Over a slow network share, where each entry can cost its own round trip, this turns "click a folder stack" into a filesystem walk proportional to the folder's *total* size. | **Fixed** — added a hard `EnumerationCap` (5000) `.Take()` applied **before** the filter/sort, so the raw enumeration is actually bounded. Existing 20-file/cap-5 test still passes. |
| H3 | **Medium-High** | `DockWindow.FolderFlyout.cs` (`ShowFolderFlyout`) | Called `FolderListing.Read(path)` **synchronously** from `Item_Click` on the UI thread. Combined with H2, or independently against a folder on a stalled network share (even `Directory.Exists`/enumeration start can stall), a single click on a folder stack froze the whole dock for as long as the read took. | **Fixed** — method is now `async void`, running the read via `await Task.Run(...)`. |
| H4 | **Medium-High** | `Launcher.cs` (`Launch`, `LaunchWith`, `OpenFileLocation`) | All three ran `File.Exists`/`Directory.Exists`/`Process.Start(UseShellExecute: true)` synchronously on the UI thread — reached directly from an icon click (`Item_Click` → `LaunchOrFocus` → `DockManager.LaunchOrFocus` → `Launcher.Launch`), a file drop (`Item_Drop` → `Launcher.LaunchWith`), and a context-menu action (`Launcher.OpenFileLocation`). Any of these against a target on a since-disconnected network/removable drive blocks for the OS's own mount/SMB timeout, freezing the dock for the duration. | **Fixed** — each entry point is now a thin synchronous wrapper that hands the real work to `Task.Run`; existing exception handling moved with the relocated code unchanged, so failure behavior is identical, just off the UI thread. |
| H5 | **Medium** (responsiveness defect, not a true hang) | `DockWindow.FlyoutBar.cs` (`ShowDockBarFlyout`) | Every bar-flyout open pairs one `PauseAutoHideForDrag()` with one `ResumeAutoHideAfterDrag()` on that bar's own `Closed` event. But two call sites — hovering straight from one group icon onto an adjacent one, and clicking a subfolder inside a folder stack — hide the currently-open bar and open a new one **in the same synchronous call**; the old bar's `Closed` only fires afterward, asynchronously. Without tracking which bar is actually current, that stale `Closed` still called `ResumeAutoHideAfterDrag()`, resuming the auto-hide cursor poll while the **new** bar was the one on screen. **Concrete repro:** an auto-hiding, hover-opening dock; hover from one group to the next, or drill two levels into a folder stack, and leave the cursor on the new bar's contents. The dock itself can slide off-screen into auto-hide while its own fly-out bar is still open and anchored to it — a floating, orphaned bar with no dock visible underneath. The process stays responsive throughout, but the feature becomes unusable/confusing in a way that reads the same as a hang to a user ("the dock disappeared and I can't get it back"). | **Fixed** — added `_activeBarFlyout`, checked with `ReferenceEquals` before a `Closed` handler is allowed to resume auto-hide; a superseded bar's belated close is now a no-op. |

## Investigated and ruled out (no fix needed)

- **`UpdateService.cs`** — `HttpClient.Timeout` is explicitly set to 10s (not the 100s default); `CheckAsync` catches `Exception` broadly (covers timeout, DNS failure, malformed JSON, HTTP error status alike); the only caller (`DockManager.CheckForUpdatesAsync`) is invoked fire-and-forget at startup with no blocking anywhere in the chain.
- **`StartupService.cs`** — `IsEnabledAsync`/`SetEnabledAsync` are `async` end-to-end with proper `await`, wrapped in try/catch; all callers already `await` correctly, no `.Result`/`.Wait()` anywhere.
- **`ItemSearch.cs`** — a single `Where/Select/OrderBy/ThenBy/Take` pass per keystroke, O(n log n); no nested loop or repeated re-sort. Trivial even at thousands of items — not a hang risk at any realistic scale.
- **`FolderListing.cs`** (beyond H2) — missing-folder, denied-access, and file-not-folder cases were already handled with a try/catch and an existence guard.
- **`DockWindow.ItemHotkey.cs` / `DockWindow.MoveToDock.cs`** — menu-construction only; both rebuild fresh on every right-click, so a stale reference in an open menu isn't persisted across opens. `MoveItemToDock` already guards against the item having been concurrently removed before mutating.
- **`Launcher.BuildArguments`** — no new crash/hang gaps found beyond what's already covered by existing tests (long argument lists, Unicode, empty-after-trim all handled).
- **`RunningAppMonitor.cs` / `RunningAppService.cs`** — poll loop already guards against overlapping polls (`_refreshInFlight`), catches per-window enumeration failures without aborting the walk, and always closes process handles in a `finally` even on early-return paths. No leak, no silently-killed timer.
- **`HotkeyService.cs`** — every `Register` unregisters first; IDs are rebuilt from a fixed base on every call, so they can't grow unbounded or collide across add/remove cycles.
- **`TrayIconService.cs` / `MessageWindow.cs`** — icon handle ownership (shared vs. owned) is tracked correctly and disposed appropriately; `WndProc` wraps its callback in try/catch and always falls through to `DefWindowProc`, so an exception in a message handler can't wedge the message pump.
- **`WindowChrome.cs` / `AcrylicBackdropManager.cs`** — no handle allocation in the former; the latter subscribes exactly once per window and is disposed on window close, confirmed via `DockManager.RebuildWindows()` closing every old window before building new ones.
- **`DockWindow.AutoHide.cs` / `Magnify.cs` / `Tooltip.cs`** — timer/subscription lifecycle already correct: single subscription per window instance, timers stopped on window `Closed`, no duplicate-handler accumulation across many snap/pause/resume cycles.

## Verification

- `dotnet build src/Anchor/Anchor.csproj -c Debug -p:Platform=x64` — 0 warnings, 0 errors, after all fixes combined.
- `dotnet test tests/Anchor.Tests/Anchor.Tests.csproj` — 233/233 passing.
- Runtime stress pass against a published build with a throwaway `ANCHOR_DATA_DIR`, seeded with:
  a normal ~22-item spread across two docks and two groups, an item pointing at a nonexistent
  target, and a folder item with 550 entries — exercising H2/H3/H4 directly. Repeated fly-out
  open/close cycles, repeated launches of the missing-target item, rapid hover-between-groups
  (exercising H5), and Settings/Search open-close cycles were driven against the running process
  while sampling handle/GDI-object count and memory and tailing `anchor.log` for `UNHANDLED:`
  lines. Four separate launches, ~220 driven UI actions total.

### Empirical runtime results

| Scenario | Outcome |
|---|---|
| Big-folder fly-out, 40 opens total (H2/H3) | Consistently capped at exactly 60 entries; opened in ~520-970ms, closed in <300ms every time; resources flat across a dedicated 20-iteration rerun (handles ~1550, GDI 132, working set ~230MB throughout). |
| Nonexistent-target item, 6 clicks (crash-report C-scope, `Launcher`) | Every click logged a graceful `Launch FAILED: Win32Exception: ... The system cannot find the drive specified.`; process stayed alive every time. |
| Settings open/close, 45 cycles across runs (all pages) | Functionally clean every cycle — but a dedicated 25-cycle rerun surfaced a genuine, previously-unreported resource leak (see [`crash-report.md` findings C6/C7](crash-report.md)): strictly linear GDI/handle/memory growth, not caught by any of the four static audits since it only shows up under repeated real use. C6's fix substantially reduced memory growth and eliminated a latency creep; the remaining GDI/handle growth (also present on EditWindow/AddNewWindow) is still open — see C7. |
| Quick-launch Search, 26 open/close + type/clear cycles | Clean; resource use rose modestly then leveled off (170MB→234MB over 20 cycles, decelerating; GDI oscillated 69-99 with no monotonic trend) — a one-time warm-up cost, not a leak. |
| Drag-and-drop onto the dock | **Skipped** — real shell drag-and-drop needs a foreground `DoDragDrop` source with a shell `IDataObject` and injected mouse input; building that harness would have consumed the run's remaining time budget for one scenario. **Still needs manual verification**: drag a file onto the dock normally, and separately drag one that pauses ~1s over the dock before dropping, to confirm the drop-shell watchdog (C4 in the crash report) doesn't misfire now that it stops itself on window close. |
| Rapid hover-toggle between two adjacent groups, 99 togglings total (H5) | Correct throughout — no stuck-open bars, dock never vanished, no crash. Resources showed a bounded sawtooth (USER objects rising then dropping by ~60 every several iterations) consistent with GC reclaiming short-lived popup wrappers, not a leak. |

One apparent 15-second stall reopening the folder fly-out (1 occurrence in ~40 opens) was traced to
the *test harness's* own close mechanism (an `Escape` keystroke depending on OS focus that a UIA
`Invoke`-click doesn't guarantee) rather than an app bug — switching to a physical, position-
addressed click to close eliminated it across a clean 20/20 rerun. Recorded here so a future test
pass doesn't waste time chasing the same harness artifact as if it were an app hang.

## Residual risk / recommendations

- H1's 300ms bounded wait means a genuinely-slow-but-eventually-successful shortcut resolution can
  take a couple of poll cycles to reflect a running-state change — an acceptable trade for never
  blocking the UI thread; revisit only if that lag becomes user-visible in practice.
- **Real shell drag-and-drop onto the dock was not exercised by the runtime stress pass** (see the
  empirical results table above) — it needs a foreground `DoDragDrop` source and injected input
  that was out of the pass's time budget. Manually verify: a normal drag-drop still adds an item,
  and a drag that lingers ~1s over the dock before dropping doesn't confuse the drop-shell watchdog
  (crash-report finding C4) now that it stops itself on window close.
- The Settings-window leak (crash-report findings C6/C7) is a reminder that the four static
  audits, thorough as they were, missed a real bug that only showed up under *repeated realistic
  use* — the runtime stress pass is not optional busywork alongside static review, it's where this
  one was actually found. Any future crash/hang pass should budget time for it. It's also a
  reminder that a plausible-looking fix (C7's backdrop disposal) needs its own empirical
  re-verification, not just a build/test pass — it measured to zero effect despite being correct
  code, and the underlying GDI/handle growth is still open.
- Any *new* code path that touches a file/folder/shortcut target should default to assuming the
  target might be on an unreliable volume — see the `crash-hang-review` skill's async/UI-thread
  section before adding another synchronous call in a click/drop/poll handler.
