using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TrackSwipe.Setup;

/// <summary>
/// Starting TrackSwipe when the user signs in: a per-user Run entry normally, or, to run as administrator
/// without a UAC prompt at every sign-in, a scheduled task with the highest run level.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "TrackSwipe", TaskName = "TrackSwipe";

    public static bool IsElevated
    {
        get
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
    }

    public static bool RunEntryExists
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static bool TaskExists => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enabled => RunEntryExists || TaskExists;

    /// <summary>Starts this executable at sign-in: as administrator when this process is elevated, otherwise normally.</summary>
    public static bool Enable(string exe)
    {
        if (IsElevated)
        {
            if (!CreateTask(exe)) return false;
            SetRunEntry(null);
            return true;
        }
        if (TaskExists) DeleteTask(); // may need elevation; the Run entry still works alongside it
        SetRunEntry(exe);
        return true;
    }

    public static void Disable()
    {
        SetRunEntry(null);
        if (TaskExists) DeleteTask();
    }

    public static void SetRunEntry(string? exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (exe is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, $"\"{exe}\"");
            key.DeleteValue("TrackSwipeGPU", throwOnMissingValue: false); // left by pre-1.0 builds
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Couldn't change the Run entry: {ex.Message}");
        }
    }

    /// <summary>A logon task for the current user at the highest run level (needs an elevated caller).</summary>
    private static bool CreateTask(string exe)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        // Administrators and SYSTEM have full control; the user may read, run and delete their own task (so
        // uninstalling needs no UAC prompt) but never change it, which would let unelevated code run as administrator.
        var access = sid is null ? "" : $"<SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFXSD;;;{sid})</SecurityDescriptor>";
        string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? text;
        // Priority 4 is normal: the default (7) would run TrackSwipe at below-normal CPU and I/O priority.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts TrackSwipe as administrator when you sign in, so gestures also work over admin apps.</Description>{access}</RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Escape(exe)}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"TrackSwipe-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            // Registering over an existing task keeps its old permissions; only a fresh registration applies ours.
            Schtasks($"/Delete /TN \"{TaskName}\" /F");
            var code = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\"");
            Log.Write(code == 0 ? "Start at sign-in: scheduled task (as administrator)." : $"Couldn't create the sign-in task (schtasks {code}).");
            return code == 0;
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>Starts the sign-in task now (TrackSwipe as administrator); any user may run their own task.</summary>
    public static bool RunTask() => Schtasks($"/Run /TN \"{TaskName}\"") == 0;

    /// <summary>Deletes the logon task, asking for elevation if this process can't.</summary>
    public static void DeleteTask()
    {
        if (Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0) return;
        if (!IsElevated) RunElevated(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN \"{TaskName}\" /F", wait: true, hidden: true);
    }

    private static int Schtasks(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Starts a program as administrator (a UAC prompt). False if the user declined.</summary>
    public static bool RunElevated(string file, string arguments, bool wait = false, bool hidden = false)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = 0x00000040 /* NOCLOSEPROCESS */ | 0x00000400 /* FLAG_NO_UI */,
            lpVerb = "runas",
            lpFile = file,
            lpParameters = arguments,
            nShow = hidden ? 0 : 1,
        };
        if (!ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1223 /* ERROR_CANCELLED */) Log.Write($"Couldn't start as administrator: {new Win32Exception(error).Message}");
            return false;
        }
        if (info.hProcess != IntPtr.Zero)
        {
            if (wait) WaitForSingleObject(info.hProcess, 30000);
            CloseHandle(info.hProcess);
        }
        return true;
    }

    /// <summary>
    /// Starts a program without elevation even from an elevated process, by asking Explorer to start it.
    /// </summary>
    public static void RunUnelevated(string file)
    {
        if (!IsElevated) { Process.Start(new ProcessStartInfo(file) { UseShellExecute = false }); return; }
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{file}\"")
            { UseShellExecute = false });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
}
