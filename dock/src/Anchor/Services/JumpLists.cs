using System.Diagnostics;
using System.Runtime.InteropServices;
using Anchor.Interop;
using Anchor.Models;

namespace Anchor.Services;

/// <summary>One line of a jump list: a file (a shell item) or a task (a shell link), or a divider between tasks.</summary>
public sealed class JumpEntry
{
    internal JumpEntry(string title, object? target, bool isSeparator)
    {
        Title = title;
        Target = target;
        IsSeparator = isSeparator;
    }

    public string Title { get; }
    public bool IsSeparator { get; }

    /// <summary>The COM object behind it: an IShellItem or an IShellLink. Held until the menu closes.</summary>
    internal object? Target { get; }
}

/// <summary>A heading of a jump list ("Recent", "Tasks", or one the app named) and its lines.</summary>
public sealed record JumpCategory(string Title, IReadOnlyList<JumpEntry> Entries);

/// <summary>
/// An app's jump list, as the taskbar shows it on right-click: the categories it files (recent and
/// frequent files, or ones it names itself) and its tasks ("New window", "New InPrivate window").
/// <para>
/// Read when the menu opens and not before — a few COM calls into windows.storage.dll, which reads
/// the app's list from disk, so there is nothing to keep up to date and nothing to hold in memory
/// between menus. Which list belongs to which app is decided by AppUserModelID, the same key the
/// taskbar uses (<see cref="AppIdFor"/>).
/// </para>
/// </summary>
public static class JumpLists
{
    private const int MaxPerCategory = 10;

