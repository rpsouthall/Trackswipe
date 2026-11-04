using System.Runtime.InteropServices;
using System.Text;

namespace Anchor.Interop;

// The shell interfaces behind jump lists and the Start menu's app list. IShellItem, IObjectArray,
// IContextMenu and IApplicationDocumentLists are documented (shobjidl_core.h). The custom
// destination list — the categories and tasks an app files with ICustomDestinationList, which is
// write-only in the SDK — is read back through windows.storage.dll's IInternalCustomDestinationList,
// the interface Explorer's own taskbar uses. It is undocumented: this is the declaration Open-Shell
// has shipped against it since Windows 10 build 10547, and every call through it is guarded, so a
// Windows that changes it costs the jump list, never the menu.
internal static class ShellInterop
{
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IID_IObjectArray = new("92CA9DCD-5622-4bba-A805-5E9F541BD8C9");
    public static readonly Guid IID_IObjectCollection = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly Guid BHID_PropertyStore = new("0384e1a4-1523-439c-a4c8-ab911052f586");

    public static readonly Guid CLSID_ApplicationDocumentLists = new("86bec222-30f2-47e0-9f25-60d11cd75c28");
    public static readonly Guid CLSID_DestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");

    public const uint SIGDN_NORMALDISPLAY = 0;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    /// <summary>System.Title: a jump-list task's label.</summary>
    public static readonly NativeMethods.PROPERTYKEY PKEY_Title =
        new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };

    /// <summary>System.AppUserModel.IsDestListSeparator: a task that is a divider rather than a command.</summary>
    public static readonly NativeMethods.PROPERTYKEY PKEY_IsDestListSeparator =
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 6 };

    /// <summary>System.Link.TargetParsingPath: what a Start menu shortcut points at.</summary>
    public static readonly NativeMethods.PROPERTYKEY PKEY_Link_TargetParsingPath =
        new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };

    public const ushort VT_BOOL = 11;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint form, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
    }

    [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint wanted, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object list);
    }

    public const int ADLT_RECENT = 0, ADLT_FREQUENT = 1;

    /// <summary>A category of an app's custom jump list, as GetCategory fills it in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct APPDESTCATEGORY
    {
        public int Type;          // 0 custom (named), 1 known (Frequent/Recent), 2 tasks
        public nint NameOrSubType; // custom: the name (CoTaskMemFree it); known: 1 Frequent, 2 Recent
        public int Count;
        public fixed int Pad[10];  // room for fields this declaration doesn't know about
    }

    [ComImport, Guid("507101CD-F6AD-46C8-8E20-EEB9E6BAC47F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInternalCustomDestinationList
    {
        [PreserveSig] int SetMinItems(uint count);
        [PreserveSig] int SetApplicationID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetSlotCount(out uint count);
        [PreserveSig] int GetCategoryCount(out uint count);
        [PreserveSig] int GetCategory(uint index, int flags, out APPDESTCATEGORY category);
        [PreserveSig] int DeleteCategory(uint index, int permanently);
        [PreserveSig] int EnumerateCategoryDestinations(uint index, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO info);
        [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint name, uint cchMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
    }

    public const uint CMF_DEFAULTONLY = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, out IShellItem item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, nint reserved);

    [DllImport("user32.dll")]
    public static extern uint GetMenuDefaultItem(nint menu, uint byPosition, uint flags);

    /// <summary>A display name from IShellItem, freed after reading.</summary>
    public static string? DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out nint name) != 0 || name == nint.Zero)
            return null;
        try { return Marshal.PtrToStringUni(name); }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    /// <summary>A string property from a property store, with "@dll,-id" resource references resolved.</summary>
    public static string? StringProperty(NativeMethods.IPropertyStore store, NativeMethods.PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            if (value.vt != NativeMethods.VT_LPWSTR || value.pointer == nint.Zero)
                return null;
            return Resolve(Marshal.PtrToStringUni(value.pointer));
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    public static bool BoolProperty(NativeMethods.IPropertyStore store, NativeMethods.PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var value) != 0)
            return false;
        try { return value.vt == VT_BOOL && (short)(value.pointer & 0xFFFF) != 0; }
        finally { NativeMethods.PropVariantClear(ref value); }
    }

    /// <summary>Resolves an indirect string ("@shell32.dll,-21", "@{package?ms-resource://…}"); anything else as is.</summary>
    public static string? Resolve(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '@')
            return text;
        var buffer = new StringBuilder(512);
        return SHLoadIndirectString(text, buffer, (uint)buffer.Capacity, nint.Zero) == 0 ? buffer.ToString() : text;
    }
}
