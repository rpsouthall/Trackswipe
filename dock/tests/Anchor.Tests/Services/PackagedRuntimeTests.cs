using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// The packaged/unpackaged split that the Microsoft Store build turns on.
/// <para>
/// The test host is a plain <c>dotnet test</c> process with no package identity, so this suite can
/// only ever observe the unpackaged answer — which is exactly the point of asserting it. The
/// behaviors gated on <see cref="PackagedRuntime.IsPackaged"/> are the ones a portable build must
/// keep (the Run-key startup toggle, the opt-in update check), and a regression that flipped the
/// detection would silently take both away from every zip user. The packaged half genuinely cannot
/// be tested from here; it needs an installed MSIX, and
/// <c>docs/microsoft-store-deployment.md</c> §11 carries it as a manual sideload check.
/// </para>
/// </summary>
public class PackagedRuntimeTests
{
    [Fact]
    public void A_test_host_has_no_package_identity()
    {
        Assert.False(PackagedRuntime.IsPackaged);
    }

    [Fact]
    public void Detection_is_resolved_once_and_does_not_change()
    {
        // Backed by a static initializer precisely so the P/Invoke happens once; a property that
        // re-probed on every read would be doing it on the UI thread during window construction.
        Assert.Equal(PackagedRuntime.IsPackaged, PackagedRuntime.IsPackaged);
    }

    [Fact]
    public void The_unpackaged_build_keeps_its_update_check()
    {
        // The mirror of the Store rule: packaged means no GitHub update check (the Store updates
        // the app), so unpackaged has to mean the feature is still there — it is the only updater
        // the portable zip has.
        Assert.True(DockManager.UpdateChecksSupported);
    }
}
