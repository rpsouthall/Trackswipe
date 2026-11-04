using System.Diagnostics;
using Anchor.Models;
using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Resilience;

/// <summary>
/// A dock item that points at a network share that is not answering must never block the UI thread.
/// <para>
/// <c>File.Exists</c> and <c>Directory.Exists</c> on a UNC path to an unreachable host block for the
/// SMB connect timeout — measured on this machine at 21 s per host, so a dock with two such items
/// took 43 s before its tray icon and shortcut came up. <c>DockWindow</c> starts every icon load from
/// the UI thread, and everything in <c>IconService.LoadIconAsync</c> before its first real
/// <c>await</c> runs on the caller's thread, so what is asserted here is that the <em>call</em>
/// returns promptly, not that the icon does.
/// </para>
/// <para>
/// The addresses are RFC 5737 documentation ranges, which are never routed, so a connect simply times
/// out. The host is chosen at random each run: Windows remembers a failed SMB connect per host for a
/// while, and a remembered failure would answer instantly and hide the hang.
/// </para>
/// </summary>
public class NetworkPathHangTests
{
    // Generous against a loaded CI machine, tiny against the 21 s this guards.
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(2);

    private static string UnroutableHost()
    {
        var rnd = Random.Shared;
        string[] nets = { "192.0.2", "198.51.100", "203.0.113" };
        return $"{nets[rnd.Next(nets.Length)]}.{rnd.Next(2, 254)}";
    }

    private static TimeSpan TimeTheCall(DockItem item)
    {
        var clock = Stopwatch.StartNew();
        _ = IconService.LoadIconAsync(item, rasterizationScale: 1.0);   // not awaited: only the synchronous part is measured
        return clock.Elapsed;
    }

    [Fact]
    public void Loading_the_icon_of_an_unreachable_folder_does_not_block_the_caller()
    {
        var item = new DockItem { Kind = DockItemKind.Folder, DisplayName = "share", Target = $@"\\{UnroutableHost()}\share\folder" };
        var took = TimeTheCall(item);
        Assert.True(took < CallBudget, $"LoadIconAsync blocked its caller for {took.TotalSeconds:0.0} s on an unreachable folder");
    }

    [Fact]
    public void Loading_the_icon_of_an_unreachable_app_does_not_block_the_caller()
    {
        var item = new DockItem { Kind = DockItemKind.Application, DisplayName = "tool", Target = $@"\\{UnroutableHost()}\tools\app.exe" };
        var took = TimeTheCall(item);
        Assert.True(took < CallBudget, $"LoadIconAsync blocked its caller for {took.TotalSeconds:0.0} s on an unreachable app");
    }

    [Fact]
    public void Loading_the_icon_of_an_unreachable_file_does_not_block_the_caller()
    {
        var item = new DockItem { Kind = DockItemKind.File, DisplayName = "report", Target = $@"\\{UnroutableHost()}\share\report.pdf" };
        var took = TimeTheCall(item);
        Assert.True(took < CallBudget, $"LoadIconAsync blocked its caller for {took.TotalSeconds:0.0} s on an unreachable file");
    }

    [Fact]
    public void A_custom_icon_on_an_unreachable_share_does_not_block_the_caller()
    {
        var item = new DockItem
        {
            Kind = DockItemKind.Application,
            DisplayName = "tool",
            Target = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            CustomIconPath = $@"\\{UnroutableHost()}\icons\tool.png",
        };
        var took = TimeTheCall(item);
        Assert.True(took < CallBudget, $"LoadIconAsync blocked its caller for {took.TotalSeconds:0.0} s on an unreachable custom icon");
    }
}
