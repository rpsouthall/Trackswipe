# Changelog

All notable changes to Anchor are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); dates are when the change was made,
not necessarily when it shipped in a release.

## [Unreleased]

#### Added

- **Open apps join the dock, as on a Mac.** An app that isn't pinned appears at the end of the
  first dock, after a divider, as soon as it opens a window, and leaves as soon as its last window
  closes. Windows announces windows coming and going, so this follows within a moment rather than
  waiting on a poll. Store apps are named and launched from their Start menu entries, and windows
  on other virtual desktops still count as open. Right-click one for **Keep in dock** (or drag it
  in among the pinned icons) and **Quit**; pinned apps that are open get **Quit** too. Clicking an
  open app's icon while it is in front minimizes it, as the taskbar does. Settings ▸ General ▸
  **Open apps in the dock** turns it off.
- **Dodge windows, as Dash to Dock does.** An auto-hiding dock now stays in view until a window
  of the app in front overlaps it, and slides back as soon as none does — switching to an app
  that leaves it clear, or restoring a maximized window, brings it back without reaching for the
  edge (which still reveals it). Settings ▸ Docks ▸ **Dodge windows**, on by default; off, the
  dock hides whenever the cursor leaves, as before.
- **Keep centred.** A dock now stays centred as it grows and shrinks — apps opening and closing,
  items added — instead of growing from wherever it was last dropped: a snapped dock stays in the
  middle of its edge, a floating one around its own middle. Settings ▸ Docks ▸ **Keep centred**,
  on by default.
- **Hide the settings button.** Settings ▸ General ▸ **Settings button** turns the gear (and its
  divider) off; Settings is still on the dock's right-click menu and the tray icon.

#### Fixed

- **Notepad and Calculator never showed as running.** The dock's starter icons point at the
  `System32` launchers, which start the Store versions under a different exe; an icon whose exe
  doesn't match now falls back to the open app's name.

## [v1.2.5]

#### Changed

- **About links are easier to find.** The About page now groups rating, GitHub stars, feedback
  and issue reporting under **Feedback & support**, and keeps the license, privacy policy,
  website and changelog together. The support section also has its own icon.

## [v1.2.4]

#### Added

- **Group icon colour.** A group's editor has a **Colour** row where other items have their
  target: the theme's own colour, eight presets from the Windows palette, and a custom swatch that
  opens a full colour picker. The swatches preview on the editor's icon and land with **Save**
  (Escape discards them, like the name). The colour is stored on the item as `IconColor`
  (`#RRGGBB`) and drawn on the dock strip, its drag ghost and the Settings ▸ Apps & links list. It
  only tints a glyph — a group showing an image icon greys the row out — and is ignored while
  Windows' High Contrast theme is on.

#### Fixed

- **The app icon sat on a blue square** on the taskbar, desktop and Start's app list in the Store
  build. The package only shipped "plated" `Square44x44Logo.targetsize-*` icons, which Windows
  draws on a backplate of the manifest's `BackgroundColor` — and `transparent` there means the
  user's accent colour. Each size now has `altform-unplated` and `altform-lightunplated` variants
  (the same transparent artwork), which Windows draws as-is.

## [v1.2.3]

### Add to Dock: drag 'n drop

#### Added

- **Drop anything onto the Add to Dock window.** Apps (Start menu apps included), files, folders,
  shortcuts and web links dropped anywhere in the window — whichever type is selected — fill the
  form in: the matching type is selected, and the path and display name are filled in to check or
  edit before pressing **Add to Dock** (which takes focus, so Enter finishes it). Several items
  dropped together are listed with their type, name and path, and added with one button; each can
  be taken off the list first. A new **Drag 'n drop** tile beside Group shows an empty drop zone.

#### Changed

- The eight type tiles now share one row (star-sized columns, ~81 DIP each, instead of seven fixed
  88 DIP tiles). The "is this text a web address?" rule the dock strip used for dropped text moved
  to `DockItemFactory.TryNormalizeWebUrl` so both windows apply the same one.

### Round 9 — crash and hang hardening

Prompted by Microsoft Partner Center health insights reporting crashes and hangs in the field. A
full-codebase audit (four parallel passes plus a runtime stress test against a published build)
found and fixed ten concrete bugs, written up in [`docs/crash-report.md`](docs/crash-report.md) and
[`docs/hang-report.md`](docs/hang-report.md); the checklist behind the audit is now a standing
skill, `crash-hang-review`, for the next time this needs doing.

