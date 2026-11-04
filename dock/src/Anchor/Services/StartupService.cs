using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Anchor.Services;

/// <summary>
/// Toggles "launch Anchor at sign-in", by whichever mechanism this build's packaging supports.
/// <para>
/// <b>Unpackaged</b> (the portable zip) uses the per-user <c>Run</c> registry key — per-user
/// (HKCU) so it needs no elevation.
/// </para>
/// <para>
/// <b>Packaged</b> (the Microsoft Store MSIX) must not: a packaged process's writes under
/// <c>HKCU\Software</c> are captured in the package's own virtualized registry hive, which
/// Windows' autostart never reads — the toggle would appear to work and then do nothing after a
/// reboot, and the value would be invisible to the user in Task Manager's Startup tab. The
/// supported equivalent is the <c>windows.startupTask</c> extension declared in
/// <c>Package.appxmanifest</c> (<c>TaskId</c> must match <see cref="TaskId"/> exactly), enabled
/// at runtime through the <see cref="StartupTask"/> API. That also puts the entry where users
/// expect to manage it, which is the point: Windows lets them override the app from Task Manager
/// or Settings, and when they have, <see cref="RequestEnableAsync"/> reports
/// <see cref="StartupTaskState.DisabledByUser"/> and the app must respect it rather than keep
/// asking.
/// </para>
/// <para>
/// Every call is guarded — a locked-down registry or an unavailable startup task means the
/// setting reports "off" rather than crashing the dock.
/// </para>
/// </summary>
public static class StartupService
{
    /// <summary>
    /// Identifies the startup task in the MSIX manifest. Changing this string means changing
    /// <c>Package.appxmanifest</c>'s <c>desktop:StartupTask TaskId</c> in the same commit — a
    /// mismatch makes <see cref="StartupTask.GetAsync"/> throw and the toggle a no-op.
    /// </summary>
    private const string TaskId = "AnchorStartupTask";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Anchor";

    /// <summary>What the startup entry ended up as after a request to change it.</summary>
    public enum StartupState
    {
        /// <summary>Anchor will not start at sign-in.</summary>
        Disabled,

        /// <summary>Anchor will start at sign-in.</summary>
        Enabled,

        /// <summary>The user turned Anchor off in Task Manager / Settings ▸ Startup apps. Only the
        /// user can turn it back on there — the app is not allowed to override that choice.</summary>
        BlockedByUser,

        /// <summary>Group policy decides this, not the app or the user.</summary>
        BlockedByPolicy,
    }

    /// <summary>True when Anchor is currently set to start at sign-in.</summary>
    public static async Task<bool> IsEnabledAsync()
    {
        if (!PackagedRuntime.IsPackaged)
            return IsRunKeyPresent();

        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            Diag.Log("StartupService.IsEnabledAsync failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Turns the startup entry on or off.
    /// </summary>
    /// <returns>What it actually ended up as — which is not always what was asked for: on the
    /// packaged build, Windows can refuse to re-enable a task the user disabled themselves. The
    /// caller is expected to reflect the returned state in the UI rather than assume the request
    /// took effect.</returns>
    public static async Task<StartupState> SetEnabledAsync(bool enabled)
    {
        if (!PackagedRuntime.IsPackaged)
        {
            SetRunKey(enabled);
            return enabled ? StartupState.Enabled : StartupState.Disabled;
        }

        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                task.Disable();
                return StartupState.Disabled;
            }
            return Map(await task.RequestEnableAsync());
        }
        catch (Exception ex)
        {
            Diag.Log("StartupService.SetEnabledAsync failed: " + ex.Message);
            return StartupState.Disabled;
        }
    }

    /// <summary>Collapses the platform's five states onto the four the UI distinguishes: the two
    /// "on" states differ only in who turned it on, which is not something to tell the user.</summary>
    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupState.Enabled,
        StartupTaskState.DisabledByUser => StartupState.BlockedByUser,
        StartupTaskState.DisabledByPolicy => StartupState.BlockedByPolicy,
        _ => StartupState.Disabled,
    };

    // ---- Unpackaged: the per-user Run key -----------------------------------

    private static bool IsRunKeyPresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    private static void SetRunKey(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Anchor.exe");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("StartupService.SetRunKey failed: " + ex.Message);
        }
    }
}
