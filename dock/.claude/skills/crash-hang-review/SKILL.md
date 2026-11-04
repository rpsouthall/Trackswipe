---
name: crash-hang-review
description: Checklist for finding and preventing app crashes and hangs in Anchor — config-load resilience, native-interop/handle lifecycle, timer teardown, async/UI-thread blocking, and load-testing patterns. Use when asked to investigate crashes or hangs, before shipping a release, when Partner Center health data or a user reports a crash/freeze, or when reviewing new drag-and-drop, native-interop, timer, or async code.
---

# Crash & hang review — checklist

Anchor's `App.xaml.cs` logs every unhandled exception to `%Temp%\anchor.log` via `Diag.Log` but
**deliberately leaves `e.Handled = false`** — nothing is silently swallowed, so any exception that
reaches the UI thread crashes the whole process. Anchor is also an **always-on** utility (runs for
days/weeks between reboots, polling and animating continuously), so bugs that only surface after
many repetitions or long uptime are as real a risk as ones that fire on the first click. This
checklist is grounded in a concrete audit (2026-09) that found and fixed 10 crash/hang bugs across
the app; use it the same way whether you're chasing a specific Partner Center report or doing a
general pass before a release.

Work through the sections in order. Each is a class of bug this codebase has actually shipped —
don't skip one just because "it doesn't feel like where the bug would be."

## 1. Startup / config-load resilience

A bug here is the worst kind: it crashes on **every** subsequent launch, because the bad state that
caused it is still on disk next time. Check:

- [ ] `DockStore.Load()` (or wherever config is deserialized): does a read/parse failure fall back
      to defaults *and* preserve the original file (rename/copy aside), or does it silently let a
      transient failure (antivirus lock, disk hiccup) get overwritten by freshly-seeded defaults on
      the very next save? Losing a user's real config to a one-off read glitch is a data-loss bug,
      not just a crash bug — check for both.
- [ ] Does the read retry briefly on a locked-file `IOException` before giving up? A momentary lock
      is not the same as corruption.
- [ ] After deserializing, walk every place a JSON `null` could survive into a reference-typed
      collection element (an array entry, a nullable child list defaulted to `null` by an explicit
      `"Field": null` in hand-edited JSON) and strip it. `dock.json`-shaped files are documented as
      user-editable — assume a human has broken one by hand.
- [ ] Does building each independent unit from config (one dock window, one profile) happen inside
      its own try/catch, so one bad entry can't take the rest of the app down with it? Prefer
      "skip and log the one bad item" over "crash before any window exists."
- [ ] Is the actual file write atomic (write to a temp file in the same directory, then
      move/replace) so a crash or power loss mid-save can't itself produce the corrupt file that
      trips the read path above?

## 1a. Short-lived windows and their backdrop/composition resources

- [ ] Any window that's constructed fresh per open and torn down on close (a dialog, an editor, a
      settings window — as opposed to a single long-lived window kept for the app's lifetime):
      confirm whatever it does with `SystemBackdrop` (`new MicaBackdrop()`, `new
      DesktopAcrylicBackdrop()`, or a custom `SystemBackdropConfiguration`/controller) is explicitly
      released in its `Closed` handler (`SystemBackdrop = null;`, or `Dispose()` on whatever manages
      it), not left for the CLR object's eventual garbage collection to clean up. Do this
      regardless — it's correct practice and the documented WinAppSDK backdrop-swap pattern — but
      **don't assume it's the fix for a measured GDI/handle leak without re-verifying empirically**:
      in this app, four dialog windows all shared this exact gap, but adding the fix measured *zero*
      change in per-open GDI/handle growth (see the `project-anchor-dialog-window-gdi-growth-unresolved`
      memory) — the growth had a different, still-unidentified cause. Treat this as one plausible
      hypothesis to test, not a diagnosis to declare solved on code-review grounds alone.
- [ ] More generally: a resource-growth symptom that survives an obvious first fix (here, a managed
      event-subscription leak, which *did* measurably help) is a sign to keep looking rather than
      declare victory. Sample USER objects alongside GDI objects/handles — USER objects staying flat
      while GDI/handles keep climbing points at compositor/DWM-backed native resources rather than
      the managed visual tree, but don't stop at the first plausible native-resource suspect either:
      verify each hypothesis with a fresh before/after measurement before moving to the next one.