    /// <summary>
    /// The AppUserModelID an item's jump list is filed under, or null when it can't be told: a Start
    /// menu item's own ID; a packaged app's; the one its open window carries; or the one Start lists
    /// its exe under.
    /// </summary>
    public static string? AppIdFor(DockItem item, RunningApp? running)
    {
        if (RunningAppService.AppIdOf(item) is { } startId)
            return startId;
        if (running?.AppId is { } packaged)
            return packaged;
        if (running is not null && RunningAppService.WindowAppId(running.Window) is { } windowId)
            return windowId;
        var exe = RunningAppService.ExecutablePathOf(item) ?? running?.ExePath;
        if (exe is null)
            return null;
        if (string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            return "Microsoft.Windows.Explorer"; // File Explorer files its list under this, not under its exe
        return StartMenuApps.AppIdFor(exe);
    }

    /// <summary>The app's jump list, categories first and tasks last, as the taskbar orders them. Never throws.</summary>
    public static List<JumpCategory> Read(string appId)
    {
        var result = new List<JumpCategory>();
        JumpCategory? tasks = null;
        bool listedKnown = false;
        try
        {
            var list = ComInterop.Create<ShellInterop.IInternalCustomDestinationList>(ShellInterop.CLSID_DestinationList);
            try
            {
                if (list.SetApplicationID(appId) == 0 && list.GetCategoryCount(out uint count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (list.GetCategory(i, 1, out var category) != 0)
                            continue;
                        switch (category.Type)
                        {
                            case 0: // one the app named
                                string name = NameOf(category);
                                var named = Entries(list, i);
                                if (named.Count > 0)
                                    result.Add(new JumpCategory(name, named));
                                break;
                            case 1: // Frequent (1) or Recent (2), kept by Windows itself
                                listedKnown = true;
                                if (Known((int)(category.NameOrSubType & 0xFFFF)) is { } known)
                                    result.Add(known);
                                break;
                            case 2:
                                var entries = Entries(list, i);
                                if (entries.Any(entry => !entry.IsSeparator))
                                    tasks = new JumpCategory(Loc.Get("Jump.Tasks"), entries);
                                break;
                        }
                    }
                }
            }
            finally
            {
                ComInterop.Release(list);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"JumpLists: custom list for {appId}: {ex.Message}");
        }

        // An app that files no list of its own still gets Windows' recent files, as on the taskbar.
        if (!listedKnown && result.Count == 0 && Known(2, appId) is { } recent)
            result.Add(recent);
        if (tasks is not null)
            result.Add(tasks);
        return result;

        JumpCategory? Known(int subType, string? id = null)
        {
            bool frequent = subType == 1;
            var entries = Documents(id ?? appId, frequent ? ShellInterop.ADLT_FREQUENT : ShellInterop.ADLT_RECENT);
            return entries.Count == 0 ? null : new JumpCategory(Loc.Get(frequent ? "Jump.Frequent" : "Jump.Recent"), entries);
        }
    }

    private static string NameOf(ShellInterop.APPDESTCATEGORY category)
    {
        if (category.NameOrSubType == nint.Zero)
            return "";
        try { return ShellInterop.Resolve(Marshal.PtrToStringUni(category.NameOrSubType)) ?? ""; }
        finally { Marshal.FreeCoTaskMem(category.NameOrSubType); }
    }

    private static List<JumpEntry> Entries(ShellInterop.IInternalCustomDestinationList list, uint category)
    {
        var iid = ShellInterop.IID_IObjectArray;
        return list.EnumerateCategoryDestinations(category, ref iid, out var array) == 0 &&
               ComInterop.Take<ShellInterop.IObjectArray>(array) is { } objects
            ? Entries(objects)
            : new List<JumpEntry>();
    }

    private static List<JumpEntry> Documents(string appId, int listType)
    {
        try
        {
            var lists = ComInterop.Create<ShellInterop.IApplicationDocumentLists>(ShellInterop.CLSID_ApplicationDocumentLists);
            try
            {
                var iid = ShellInterop.IID_IObjectArray;
                return lists.SetAppID(appId) == 0 && lists.GetList(listType, MaxPerCategory, ref iid, out var array) == 0 &&
                       ComInterop.Take<ShellInterop.IObjectArray>(array) is { } objects
                    ? Entries(objects)
                    : new List<JumpEntry>();
            }
            finally
            {
                ComInterop.Release(lists);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"JumpLists: documents for {appId}: {ex.Message}");
            return new List<JumpEntry>();
        }
    }

    private static List<JumpEntry> Entries(ShellInterop.IObjectArray array)
    {
        var entries = new List<JumpEntry>();
        try
        {
            if (array.GetCount(out uint count) != 0)
                return entries;
            var unknown = ShellInterop.IID_IUnknown;
            for (uint i = 0; i < count && entries.Count(e => !e.IsSeparator) < MaxPerCategory; i++)
            {
                if (array.GetAt(i, ref unknown, out var pointer) != 0 || ComInterop.Take(pointer) is not { } item)
                    continue;
                if (Entry(item) is { } entry)
                    entries.Add(entry);
                else
                    ComInterop.Release(item);
            }
        }
        finally
        {
            ComInterop.Release(array);
        }
        return entries;
    }

    private static JumpEntry? Entry(object item)
    {
        if (item is ShellInterop.IShellItem shellItem)
            return ShellInterop.DisplayName(shellItem, ShellInterop.SIGDN_NORMALDISPLAY) is { Length: > 0 } name
                ? new JumpEntry(name, item, false)
                : null;
        // A task: a shell link, labelled by its System.Title.
        if (item is not NativeMethods.IPropertyStore store)
            return null;
        if (ShellInterop.BoolProperty(store, ShellInterop.PKEY_IsDestListSeparator))
            return new JumpEntry("", null, true);
        return ShellInterop.StringProperty(store, ShellInterop.PKEY_Title) is { Length: > 0 } title
            ? new JumpEntry(title, item, false)
            : null;
    }

    /// <summary>Opens a line of a jump list as the taskbar would: a file with its app, a task through its link.</summary>
    public static void Open(JumpEntry entry)
    {
        try
        {
            switch (entry.Target)
            {
                case ShellInterop.IShellItem item:
                    if (ShellInterop.DisplayName(item, ShellInterop.SIGDN_DESKTOPABSOLUTEPARSING) is { } path)
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                case ShellInterop.IContextMenu menu:
                    InvokeDefault(menu);
                    break;
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"JumpLists: opening \"{entry.Title}\" failed: {ex.Message}");
        }
    }

    /// <summary>Runs a link's default command ("Open"), which carries its arguments and app identity with it.</summary>
    private static void InvokeDefault(ShellInterop.IContextMenu menu)
    {
        nint popup = NativeMethods.CreatePopupMenu();
        try
        {
            const uint first = 1;
            if (menu.QueryContextMenu(popup, 0, first, 0x7FFF, ShellInterop.CMF_DEFAULTONLY) < 0)
                return;
            uint id = ShellInterop.GetMenuDefaultItem(popup, 0, 0);
            if (id == uint.MaxValue)
                id = first;
            var info = new ShellInterop.CMINVOKECOMMANDINFO
            {
                cbSize = (uint)Marshal.SizeOf<ShellInterop.CMINVOKECOMMANDINFO>(),
                lpVerb = (nint)(id - first),
                nShow = 1, // SW_SHOWNORMAL
            };
            menu.InvokeCommand(ref info);
        }
        finally
        {
            NativeMethods.DestroyMenu(popup);
        }
    }

    /// <summary>Lets go of the COM objects behind a menu's worth of entries once it has closed.</summary>
    public static void Release(IEnumerable<JumpCategory> categories)
    {
        foreach (var entry in categories.SelectMany(category => category.Entries))
            ComInterop.Release(entry.Target);
    }
}
