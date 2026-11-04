using System.Diagnostics;
using Anchor.Models;
using Microsoft.Win32;

namespace Anchor.Services;

/// <summary>Launches a dock item via the shell (handles apps, files, folders, .lnk and URLs).</summary>
public static class Launcher
{
    public static void Launch(DockItem item)
    {
        Diag.Log($"Launch requested: kind={item.Kind} name='{item.DisplayName}' target='{item.Target}'");
        if (item.IsSeparator || string.IsNullOrWhiteSpace(item.Target))
        {
            Diag.Log("Launch skipped: separator or empty target");
            return;
        }

        // Every check and call below can touch the file system or hand off to the shell, and a
        // dock item's target can be on a network share or removable drive that has since gone
        // away. File.Exists and Process.Start (via ShellExecute) can both block for the OS's own
        // SMB/mount timeout against a path like that — tens of seconds, sometimes much longer —
        // and this is always reached directly from a click, a drop or a hotkey on the UI thread.
        // Doing the actual launch on a thread-pool thread means a stalled target only ever costs
        // that thread; the dock keeps drawing and responding to input either way.
        Task.Run(() => LaunchCore(item));
    }

    private static void LaunchCore(DockItem item)
    {
        // A "File" item with no registered handler doesn't throw and doesn't visibly open
        // anything — ShellExecute silently spins up a helper process that exits on its own.
        // Detect that up front and hand it to the shell's own "Open with" picker instead of
        // clicking the item and nothing happening.
        if (item.Kind == DockItemKind.File && File.Exists(item.Target) &&
            !HasFileAssociation(Path.GetExtension(item.Target)))
        {
            Diag.Log($"Launch: no app associated with '{Path.GetExtension(item.Target)}' — opening the picker");
            OpenWithPicker(item.Target);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.Target,
                UseShellExecute = true, // resolves associations, shortcuts, and default browser for URLs
            };

            if (item.Kind == DockItemKind.Application && !string.IsNullOrWhiteSpace(item.Arguments))
                psi.Arguments = item.Arguments;

            // Give apps a sensible working directory — but never let resolving it block the
            // launch itself (a bad path here must not stop the item from opening).
            var dir = TryGetWorkingDirectory(item);
            if (dir is not null)
                psi.WorkingDirectory = dir;

            Process.Start(psi);
            Diag.Log("Launch: Process.Start succeeded");
        }
        catch (Exception ex)
        {
            Diag.Log($"Launch FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts an app with files as its arguments — what dropping a file onto an app's icon does,
    /// the same way dropping it onto that exe in Explorer would.
    /// <para>
    /// One process with every path passed to it, rather than one process per file: that is what
    /// the shell does, and it is what an editor or a viewer expects (opening five files in five
    /// windows of the same app is rarely what was meant). The item's own configured
    /// <see cref="DockItem.Arguments"/> come first, so a pinned "app with switches" keeps them.
    /// </para>
    /// </summary>
    public static void LaunchWith(DockItem item, IReadOnlyList<string> paths)
    {
        if (item.Kind != DockItemKind.Application || string.IsNullOrWhiteSpace(item.Target))
            return;

        // Same reasoning as Launch: this runs synchronously on the UI thread as part of a drop
        // handler, and Process.Start/the working-directory check below can stall against a target
        // on a since-disconnected network or removable drive. Push the actual work off the UI
        // thread so that stall can't freeze the drop gesture (or the dock) with it.
        Task.Run(() => LaunchWithCore(item, paths));
    }

    private static void LaunchWithCore(DockItem item, IReadOnlyList<string> paths)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = item.Target,
                Arguments = BuildArguments(item.Arguments, paths),
                UseShellExecute = true,
            };

            var dir = TryGetWorkingDirectory(item);
            if (dir is not null)
                psi.WorkingDirectory = dir;

            Process.Start(psi);
            Diag.Log($"LaunchWith: started '{item.Target}' with {paths.Count} file(s)");
        }
        catch (Exception ex)
        {
            Diag.Log($"LaunchWith FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the command line for <see cref="LaunchWith"/>: the item's own configured arguments
    /// first, then each path quoted.
    /// <para>
    /// One string rather than <see cref="ProcessStartInfo.ArgumentList"/>, because an item may
    /// already carry a raw <see cref="DockItem.Arguments"/> string and <c>ProcessStartInfo</c>
    /// refuses to have both — setting <c>Arguments</c> and <c>ArgumentList</c> together throws.
    /// </para>
    /// <para>
    /// Every path is quoted whether or not it contains a space: quoting unconditionally is what
    /// makes the result predictable, and an app that can't cope with a quoted path it asked for
    /// can't cope with the shell either. Simple quoting is sufficient — a Windows path cannot
    /// contain a double quote, so nothing here can escape its own quotes — but any that somehow
    /// does is dropped rather than allowed to split one argument into several.
    /// </para>
    /// </summary>
    public static string BuildArguments(string? configured, IReadOnlyList<string> paths)
    {
        var arguments = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(configured))
            arguments.Append(configured.Trim());

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (arguments.Length > 0)
                arguments.Append(' ');
            arguments.Append('"').Append(path.Replace("\"", string.Empty)).Append('"');
        }

        return arguments.ToString();
    }

