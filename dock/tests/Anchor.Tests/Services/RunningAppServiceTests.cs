using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

public class RunningAppServiceTests
{
    [Fact]
    public void A_snapshot_lists_each_open_app_once_with_its_windows()
    {
        // Runs against whatever is open on the machine running the tests, so it checks the shape
        // of the answer rather than its contents.
        var apps = RunningAppService.Snapshot();

        Assert.Equal(apps.Count, apps.Select(app => app.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(apps, app =>
        {
            Assert.NotEmpty(app.Windows);
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.False(string.IsNullOrWhiteSpace(app.LaunchTarget));
            // The shell's own surfaces are never apps.
            Assert.NotEqual("ApplicationFrameHost.exe", Path.GetFileName(app.ExePath ?? ""), StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("SearchHost.exe", Path.GetFileName(app.ExePath ?? ""), StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_snapshot_never_includes_the_dock_itself()
    {
        string self = Environment.ProcessPath!;
        Assert.DoesNotContain(RunningAppService.Snapshot(), app => string.Equals(app.ExePath, self, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_packaged_app_launches_from_its_Start_menu_entry_and_a_desktop_app_from_its_exe()
    {
        var store = new RunningApp("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Calculator", [1], 0);
        var desktop = new RunningApp(@"C:\Windows\notepad.exe", @"C:\Windows\notepad.exe", null, "Notepad", [1], 0);

        Assert.Equal(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", store.LaunchTarget);
        Assert.Equal(@"C:\Windows\notepad.exe", desktop.LaunchTarget);
    }

    [Fact]
    public void A_Start_menu_item_is_matched_by_its_AppUserModelID()
    {
        var item = new DockItem { Kind = DockItemKind.Application, Target = @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App" };

        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", RunningAppService.AppIdOf(item));
        Assert.Null(RunningAppService.ExecutablePathOf(item));
    }

    [Fact]
    public void Only_apps_have_an_AppUserModelID_to_match()
    {
        Assert.Null(RunningAppService.AppIdOf(new DockItem { Kind = DockItemKind.Application, Target = @"C:\Windows\notepad.exe" }));
        Assert.Null(RunningAppService.AppIdOf(new DockItem { Kind = DockItemKind.Folder, Target = @"shell:AppsFolder\Something" }));
    }
}
