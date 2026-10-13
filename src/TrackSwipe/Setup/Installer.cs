using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrackSwipe.Setup;

/// <summary>
/// One-click setup built into the executable. Run from anywhere but its install folder, TrackSwipe offers to
/// install itself for the current user (no admin rights), choose the finger count and whether it runs as
/// administrator. Installing over an existing copy updates it. Settings, Apps lists it for uninstalling.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TrackSwipe";
    private const int InstallButton = 1001, PortableButton = 1002, ThreeFingers = 3, FourFingers = 4;

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TrackSwipe");
    public static string InstalledExe { get; } = Path.Combine(InstallDirectory, "TrackSwipe.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TrackSwipe.lnk");

    public static bool RunningInstalled => string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    private static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public enum Choice { Quit, RunPortable }

    /// <summary>Shows the setup page; installs and starts the installed copy, or lets this one run as is.</summary>
    public static Choice Run()
    {
        var installed = File.Exists(InstalledExe);
        var page = new Win32.TaskDialog.Page
        {
            Title = "TrackSwipe Setup",
            Heading = installed ? $"Update TrackSwipe to {Version}" : "Install TrackSwipe",
            Content = "Swipe between desktops with your fingers, live, and swipe up for Mission Control.\n\n" +
                "Choose how many fingers to swipe with. Windows' own swipe for that count is turned off so the two " +
                "don't both fire; uninstalling turns it back on.",
            Radios = new[]
            {
                new Win32.TaskDialog.Button(ThreeFingers, "Swipe with three fingers"),
                new Win32.TaskDialog.Button(FourFingers, "Swipe with four fingers"),
            },
            DefaultRadio = GesturePreferences.LoadFingers() == 3 ? ThreeFingers : FourFingers,
            Checkbox = "Run as administrator, so gestures also work while admin apps are in front",
            Checked = installed && Startup.TaskExists,
            Buttons = new[]
            {
                new Win32.TaskDialog.Button(InstallButton, installed ? "Update and start\nKeeps your settings" :
                    "Install and start\nFor your account only, and starts when you sign in"),
                new Win32.TaskDialog.Button(PortableButton, "Run without installing\nTry it now; nothing is set up"),
            },
            Footer = "Administrator mode asks for permission once; after that TrackSwipe starts that way without asking.",
        };
        var result = Win32.TaskDialog.Show(page);
        if (result.Button == PortableButton) return Choice.RunPortable;
        if (result.Button != InstallButton) return Choice.Quit;
        Install(result.Radio == ThreeFingers ? 3 : 4, result.Checked);
        return Choice.Quit;
    }

    private static void Install(int fingers, bool admin)
    {
        CloseRunning();
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            if (!RunningInstalled) CopyWithRetry(Environment.ProcessPath!, InstalledExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Win32.TaskDialog.Message("TrackSwipe couldn't be installed", ex.Message, warning: true);
            return;
        }
        GesturePreferences.SaveFingers(fingers);
        NativeGestures.Claim(fingers);
        CreateShortcut();
        Register();
        Log.Write($"Installed {Version} to {InstallDirectory}: {fingers} fingers, administrator {admin}.");

        // As administrator, the elevated copy registers its own sign-in task and runs; if the prompt is declined,
        // install normally instead.
        if (admin && Startup.RunElevated(InstalledExe, "--register-startup-task --welcome")) return;
        if (Startup.TaskExists) Startup.DeleteTask();
        Startup.SetRunEntry(InstalledExe);
        Process.Start(new ProcessStartInfo(InstalledExe, admin ? "--welcome --admin-declined" : "--welcome") { UseShellExecute = false });
    }

    public static void Uninstall()
    {
        var result = Win32.TaskDialog.Show(new Win32.TaskDialog.Page
        {
            Title = "TrackSwipe",
            Heading = "Uninstall TrackSwipe?",
            Content = "TrackSwipe will be closed and removed, and Windows' own touchpad swipes put back as they were.",
            CommandLinks = false,
            Buttons = new[] { new Win32.TaskDialog.Button(InstallButton, "Uninstall") },
        });
        if (result.Button != InstallButton) return;
        CloseRunning();
        NativeGestures.RestoreAll();
        Startup.Disable();
        try { File.Delete(Shortcut); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\TrackSwipe", throwOnMissingSubKey: false);
        Log.Flush();
        Win32.TaskDialog.Message("TrackSwipe has been uninstalled", "Windows' touchpad swipes are back to how they were.");
        // This executable is in the folder being removed: delete the folder once this process has really exited.
        var logs = Path.GetDirectoryName(Log.FilePath)!;
        var command = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
            $"foreach ($i in 1..20) {{ Remove-Item -LiteralPath '{InstallDirectory}','{logs}' -Recurse -Force -ErrorAction SilentlyContinue; " +
            $"if (-not (Test-Path -LiteralPath '{InstallDirectory}')) {{ break }}; Start-Sleep -Milliseconds 500 }}\"";
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), command)
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    /// <summary>Asks a running TrackSwipe (any build) to quit, and waits for it.</summary>
    public static void CloseRunning()
    {
        var quit = RegisterWindowMessageW(TrayIcon.QuitMessage);
        for (var window = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "TrackSwipe.Window", "TrackSwipe tray"); window != IntPtr.Zero;
             window = FindWindowExW(IntPtr.Zero, window, "TrackSwipe.Window", "TrackSwipe tray"))
            PostMessageW(window, quit, IntPtr.Zero, IntPtr.Zero);
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("TrackSwipe").Concat(Process.GetProcessesByName("TrackSwipeGPU")))
        {
            using (process)
            {
                if (process.Id == self) continue;
                try
                {
                    if (!process.WaitForExit(4000)) process.Kill();
                    process.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            // The old copy may still be exiting, or a just-deleted one still held open by a scanner.
            try { File.Copy(from, to, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40) { Thread.Sleep(200); }
        }
    }

    /// <summary>Lists TrackSwipe in Settings, Apps, with its uninstaller.</summary>
    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "TrackSwipe");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "rpsouthall");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", "https://github.com/rpsouthall/Trackswipe");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /// <summary>A Start menu shortcut, through IShellLinkW and IPersistFile called by vtable slot.</summary>
    private static unsafe void CreateShortcut()
    {
        var clsid = new Guid("00021401-0000-0000-C000-000000000046");
        var iidShellLink = new Guid("000214F9-0000-0000-C000-000000000046");
        var iidPersistFile = new Guid("0000010B-0000-0000-C000-000000000046");
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iidShellLink, out var link) < 0) return;
        try
        {
            var vtable = *(IntPtr**)link;
            fixed (char* path = InstalledExe)
            fixed (char* directory = InstallDirectory)
            fixed (char* description = "Finger-tracked desktop switching and Mission Control")
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[20])(link, path);        // SetPath
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[9])(link, directory);    // SetWorkingDirectory
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtable[7])(link, description);  // SetDescription
            }
            if (Marshal.QueryInterface(link, ref iidPersistFile, out var file) < 0) return;
            try
            {
                fixed (char* target = Shortcut)
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)(*(IntPtr**)file)[6])(file, target, 1); // Save
            }
            finally { Marshal.Release(file); }
        }
        finally { Marshal.Release(link); }
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr pointer);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
