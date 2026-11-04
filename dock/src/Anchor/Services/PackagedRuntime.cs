using Anchor.Interop;

namespace Anchor.Services;

/// <summary>
/// Whether this process is running from an MSIX package (the Microsoft Store build) or as the
/// plain unpackaged exe (the portable zip).
/// <para>
/// Anchor ships both ways from one codebase, and a handful of behaviors genuinely have to differ
/// between them — not for cosmetic reasons but because the packaged form changes what works and
/// what the Store permits:
/// </para>
/// <list type="bullet">
///   <item><description><b>Start with Windows.</b> A packaged app's writes to
///   <c>HKCU\…\CurrentVersion\Run</c> land in the package's virtualized registry hive, where
///   Windows' autostart never looks — the setting would silently do nothing. The supported
///   equivalent is the <c>windows.startupTask</c> manifest extension driven by the
///   <c>StartupTask</c> API. See <see cref="StartupService"/>.</description></item>
///   <item><description><b>The update check.</b> The Store delivers updates itself, so a packaged
///   Anchor pointing users at a GitHub release would be both redundant and a distribution route
///   outside the Store. The whole feature is therefore hidden when packaged — see
///   <see cref="DockManager.UpdateChecksSupported"/>.</description></item>
/// </list>
/// <para>
/// Resolved once: package identity cannot change while a process runs.
/// </para>
/// </summary>
public static class PackagedRuntime
{
    /// <summary>True when Anchor is running from its MSIX package (the Store build).</summary>
    public static bool IsPackaged { get; } = Resolve();

    private static bool Resolve()
    {
        try
        {
            uint length = 0;
            int result = NativeMethods.GetCurrentPackageFullName(ref length, null);
            // No package at all is the unpackaged build; anything else (in practice
            // ERROR_INSUFFICIENT_BUFFER, since the buffer was deliberately empty) means there is
            // one. Treating an unexpected code as "packaged" is the safe direction: the packaged
            // paths below are the conservative ones.
            bool packaged = result != NativeMethods.APPMODEL_ERROR_NO_PACKAGE;
            Diag.Log($"PackagedRuntime: {(packaged ? "packaged (MSIX)" : "unpackaged")} (code {result})");
            return packaged;
        }
        catch (Exception ex)
        {
            // The API is missing only on pre-Windows-8 systems, which Anchor doesn't target — but
            // a failed lookup must not stop the app starting, and unpackaged is what a portable
            // copy is.
            Diag.Log($"PackagedRuntime: identity lookup failed ({ex.GetType().Name}) — assuming unpackaged");
            return false;
        }
    }
}
