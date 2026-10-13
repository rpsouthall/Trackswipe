using System.Runtime.InteropServices;

namespace TrackSwipe.Win32;

/// <summary>The Windows task dialog: a native, themed dialog with command links, radio buttons and one checkbox.</summary>
internal static unsafe class TaskDialog
{
    public sealed record Button(int Id, string Text);

    public sealed class Page
    {
        public string Title { get; init; } = "TrackSwipe";
        public string Heading { get; init; } = "";
        public string Content { get; init; } = "";
        public IReadOnlyList<Button> Buttons { get; init; } = Array.Empty<Button>();
        public IReadOnlyList<Button> Radios { get; init; } = Array.Empty<Button>();
        public int DefaultRadio { get; init; }
        public string? Checkbox { get; init; }
        public bool Checked { get; init; }
        public string? Footer { get; init; }
        public bool CommandLinks { get; init; } = true;
        public bool Cancel { get; init; } = true;
        public bool Warning { get; init; }
    }

    public readonly record struct Result(int Button, int Radio, bool Checked);

    public const int IdCancel = 2, IdOk = 1;

    public static Result Show(Page page)
    {
        var strings = new List<IntPtr>();
        char* Str(string? text)
        {
            if (text is null) return null;
            var pointer = Marshal.StringToHGlobalUni(text);
            strings.Add(pointer);
            return (char*)pointer;
        }
        var buttons = stackalloc TASKDIALOG_BUTTON[Math.Max(1, page.Buttons.Count)];
        for (var i = 0; i < page.Buttons.Count; i++) buttons[i] = new TASKDIALOG_BUTTON { nButtonID = page.Buttons[i].Id, pszButtonText = Str(page.Buttons[i].Text) };
        var radios = stackalloc TASKDIALOG_BUTTON[Math.Max(1, page.Radios.Count)];
        for (var i = 0; i < page.Radios.Count; i++) radios[i] = new TASKDIALOG_BUTTON { nButtonID = page.Radios[i].Id, pszButtonText = Str(page.Radios[i].Text) };
        var icon = page.Warning ? IntPtr.Zero : AppIcon.Large;
        try
        {
            var config = new TASKDIALOGCONFIG
            {
                cbSize = (uint)sizeof(TASKDIALOGCONFIG),
                hInstance = GetModuleHandleW(null),
                dwFlags = 0x0008 /* ALLOW_DIALOG_CANCELLATION */ | (page.CommandLinks ? 0x0010u /* USE_COMMAND_LINKS */ : 0) |
                    (icon != IntPtr.Zero ? 0x0002u /* USE_HICON_MAIN */ : 0) | 0x1000000 /* SIZE_TO_CONTENT */,
                dwCommonButtons = page.Cancel ? 0x0008u /* CANCEL */ : 0,
                pszWindowTitle = Str(page.Title),
                hMainIcon = icon != IntPtr.Zero ? icon : (IntPtr)0xFFFF /* TD_WARNING_ICON: MAKEINTRESOURCE(-1), a 16-bit id */,
                pszMainInstruction = Str(page.Heading),
                pszContent = Str(page.Content),
                cButtons = (uint)page.Buttons.Count,
                pButtons = page.Buttons.Count > 0 ? buttons : null,
                nDefaultButton = page.Buttons.Count > 0 ? page.Buttons[0].Id : 0,
                cRadioButtons = (uint)page.Radios.Count,
                pRadioButtons = page.Radios.Count > 0 ? radios : null,
                nDefaultRadioButton = page.DefaultRadio,
                pszVerificationText = Str(page.Checkbox),
                pszFooter = Str(page.Footer),
                hFooterIcon = page.Footer is null ? IntPtr.Zero : (IntPtr)0xFFFD /* TD_INFORMATION_ICON: MAKEINTRESOURCE(-3) */,
            };
            if (page.Checked) config.dwFlags |= 0x0100; // VERIFICATION_FLAG_CHECKED
            int button, radio, isChecked;
            var hr = TaskDialogIndirect(&config, &button, &radio, &isChecked);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return new Result(button, radio, isChecked != 0);
        }
        finally
        {
            foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>A plain message with an OK button.</summary>
    public static void Message(string heading, string content, bool warning = false) => Show(new Page
    {
        Heading = heading, Content = content, CommandLinks = false, Cancel = false, Warning = warning,
        Buttons = new[] { new Button(IdOk, "OK") },
    });

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOG_BUTTON { public int nButtonID; public char* pszButtonText; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent, hInstance;
        public uint dwFlags, dwCommonButtons;
        public char* pszWindowTitle;
        public IntPtr hMainIcon;
        public char* pszMainInstruction, pszContent;
        public uint cButtons;
        public TASKDIALOG_BUTTON* pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public TASKDIALOG_BUTTON* pRadioButtons;
        public int nDefaultRadioButton;
        public char* pszVerificationText, pszExpandedInformation, pszExpandedControlText, pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public char* pszFooter;
        public IntPtr pfCallback, lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll")] private static extern int TaskDialogIndirect(TASKDIALOGCONFIG* config, int* button, int* radio, int* verification);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>The application icon embedded in the executable (resource 32512, which the SDK assigns to ApplicationIcon).</summary>
internal static class AppIcon
{
    private static IntPtr _large, _small;

    public static IntPtr Large => _large != IntPtr.Zero ? _large : _large = Load(GetSystemMetrics(11 /* SM_CXICON */) * 2);
    public static IntPtr Small => _small != IntPtr.Zero ? _small : _small = Load(GetSystemMetrics(49 /* SM_CXSMICON */));

    private static IntPtr Load(int size)
    {
        LoadIconWithScaleDown(GetModuleHandleW(null), 32512, size, size, out var icon);
        return icon;
    }

    [DllImport("comctl32.dll")] private static extern int LoadIconWithScaleDown(IntPtr instance, nint name, int width, int height, out IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
