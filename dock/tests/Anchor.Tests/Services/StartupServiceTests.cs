using Anchor.Services;
using Microsoft.Win32;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// The unpackaged half of "start with Windows" — the per-user <c>Run</c> key — round-tripped
/// against the real registry.
/// <para>
/// This writes to <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, which is the actual
/// key the app uses, so each test saves whatever was there and puts it back. That is deliberate:
/// the value the service writes has to be one Windows will accept and run (a quoted absolute path
/// to a real executable), and a fake key would prove nothing about that. It needs no elevation —
/// HKCU is the whole point of this mechanism.
/// </para>
/// <para>
/// The packaged path (<c>windows.startupTask</c>) is unreachable from a test host with no package
/// identity — see <see cref="PackagedRuntimeTests"/> — and is covered by the manual sideload check
/// in <c>docs/microsoft-store-deployment.md</c> §11.
/// </para>
/// </summary>
[Collection("Registry")]
public class StartupServiceTests
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Anchor";

    [Fact]
    public async Task Enabling_then_disabling_leaves_the_run_key_as_it_was()
    {
        object? original = ReadRaw();
        try
        {
            var enabled = await StartupService.SetEnabledAsync(true);
            Assert.Equal(StartupService.StartupState.Enabled, enabled);
            Assert.True(await StartupService.IsEnabledAsync());

            var disabled = await StartupService.SetEnabledAsync(false);
            Assert.Equal(StartupService.StartupState.Disabled, disabled);
            Assert.False(await StartupService.IsEnabledAsync());

            // Turning it off must remove the value, not blank it: a leftover empty entry is
            // clutter in Task Manager's Startup tab that the user can't explain.
            Assert.Null(ReadRaw());
        }
        finally
        {
            Restore(original);
        }
    }

    [Fact]
    public async Task The_value_written_is_a_quoted_absolute_path_windows_can_run()
    {
        object? original = ReadRaw();
        try
        {
            await StartupService.SetEnabledAsync(true);

            var value = Assert.IsType<string>(ReadRaw());
            // Quoted, because the install path routinely contains spaces
            // (%LocalAppData%\Programs\…) and an unquoted one would be parsed as several
            // arguments and fail to start.
            Assert.StartsWith("\"", value, StringComparison.Ordinal);
            Assert.EndsWith("\"", value, StringComparison.Ordinal);

            var path = value.Trim('"');
            Assert.True(Path.IsPathFullyQualified(path), $"'{path}' is not an absolute path");
        }
        finally
        {
            Restore(original);
        }
    }

    [Fact]
    public async Task Disabling_when_it_was_never_enabled_is_not_an_error()
    {
        object? original = ReadRaw();
        try
        {
            Delete();
            Assert.Equal(StartupService.StartupState.Disabled, await StartupService.SetEnabledAsync(false));
            Assert.False(await StartupService.IsEnabledAsync());
        }
        finally
        {
            Restore(original);
        }
    }

    private static object? ReadRaw()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName);
    }

    private static void Delete()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void Restore(object? original)
    {
        if (original is null)
        {
            Delete();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.SetValue(ValueName, original);
    }
}

/// <summary>Serializes every test that touches the shared <c>Run</c> key, so two of them can't
/// save and restore it over each other.</summary>
[CollectionDefinition("Registry", DisableParallelization = true)]
public sealed class RegistryCollection;