#### Fixed

- **A corrupt or momentarily-locked `dock.json` could silently cost a user their whole
  configuration.** `DockStore.Load()` fell back to defaults *in memory* on any read/parse failure,
  and the very next save then overwrote the still-present original file with those fresh
  defaults — an antivirus scan catching the file at the wrong instant, or a one-off truncated
  write, permanently erased a real dock. Reads now retry briefly past a locked-file `IOException`,
  and a file that's still unreadable after that is copied aside as `dock.json.unreadable-<stamp>.bak`
  before defaults are handed back, so nothing is discarded without a copy surviving somewhere.
- **A hand-edited `dock.json` with a stray JSON `null` in an items array crashed the app on every
  subsequent launch.** A `null` list entry deserializes cleanly (these are reference-type lists) and
  reached `DockWindow`'s constructor, which throws walking it — before any window exists, so the bad
  entry was never removed and the crash recurred every time. `DockConfig.Migrate()` now strips null
  dock/item/child entries and repairs an explicitly-nulled `Children` list back to empty.
- **One bad dock profile could take the whole app down with it.** Building each dock's window during
  startup (and during the language-change/import rebuild) is now individually try/caught and logged,
  so a single corrupt or unbuildable profile is skipped rather than crashing every dock.
- **Dragging a file onto the dock, then removing that dock mid-drag, crashed the app ~200ms later.**
  The new drop-shell watchdog timer (Round 8) didn't stop itself on window close the way every other
  polling timer on the window does, so it kept ticking against a closed `AppWindow` and threw reading
  its position. It now stops itself on `Closed`.
- An unguarded `DataView.Contains` call in the per-icon drag-over handler could throw on a live
  drag's own timing hazard (a call the code right next to it already knew to guard) and crash the
  process mid-drag. Now goes through the same guarded check as its neighbor.
- **Anything that could stall on a removed network share or unplugged drive was moved off the UI
  thread**, since any of it freezing the dock reads as a hang to a user: launching an item, dropping
  files on an app, revealing an item's file location, and — the worst of these — resolving a pinned
  shortcut's target, which ran synchronously on every ~2-second running-app poll and could freeze
  every dock for as long as an unresponsive volume stayed down.
- **Opening a folder stack on a very large folder could freeze the dock.** The listing capped its
  *output* at 60 entries but sorted the entire folder first — over a slow network share that made a
  single click cost a walk of the whole folder. The cap now bounds the raw enumeration itself.
- **A group or folder fly-out closing while a new one opened in the same gesture** (hovering from one
  group straight onto the next, or drilling into a folder stack) could leave the dock's auto-hide
  timer resumed against the wrong bar, letting the dock slide away while its own fly-out was still
  open above it. Only the fly-out that's actually still current can now resume auto-hide.
- **Opening Settings leaked a handler per Apps & Links row, every time.** Found by an empirical
  stress-test pass, not the static audits: each row subscribes to its `DockItem`'s
  `PropertyChanged` to refresh its icon, and the only teardown relied on `Unloaded` firing on
  window close — which it doesn't, reliably. The `DockItem`s are long-lived (owned by
  `DockManager.Config`), so every Settings open pinned one more dangling closure on them; measured
  at +34 GDI objects and +63 handles per open/close cycle, linearly, with no plateau. The window's
  `Closed` handler now clears the Apps & Links list itself, driving the same teardown that already
  worked correctly for a live rebuild. This closed most of the memory growth and all of a creeping
  per-open slowdown, but a re-verification pass found GDI-object/handle growth persisted almost
  unchanged — a second leak underneath the first.