- [ ] If a resource-growth measurement is **perfectly deterministic** (the same delta every single
      cycle, unaffected by changing timing/settle delays before sampling) rather than noisy or
      GC-dependent, that's a signal the cause is a fixed per-construction native allocation — or an
      artifact of the test environment's own composition/DWM behavior — not a managed-code
      timing/lifetime bug. At that point, guessing at further C# disposal patterns has a low hit
      rate: get a real GDI-handle-type breakdown (GDIView, Process Explorer's handle diff, an ETW
      composition trace) or re-run the same measurement on different (ideally non-virtualized)
      hardware before spending another round on speculative code changes.

## 2. Native interop & OS-handle lifecycle

Anchor P/Invokes shell32/user32 directly (`Interop/NativeMethods.cs`) for icons, drag/drop, window
chrome, and the tray/hotkey message window. For every native handle acquired:

- [ ] Is it released on **every** exit path, not just the happy one — `finally`, not just after the
      last statement? (`Marshal.FreeCoTaskMem` for a pidl, `DestroyIcon` for an `HICON`,
      `CloseHandle` for a process handle, `UnregisterHotKey` for a registered hotkey ID.)
- [ ] For an `out`/`ref` handle from a function that can fail: confirm the API actually
      zeroes/nulls it on failure (check the docs, don't assume) before trusting a
      `finally { if (handle != 0) Free(handle) }` guard — freeing a garbage pointer is an
      access-violation crash, not a leak.
- [ ] Does every native call happen on the thread it needs to (usually the UI/dispatcher thread for
      anything tied to a specific HWND)? An `await` without `ConfigureAwait(false)` returns to the
      UI thread by default in this app (it installs a `DispatcherQueueSynchronizationContext`) —
      confirm nothing has *added* a `Task.Run`/background hop ahead of a native call that assumes
      thread affinity.
- [ ] For something that repeats for the life of the process (icon loads, running-app polls): does
      the handle get freed on *every* iteration, not just leaked slowly? A one-HICON-per-icon-load
      leak is invisible in a quick test and only shows up as GDI-handle exhaustion (which can hang
      or crash the whole desktop, not just Anchor) after real uptime.

## 3. Timer & event-subscription lifecycle

Anchor owns several `DispatcherQueueTimer`s (auto-hide poll/slide, magnify, tooltip, drag-reorder
watch, drop-shell watchdog) and opens/closes UI surfaces repeatedly (group/folder fly-outs,
Settings, Search).

- [ ] Every timer created against a specific window is either stopped from that window's shared
      `Closed` handler, **or** — if it's created lazily well after construction (so it might not
      exist yet when the shared handler is wired up) — stops itself via its own
      `Closed += (_, _) => timer.Stop()` at creation time. A timer left running against a closed
      window that then touches anything on that window (position, size, XAML elements) throws
      unhandled on the very next tick — nothing above a bare `Tick` handler catches it.
- [ ] A `Tick`/callback body that can throw from something outside your control (reading cursor
      position, a native call, touching a collection another handler might mutate) should not be
      able to propagate — wrap it, or make the failure mode "stop cleanly and log", not "throw into
      the dispatcher."
- [ ] For anything that can be **superseded before it finishes closing** (one fly-out replaced by
      another in the same synchronous call — hovering straight from one group to the next, drilling
      into a folder stack): does the old instance's asynchronous `Closed`/completion event still
      assume it's the current one? If two instances can both fire "I'm done, clean up shared state"
      and only the second should win, track *which instance* owns that shared state explicitly
      (a `_activeXyz` field checked with `ReferenceEquals` before acting) rather than assuming
      last-fired-wins is also last-opened-wins.
- [ ] Repeated open/close of the same kind of UI surface (fly-out, tooltip, context menu) should not
      accumulate a new event subscription per open — one subscription per instance's own lifetime,
      not one per the *type* of surface.
- [ ] For a row/element built against a long-lived model object (a `DockItem` owned by
      `DockManager.Config`, not by the window showing it) that subscribes to that object's own
      event (`PropertyChanged`, etc.) to stay in sync: if the only teardown is that row's own
      `Unloaded`, confirm it actually fires in **every** case the code is relying on it for —
      `Unloaded` fires reliably when an element is removed from a still-*loaded* tree (a list
      rebuild clearing and re-adding rows while the window stays open), but does **not** reliably
      fire on every descendant purely because the containing `Window` closed. A window that's
      closed and reopened many times over an always-on session, with per-row subscriptions that
      only clean up via a rebuild-time `Unloaded`, leaks one subscription per row per open onto a
      shared object that outlives the window — found empirically in this app's Settings window
      (linear GDI/handle growth, no plateau, invisible in a single manual test). Fix: have the
      window's own `Closed` handler drive the same teardown explicitly (e.g. clear the hosting
      panel's `Children`, which re-triggers the same `Unloaded` path) rather than trusting it to
      fire on its own.