    /// <summary>
    /// True if Windows has an app registered to open <paramref name="extension"/> (e.g. ".md"),
    /// checked the same way the shell resolves it: the per-user "UserChoice" override first
    /// (also covers packaged/Store apps), then the classic HKEY_CLASSES_ROOT ProgId. Never
    /// throws; an inconclusive lookup returns true so it never blocks a launch that might
    /// otherwise work fine.
    /// </summary>
    private static bool HasFileAssociation(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return false;
        try
        {
            using (var userChoice = Registry.CurrentUser.OpenSubKey(
                       $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice"))
            {
                if (userChoice?.GetValue("ProgId") is string progId && !string.IsNullOrEmpty(progId))
                    return true;
            }
            using (var classesRoot = Registry.ClassesRoot.OpenSubKey(extension))
            {
                if (classesRoot?.GetValue(null) is string progId && !string.IsNullOrEmpty(progId))
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Summons the shell's own "How do you want to open this file?" picker.</summary>
    private static void OpenWithPicker(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                ArgumentList = { "shell32.dll,OpenAs_RunDLL", path },
                UseShellExecute = true,
            });
            Diag.Log("Launch: 'Open with' picker invoked");
        }
        catch (Exception ex)
        {
            Diag.Log($"Launch: 'Open with' picker FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// True when <see cref="DockItem.Target"/> is <em>expected</em> to be a real path on disk, so
    /// "Open file location" belongs in the item's menu. Checked against the kind alone — no
    /// <see cref="File.Exists"/>/<see cref="Directory.Exists"/> here — because this runs every time
    /// a menu is built (every right-click), and a target that's actually an unreachable UNC path or
    /// a disconnected mapped drive would stat the network and could freeze the whole dock just to
    /// decide what a menu should say. <see cref="OpenFileLocation"/> does that real check itself,
    /// on the one right-click in a hundred that actually chooses the entry.
    /// <para>
    /// This can say yes for a target that turns out not to have a location — a "Shortcut" add
    /// resolves to <see cref="DockItemKind.File"/> just as a real file does (see
    /// <c>DockItemFactory.Classify</c>), but its target can be a shell command or URI like
    /// <c>"ms-settings:"</c> — in which case the entry is shown but clicking it quietly does
    /// nothing, which is the safer failure than a menu that can hang.
    /// </para>
    /// </summary>
    public static bool SupportsFileLocation(DockItem item) =>
        item.Kind is DockItemKind.Application or DockItemKind.File or DockItemKind.Folder &&
        !string.IsNullOrWhiteSpace(item.Target) &&
        // A Start-menu app is an entry in a virtual folder, not a file, so it has no location to
        // open. This one is known up front — no filesystem call needed to rule it out.
        !DockItemFactory.IsAppsFolderTarget(item.Target);

    /// <summary>
    /// Reveals the item's target in Explorer: opens its containing folder with the target itself
    /// selected — the same "Open file location" a Windows shortcut's own right-click menu offers.
    /// A no-op (besides a log line) for anything that isn't really a path on disk.
    /// </summary>
    public static void OpenFileLocation(DockItem item)
    {
        if (!SupportsFileLocation(item))
        {
            Diag.Log($"OpenFileLocation skipped: '{item.Target}' is not a path on disk");
            return;
        }

        // Called synchronously from a context-menu click on the UI thread. The existence check
        // just below is exactly the kind of call that can stall for the OS's own SMB/mount
        // timeout against a target whose network share or removable drive has since gone away —
        // do it, and the Process.Start that follows, on a thread-pool thread so a stale item can't
        // freeze the dock just because its menu was clicked.
        Task.Run(() => OpenFileLocationCore(item));
    }

    private static void OpenFileLocationCore(DockItem item)
    {
        if (!(File.Exists(item.Target) || Directory.Exists(item.Target)))
        {
            Diag.Log($"OpenFileLocation skipped: '{item.Target}' is not a path on disk");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                // No space after the comma: that's the canonical form Explorer's /select expects.
                // A Windows path can't contain a double quote, so nothing else here needs escaping
                // — except a trailing backslash (a drive root like "C:\", or a UNC share root),
                // which QuoteForCommandLine doubles so the parser reads it as a literal backslash
                // rather than as escaping the closing quote.
                Arguments = $"/select,{QuoteForCommandLine(item.Target)}",
                UseShellExecute = true,
            });
            Diag.Log($"OpenFileLocation: revealed '{item.Target}'");
        }
        catch (Exception ex)
        {
            Diag.Log($"OpenFileLocation FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wraps a path in quotes for a raw Win32 command line — the convention
    /// <see cref="ProcessStartInfo.Arguments"/> expects, since (unlike <c>ArgumentList</c>) it does
    /// no escaping of its own. Doubles a trailing run of backslashes before the closing quote: the
    /// standard argv-parsing rule is that backslashes immediately before a quote are escapes for
    /// it unless there's an even number of them, so a path ending in exactly one — a drive root
    /// like <c>C:\</c>, or a UNC share root — would otherwise have its closing quote read as
    /// escaped rather than as ending the quoted argument.
    /// </summary>
    private static string QuoteForCommandLine(string path)
    {
        int trailingBackslashes = path.Length - path.TrimEnd('\\').Length;
        return trailingBackslashes == 0
            ? $"\"{path}\""
            : $"\"{path}{new string('\\', trailingBackslashes)}\"";
    }

    /// <summary>Working directory for a real local file/app, or null. Never throws.</summary>
    private static string? TryGetWorkingDirectory(DockItem item)
    {
        if (item.Kind is not (DockItemKind.Application or DockItemKind.File) ||
            DockItemFactory.IsAppsFolderTarget(item.Target))
            return null; // folders, web links, shell commands: let the shell decide
        try
        {
            var dir = Path.GetDirectoryName(item.Target);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
        }
        catch
        {
            return null;
        }
    }
}