- Settings, the item editor, the new-group dialog and the add-item dialog all set
  `SystemBackdrop = new MicaBackdrop()` in their constructor and never released it, unlike the
  long-lived dock window and the search window, which already dispose their backdrop controller on
  close — a plausible remaining source for the GDI/handle growth above, so each window's close
  handler now also sets `SystemBackdrop = null`. **Measured to make no difference**: a follow-up
  empirical pass found the same deterministic per-open GDI/handle growth on Settings, the editor
  and the add-item dialog alike, unchanged by this fix. Left in (it's correct regardless) but the
  underlying growth is tracked as an open issue — see `docs/crash-report.md` finding C7 — rather
  than claimed as resolved.

### Round 8 — a drop opens the slot it is going to land in

#### Added

- **Dragging something onto the dock now parts the strip around an empty slot where the drop will
  land**, and the drop lands *there* rather than on the end of the strip. It is the same cue a
  reorder already gave — the strip opening up around a cell in flight — so adding an item and moving
  one now read as the same gesture, which is what they are. The slot follows the cursor as it moves
  along the strip, and several files dropped together fill it and the slots after it, in order.
  The empty cell is a transient `DockItem` (`CreatePlaceholder`) that lives only in the dock's
  *visible* collection, never in the profile, so nothing about it can reach the config — and it maps
  back onto the master list through the new `InsertDockItem`, so a hidden neighbour doesn't quietly
  shift where the drop goes.
- Where that slot is comes from `SlotAt`, which is now **shared with the reorder gesture** rather
  than written twice: both place a cell by walking the strip's own (non-uniform) cell extents and
  inserting where the position passes each midpoint, so the two can never disagree about where a
  cell starts.

#### Changed

- **The dock grows from where it is while a drop slot is open**, instead of re-deriving its
  placement. A centered dock re-centers as it widens, which slid every icon half a cell sideways
  the moment the gap opened — and took the gap itself out from under the cursor that asked for it,
  putting the drop one slot off. It settles back to its usual placement once the slot closes,
  whether the drop landed or not.
- **An icon that takes the drop itself keeps the slot open but silences it.** Dropping a file on an
  app, or anything into a group, is announced by that icon swelling, so a second cue beside it
  would contradict it — but *closing* the slot there would shift every cell past it by a full cell
  width and hand the drag to whichever icon slid under the cursor, which promptly re-opens it. So
  the geometry stays put and only the fill goes.
- The slot closes when the drag leaves, is cancelled, or is dropped. A cell's own `DragLeave`
  bubbles up to the strip every time the cursor crosses between two icons, so it is the cursor —
  not the event — that is asked whether the drag has really gone. A watchdog covers a `DragLeave`
  that never arrives, and is careful about the difference between a drag that is *gone* and one
  merely being **held still**: a drag held still raises no `DragOver` either, and someone lining a
  file up between two icons holds it still on purpose, so the slot goes only once the cursor has
  left the dock or the button carrying the drag is up.

### Round 7 — apps can be dragged onto the dock

#### Fixed

- **Dragging an app from the Start menu onto the dock showed the "no entry" cursor and did
  nothing.** An app in Start's list is not a file: it lives in the shell's virtual `AppsFolder`
  namespace and is identified by an AppUserModelID, so the shell offers such a drag as a
  `"Shell IDList Array"` (CFSTR_SHELLIDLIST) and nothing else — no CF_HDROP, which means
  `DataView.Contains(StorageItems)` says there is nothing there. That question was the only one
  the dock asked, so every one of those drags was refused. It now asks whether the drag carries
  shell items *or* storage items, and reads them either way — the items come back from
  `GetStorageItemsAsync` regardless of what `Contains` admitted to, as path-less stand-ins for the
  shell items. New `Services/ShellDrop.cs` resolves each of those: the app's own exe if it has one
  (so a dragged-in Chrome is the same dock item as one added by browsing to it — running
  indicators, arguments and *Open file location* all key off a real path), and otherwise the
  AppUserModelID as a `shell:AppsFolder\<id>` target, which is how a Store app with no exe of its
  own is launched. `IconService` reaches those icons through the item ID list the shell parses that
  target into, since nothing that takes a path can answer for one.
- **An app dropped on the dock is now added as a shortcut wherever it lands**, icon or not.
  Dropping a *file* on an app icon still opens the file with that app, as it always has — but a
  dropped app took that same path, so "open Chrome with Notepad" is what a drop on an icon meant.
  A dock strip is mostly icons, so that made adding an app by dragging it a matter of hitting the
  few pixels between two of them. `Item_DragOver` says "Add to dock" for those drags and lets them
  through to the strip's own handler; the caption is decided from a read of the payload started as
  the drag enters each cell, deliberately without a deferral (a deferral applies DragEnter's own
  "no operation" when it completes, which left the shell drawing the "no entry" cursor over a dock
  that was in fact accepting the drop).
- ***Open file location*** no longer appears for a Store app pinned from the Start menu. Round 6
  gated the entry on kind alone, deliberately, to keep a filesystem call out of building a menu —
  a `shell:AppsFolder\…` target needs no such call to rule out, since it is knowable from the
  target itself that there is no folder to reveal.

### Round 6 — group items become fully reorderable, and Explorer is one right-click away

#### Added

- **A group's fly-out bar is now drag-to-reorder**, the same gesture the main strip already offered
  its own items. Dragging a cell along the bar's own flow slides it past its siblings — a floating
  ghost tracks the cursor while the cell's own slot dims, mirroring the strip's own reorder cue —
  and dragging it clear across the flow still pulls it back out onto the dock, unchanged. Both
  share one cursor-polled gesture in `DockWindow.WatchCellDrag`, since a folder fly-out (a view of
  the disk, not something the user arranges) only ever gets the second half.
- **Settings ▸ Apps & links can now reorder items**, not just show or hide them: every row gets a
  *Move up*/*Move down* pair, for a top-level dock item and for a group's own children alike,
  reaching the same reorder the dock's drag gesture and its right-click "Move left/right" already
  offer — without a mouse drag. Backed by two new methods, `DockWindow.MoveTopLevelItem` and
  `.MoveGroupChild`.
- ***Open file location*** joins the right-click menu on the dock and inside a group's fly-out, for
  any app, file or folder whose target actually exists on disk. It hands Explorer the same
  `/select,"path"` a shortcut's own "Open file location" uses — opening the containing folder with
  the item itself highlighted, rather than launching it. A "Shortcut" add whose target is really a
  shell command (`ms-settings:`, `shell:RecycleBinFolder`) has no location to reveal — the menu
  can't tell without a filesystem check it has no business doing just to decide what to show, so
  the entry still appears (`Launcher.SupportsFileLocation` gates only on kind) but quietly does
  nothing if chosen.

### Round 5 — a group's fly-out stops flickering, and hover becomes a setting

#### Fixed

- **A hovered group's fly-out flickered open and shut** for as long as the cursor rested on the
  icon. Opening the bar makes the strip fire `PointerExited` — the fly-out's light-dismiss layer
  takes the pointer for a moment as the bar comes up — with the cursor still squarely on the icon,
  and the close that fired on that report was undone by the next pointer move, over and over. The
  bar now stays up on the answer to "where is the cursor actually?", polled against the icon's own
  screen rectangle (`DockWindow.CursorIsOverGroupTrigger`) rather than on a pointer event that
  cannot be trusted at exactly this moment. The dock is also named as the bar's
  `OverlayInputPassThroughElement`, so the strip keeps seeing the cursor underneath an open bar
  instead of being cut off from it.
- **A second click on a group re-opened the bar it had just closed.** The press that lands on the
  icon light-dismisses the bar on its way through, so the click handler found nothing left to
  toggle and opened it again; and in hover mode the cursor was still on the icon afterwards, ready
  to re-open it in any case. A close counts as belonging to the click that caused it, and hover is
  held off that icon until the cursor leaves it — so first click shows the bar, second click hides
  it, whichever mode is on.

#### Added

- **Settings ▸ Appearance ▸ *Open groups on hover***, a toggle alongside *Magnify on hover*. On
  by default, which is how groups already behaved; off ignores the cursor entirely and leaves a
  click to both open and close the bar. Stored as `GroupOpenOnHover` in `dock.json`.

### Round 4 — group fly-outs open on hover

#### Added

- **A group's fly-out now opens as soon as the cursor lands on its icon**, the same bar a click
  already opened — closing the gap called out in Round 1's known limits. It rides the same
  per-move tracking that drives the hover highlight and the fast tooltip
  (`DockWindow.TrackStripPointer`), so it needs no dwell timer or separate pointer subscription.
  Landing on a *different* group while one is already open swaps straight to it — closing the
  first bar and opening the second — the way a menu bar swaps top-level menus under the cursor.
  Re-hovering (or clicking) the group already showing is a no-op rather than a restart of the same
  content. A folder fly-out is unaffected and still opens on click only.

### Round 3 — Microsoft Store readiness

A compliance and correctness pass over the MSIX/Store path. Two of these are behavior bugs that
only appear once the app is installed from the Store, which is exactly where they'd have been
found by a user rather than by us.

#### Fixed

- **"Start with Windows" silently did nothing in a packaged build.** `StartupService` wrote the
  per-user `HKCU\…\CurrentVersion\Run` key, but a packaged process's writes under `HKCU\Software`
  are captured in the package's virtualized registry hive, which Windows' autostart never reads —
  the switch would flip, save, and then not start anything after a reboot. The manifest now
  declares a `windows.startupTask` extension and the service drives it through
  `StartupTask.RequestEnableAsync` when packaged, keeping the Run key for the portable zip.
  `Services/PackagedRuntime.cs` is the single place that tells the two apart.
- **A user's own "off" is now respected.** Windows refuses to let an app re-enable a startup task
  the user disabled in Task Manager. `SetLaunchAtStartupAsync` returns what the entry *actually*
  became rather than what was asked for, and the Settings switch springs back with a note pointing
  at Task Manager instead of claiming an autostart that won't happen.
- **`-p:StorePackage=true` failed after building the package.** Generating the symbol package needs
  `mspdbcmf.exe` (a Visual Studio C++ tool); when it is absent the MSIX targets build the `.msix`
  and *then* fail the build with `MSB6011`. `AppxSymbolPackageEnabled` now defaults to false —
  symbols are optional for a Partner Center upload — and can be turned back on with
  `-p:AppxSymbolPackageEnabled=true`. The dead `WinAppSdkCheckForPdbConversion` property, which
  this version of the targets never read, is gone.
- **The shipped manifest claimed `MaxVersionTested="10.0.19041.0"`** regardless of what
  `Package.appxmanifest` said: the packaging targets overwrite it from `$(TargetPlatformVersion)`.
  The target framework moved to `net10.0-windows10.0.26100.0` so the declared value matches the
  Windows the app is actually tested on. `TargetPlatformMinVersion` is unchanged at 10.0.17763.0,
  so the install floor (Windows 10 1809) is the same.

#### Changed

- **The GitHub update check is absent from the Store build**, not merely off by default. The Store
  updates a packaged app itself, so the banner would have sent users to install a second,
  unmanaged copy of the app they already had — and a Store listing that routes users to a build
  distributed elsewhere is a pattern review looks for. `DockManager.UpdateChecksSupported` gates
  the startup check and collapses the whole Settings card. The portable zip is unaffected; it has
  no other updater.
- **Manifest metadata lines up with the Store listing.** The Start-menu/app-list name is now
  "Anchor Dock", matching `Properties/DisplayName` and the reserved name, and the description no
  longer says "for Windows 11" while `MinVersion` admits Windows 10 1809 — both are policy 10.1.1
  ("metadata must accurately describe the product"). The app's own UI still calls itself Anchor.

#### Added

- **A privacy policy** — [`docs/privacy-policy.md`](docs/privacy-policy.md), linked from
  **Settings ▸ About** and destined for the Partner Center listing field. Store policy 10.5.1
  requires one from "Desktop Bridge and Win32 products" specifically, whatever the app collects
  (here: nothing).
- Tests for the Run-key round trip (including that disabling *removes* the value rather than
  blanking it, and that what's written is a quoted absolute path Windows can run) and for the
  packaged/unpackaged split. The packaged half needs an installed MSIX and stays a manual sideload
  check in `docs/microsoft-store-deployment.md` §11.

### Round 2 — the rest of the roadmap

Everything in this section clears the remaining "Roadmap / future enhancements" items from the
README, plus a redesign of the group fly-out. Built on top of the round-1 work below.

#### Added

- **Fly-out bars.** A group no longer opens a boxed menu: it opens a **second dock** out of the
  clicked icon — the same cells, chrome, spacing and corner radius as the strip itself, running
  across the dock's flow (a vertical bar out of a horizontal dock, and vice versa) and always away
  from the snapped edge. Built once in `DockWindow.FlyoutBar.cs` and shared by groups and folder
  stacks, so the two can't drift apart. It keeps the platform `FlyoutPresenter` as its surface —
  restyled to the strip's chrome — because that already carries the Windows 11 material, shadow
  and light-dismiss behavior.
- **Folder fly-outs (macOS-style stacks).** *Show folder contents* on a folder's right-click menu
  makes it list what's inside it in a fly-out bar instead of handing the folder to Explorer.
  Clicking a subfolder drills in; the last cell is always *Open in File Explorer*; a folder entry's
  own menu offers *Pin to dock*. Hidden and system entries are skipped, the list is capped at 60,
  and entries are built fresh on every open — they're a view of the disk, never persisted.
- **Drag an item into a group.** Dragging an icon over the middle of a group swells that group and
  files the item into it on release; the outer thirds still reorder past it. Dragging a cell **out**
  of an open group bar (sideways, across the bar) puts it back on the strip.
- **Drop a file onto an app icon** to open it with that app, and onto a **group** to file it in
  there. The drop cue is the target icon swelling; the drag caption says which of the two will
  happen. Everything else on the strip still adds the dropped item to the dock.
- **Move an item between docks** — *Move to dock ▸* on an item's menu hands the `DockItem` itself
  over, so its custom icon, arguments and shortcut travel with it.
- **Per-item shortcuts.** Any item can be given its own system-wide combination from
  *Shortcut ▸* on its right-click menu, including one inside a group. `HotkeyService` now keys
  registrations by id and routes `WM_HOTKEY` on `wParam`, so the summon shortcut, search and every
  item share one service. Off by default behind a master switch (Settings ▸ Shortcuts) — each one
  claims a combination from every other app — and the assigned set is listed there.
- **Quick-launch search.** A borderless acrylic card, opened by its own shortcut (unassigned by
  default) or from the tray, filtering every item on every dock. Ranks name-prefix over
  name-substring over target, arrow keys wrap, Enter launches, Esc or losing focus dismisses.
- **Icon size / density.** *Small / Medium / Large* in Settings ▸ Appearance. All dock geometry
  now derives from `DockMetrics`, so the cells, icons, glyphs, separators, running dots, the gear
  and the window sizing all move together and can't drift.
- **Glass personalization** — a frostiness slider (30–100%) and an **accent tint** switch that
  colors the acrylic with the Windows accent instead of the taskbar's neutral grey.
- **Magnification.** Icons swell under the cursor on a cosine curve, tapering over two cells
  either side. The swell happens *inside* each cell — see *Known limits* below.
- **Import / export.** Settings ▸ General writes the whole configuration to a file and restores
  one, wholesale and behind a confirmation. The exported file is the same shape as `dock.json`, so
  a backup can also be dropped into `%AppData%\Anchor` by hand.
- **Opt-in update check.** Asks the GitHub releases API whether a newer Anchor exists, and offers
  the release page or "skip this version". **Off by default**, checked at startup only when it is
  on, silent on any failure, and it never downloads or installs anything. A startup check puts
  nothing on screen — the result surfaces through a tray-menu entry and the Settings banner.
- **Code signing in `package-release.ps1`** — `-CertificateThumbprint` (plus `-TimestampUrl` and
  `-SignToolPath`) signs `Anchor.exe` before the zip is built, so the published SHA256 is the hash
  of the signed archive. Releases still ship unsigned; there is no certificate yet.
- **CI workflow** (`.github/workflows/ci.yml`) — builds x64 and ARM64 warning-free and runs the
  unit tests on every push and PR. The UI smoke tests are a separate, opt-in job pinned to a
  self-hosted `windows-desktop` runner, since UI Automation needs an interactive session.
- **Settings ▸ Appearance** and **Settings ▸ Shortcuts** pages, and a tray-menu *Search…* entry.

#### Changed

- **Language changes apply immediately** — the "restart Anchor to finish switching language"
  prompt is gone. Every window is rebuilt in place against the same `DockProfile`s, so items,
  positions and snap state are untouched. `App.Restart` had no other caller and was removed.
- **The auto-hide slide is time-based.** It was a per-frame "move 28% of what's left", which made
  the duration depend on how far the dock had to travel and ended on a snap; it is now a fixed
  220 ms cubic ease-out at ~120 Hz that lands exactly on target.
- **The Settings shortcut capture** moved into a reusable `HotkeyCaptureButton` control, shared by
  the summon shortcut, the search shortcut and the per-item ones. Each announces what it is a
  shortcut *for*, since the visible content is only the combination.
- `DockManager` gained `LaunchOrFocus` and `LabelFor`, so the strip, the fly-out bars, search and
  the shortcuts all resolve "open this item" and "what is this dock called" the same way.
- `DockItem.CellExtentDefault` / `SeparatorExtent` constants are gone; geometry lives on
  `DockMetrics` and items re-read it via `RefreshMetrics()`.

#### Review pass

A pass back over the above, mostly aimed at logic that was correct but untestable.

- **Pure logic moved out of the window classes**, which a unit test can't construct, and covered:
  `Services/ItemSearch.cs` (quick-launch ranking and filtering), `Services/FolderListing.cs` (a
  folder stack's contents and their order), `Launcher.BuildArguments` (the command line built when
  files are dropped on an app) and `DockMetrics.MagnificationAt` (the swell curve). That is 43 new
  tests over behaviour that previously had none — the argument quoting in particular, where a
  dropped path containing a space arriving as two arguments would look like the app was broken
  rather than Anchor.
- **Fixed: dragging across two adjacent icons could leave the drop cue on the wrong one.** The new
  cell's `DragOver` fires before the old cell's `DragLeave`, so the unconditional clear in
  `DragLeave` wiped the swell off the cell the cursor had just arrived at. It now only clears the
  cue if that cell is still the one showing it.
- **Fixed: `BuildArguments` drops blank paths** (a dragged storage item can have no filesystem
  path) and strips any quote inside a path, so a stray one can't close its own quoting and split
  one argument into several.
- **Fixed: the drag-out watch timer** is now stopped when the dock closes and when a second press
  starts, rather than only on the next mouse-release.
- **Settings ▸ Apps rows now show an item's shortcut** and whether a folder opens a stack. A
  per-item shortcut is assigned from an icon's menu on the dock, so without this an item could
  own a system-wide combination that this page said nothing about.
- **The dock's own right-click menu gained *Search…*.** Quick-launch has no shortcut until the
  user assigns one, and the tray menu alone left it undiscoverable from the dock itself.

#### Known limits in this round

- **Magnification stays inside the strip.** The roadmap asked for icons to pop *above* the dock.
  That needs a window taller than the strip with a masked backdrop, and the dock's glass is a real
  `DesktopAcrylicBackdrop` that paints the whole window and cannot be masked — a taller window
  would be a slab of glass with icons along the bottom. Growing the icon within its (fixed) cell
  keeps the strip exactly as tight as it is.
- **The slide is still timer-driven, not Composition.** Hiding moves the *window* off the screen
  edge, and Composition animates content inside a window — it cannot animate an HWND's position.
  Keeping the window still and sliding its content would leave a full-size invisible window over
  the screen edge eating clicks. The roadmap item is retired; the timing fix above is the part
  that was actually visible.
- **Store / winget submission** still isn't done — the manifests remain staged under
  `packaging/winget/`, and landing them needs a signed release and a PR to `microsoft/winget-pkgs`.
- **Translations for the new strings** are machine-assisted like the rest and unreviewed by native
  speakers.
- The **UI tests** now cover the Appearance and Shortcuts pages and a group fly-out, but snap and
  auto-hide are still untested (the harness would have to move the window and watch it slide) and
  the tray menu still has no UI Automation surface at all.

---

### Round 1 — the original six roadmap items

Everything below was implemented in one working session against the six items in the README's
"Roadmap / future enhancements" section, in this order: separators/custom icons → running-app
indicators → grouping → UI smoke tests → multiple/per-monitor docks → ARM64 build (ARM64 landed
first in practice since it was pure build config with no behavioral risk; the config-schema
migration for multi-dock support went last since every other item was easier to build against a
single dock first).

### Added

- **ARM64 build.** `Anchor.csproj` now targets `x64;ARM64`; `-p:Platform=ARM64` builds natively
  for Windows-on-ARM. `scripts/package-release.ps1` gained `-Architecture x64|arm64|both` and
  produces a zip per architecture. Added an arm64 entry to the winget installer manifest.
- **Separators.** A thin divider you can drop anywhere on the dock. Add one from the dock's
  right-click menu (*Add separator*) or the Add window (*Separator* type). Renders as a hairline
  in a narrow 13px cell instead of a full 40px icon slot.
- **Custom icons.** *Change icon…* on an item's right-click menu opens a file picker (PNG, ICO,
  JPG, BMP, GIF, TIF); *Use the default icon* clears it back to the shell/favicon icon.
- **Running-app indicators.** A dot under an icon whose app already has a window open; clicking it
  brings that window forward instead of starting a second copy. **Shift+click** forces a new
  instance, matching the Windows 11 taskbar. Polls every 2 seconds; toggle in
  Settings ▸ General ▸ *Running-app indicators*. Shortcuts (`.lnk`) are resolved to their target
  exe via `IShellLinkW` so a pinned shortcut still matches its running process.
- **Groups.** One dock icon that holds several items and opens them in a fly-out. Right-click an
  item → *Move to group ▸* (an existing group, or *New group…*); a group's own menu offers
  *Ungroup* (puts its contents back on the strip) and *Open group*. Groups don't nest and can't
  hold separators.
- **Multiple / per-monitor docks.** Add a dock from any dock's right-click menu (*Add another
  dock*) or **Settings ▸ Docks ▸ Add dock**. Each dock has its own items, monitor, edge, auto-hide
  and always-on-top setting; theme, language, the global shortcut and start-with-Windows stay
  shared across all of them. The tray icon and global shortcut summon every dock at once. A dock's
  monitor is derived from its stored position, not a separate display id.
- **UI smoke tests** (`tests/Anchor.UITests/`). Launches the published `Anchor.exe` and drives it
  through UI Automation (FlaUI/UIA3): dock appears, seeded items render as named buttons, gear
  opens Settings and its pages switch, Docks page lists a dock, closing Settings doesn't take the
  dock down. Opt-in and excluded from `Anchor.slnx` so `dotnet test` on the solution stays fast
  and headless; run via `scripts/run-ui-tests.ps1`. Each run uses a throwaway `ANCHOR_DATA_DIR`
  so it never touches a real dock.
- **`ANCHOR_DATA_DIR` environment variable** to redirect `dock.json` and the icon cache to a
  custom folder — enables the UI test harness and a portable install from a removable drive.
  Scopes the single-instance mutex too, so a redirected copy runs alongside the everyday one.
- Settings ▸ Docks page: one card per dock (name, monitor picker, position, transpose, auto-hide,
  always-on-top, remove).
- Apps & links page now lists items grouped by dock (when more than one exists) and shows a
  group's children indented underneath it, with a "move out of group" action.

### Changed

- `DockConfig` restructured around `Docks: List<DockProfile>` instead of a single flat set of
  fields. **Old single-dock config files upgrade automatically on first launch** — folded into
  `Docks[0]` with every setting intact, and the file is rewritten once in the new shape.
- `DockItem` gained `Children` (a group's contents) and `IsRunning`/`HasCustomIcon`/`CellExtent`
  and related sizing properties.
- Settings window reorganized: the old single **General** page's position/auto-hide/always-on-top/
  transpose controls moved to the new **Docks** page (they're per-dock now); General keeps theme,
  language, shortcut, running-app indicators, start-with-Windows, and reset.
- Drag-to-reorder math changed from a uniform cell pitch to walking actual per-cell extents, so
  reordering around a separator's narrower slot doesn't misplace the drop target.
- `dock.json` is now written with relaxed JSON escaping so shortcuts like `Ctrl+Alt+A` and
  non-ASCII item names stay human-readable instead of being `\u`-escaped.

### Fixed

- **Published builds failed to start.** `dotnet publish` was not copying `Anchor.pri` (the
  compiled-XAML resource index) into the output, so any published `Anchor.exe` — including the
  already-released `dist/Anchor-win-x64-1.0.0.zip` — crashed on its first window with *"Cannot
  locate resource from `ms-appx:///DockWindow.xaml`"*. Fixed by enabling `EnableMsixTooling` even
  though the app ships unpackaged; that switch is what generates the resource index.
  **The 1.0.0 release zip should be re-cut from a build that includes this fix.**
- `DisplayArea.FindAll().ToList()` threw `InvalidCastException` through its WinRT projection,
  crashing the Settings window while building the new Docks page's monitor list. Now copied out by
  index instead of through LINQ's `IEnumerable` adapter.

### Known gaps in this round

- Translations for the new strings (German, Spanish, French, Hindi, Japanese, Portuguese,
  Simplified Chinese) are machine-assisted, like the existing eight-language set, and unreviewed
  by native speakers.
- The ARM64 build cross-compiles cleanly from an x64 host but has not been run on real ARM64
  hardware.
- Group fly-outs are click-only (no hover-to-open) and filing an item into a group has no drag
  gesture yet — only the right-click *Move to group* menu.

## Earlier history

Everything before this entry predates this changelog; see `git log` for the commit history
(localization for Japanese/Portuguese/Simplified Chinese, MSIX packaging metadata, winget
manifests, the Settings window, and the initial dock implementation).