## 4. Async & UI-thread blocking

- [ ] No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` anywhere reachable from the UI thread
      — this app's sync context makes that a deadlock, not just a stall.
- [ ] Any file-system or shell call reachable from a click/drop/hotkey/poll handler that can touch a
      **network share, removable drive, or mounted image**: assume the volume can vanish or hang,
      and that `File.Exists`, `Directory.Exists`, `Process.Start` (`UseShellExecute: true`), and any
      COM call against such a path (`IShellLinkW`, `SHParseDisplayName`, …) can block for the OS's
      own SMB/mount timeout — tens of seconds or more. If the call sits directly in a UI-thread
      event handler, move the actual work to a thread-pool task (`Task.Run`) rather than adding a
      try/catch — catching the exception after a 30-second freeze still froze the dock for 30
      seconds.
- [ ] For anything that runs on a **recurring timer** and does this kind of I/O per tick (a running-
      app poll resolving shortcut targets, for instance): a bounded wait (e.g. 200–300ms) with the
      real lookup continuing on its own thread-pool task, de-duplicated per key so a persistently
      slow target doesn't accumulate one stuck thread per tick, is the right shape — not "just make
      it async" (an unbounded await on a UI-thread poll still stalls that poll cycle) and not
      "just catch exceptions" (a stall isn't an exception).
- [ ] Outbound network calls (update checks, telemetry, anything with an `HttpClient`): confirm an
      explicit timeout is set (don't rely on the 100-second default) and that timeout/DNS-failure/
      cancellation are all caught alongside HTTP error status — "silent on any failure" has to
      actually mean *any*.

## 5. Unbounded work disguised as bounded

- [ ] Anywhere a result set is capped (`Take(n)`, `MaxEntries`, a page size): confirm the cap is
      applied **before** the expensive part (a filesystem walk, a sort), not after. `.OrderBy(...)
      .Take(n)` still buffers and sorts the *entire* input before yielding — for a folder listing,
      search filter, or any "top N of everything" pattern, put a hard ceiling on the raw enumeration
      first, then sort/rank within that ceiling.
- [ ] For anything driven by user-supplied scale (item count across all docks, folder contents,
      search-query length): sanity-check the actual algorithmic shape (no accidental O(n²) re-sort
      per keystroke, no nested loop over the same collection) rather than assuming "it's fine, it's
      just a dock."

## 6. Verification

Static review isn't enough by itself — confirm each fix compiles, is covered, and (where
practical) actually runs:

- [ ] `dotnet build src/Anchor/Anchor.csproj -c Debug -p:Platform=x64` — 0 errors/warnings.
- [ ] `dotnet test tests/Anchor.Tests/Anchor.Tests.csproj` — full pass. Add a regression test for
      any data-shape bug (a null-entry-in-JSON crash, an argument-quoting edge case) rather than
      relying on manual re-testing catching it next time.
- [ ] For anything touching drag-and-drop, timers, or long-running services, do at least one real
      run: publish the app (`dotnet publish src/Anchor/Anchor.csproj -c Debug -p:Platform=x64
      --self-contained true -o <dir>`, matching `scripts/run-ui-tests.ps1`), launch it against a
      **throwaway** `ANCHOR_DATA_DIR` (never the real `%AppData%\Anchor`), and seed that directory's
      `dock.json` with edge cases: an item whose target no longer exists, a folder item pointing at
      a directory with hundreds/thousands of entries, a group with several children. Drive the
      specific interaction repeatedly (open/close a fly-out ~10x, click a dead item, hover between
      two groups rapidly) and confirm the app stays responsive and `anchor.log` shows no
      `UNHANDLED:` lines. Kill the process and delete the throwaway data dir when done.
- [ ] For a leak-shaped fix (handles, subscriptions, timers): sample `(Get-Process
      Anchor).HandleCount` and working-set memory a few times across a repeated-action run — flat
      is fine, a steady climb across identical repeated actions is a leak even if nothing crashed
      during the sample window.

## Reporting

When this checklist surfaces real findings, write them up the way the 2026-09 audit did:
[`docs/crash-report.md`](../../../docs/crash-report.md) and
[`docs/hang-report.md`](../../../docs/hang-report.md) — one row per finding with file:line, a
concrete failure scenario (not just a category), severity, and fix status. Findings investigated
and ruled out are worth recording too (briefly, with why) so the next pass doesn't re-walk the same
ground.
