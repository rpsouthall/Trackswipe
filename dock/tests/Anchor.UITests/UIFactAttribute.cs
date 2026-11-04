using Xunit;

namespace Anchor.UITests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless the run has opted into UI tests.
/// <para>
/// These need an interactive desktop and a built copy of the app, so they must never fail a
/// checkout that simply ran <c>dotnet test</c>. Opting in means setting two environment
/// variables, which <c>scripts/run-ui-tests.ps1</c> does:
/// </para>
/// <list type="bullet">
///   <item><c>ANCHOR_UITESTS=1</c> — confirms an interactive desktop is available.</item>
///   <item><c>ANCHOR_EXE</c> — full path to the <c>Anchor.exe</c> under test.</item>
/// </list>
/// </summary>
public sealed class UIFactAttribute : FactAttribute
{
    public UIFactAttribute()
    {
        if (!AnchorApp.UITestsEnabled)
            Skip = "UI tests are opt-in: set ANCHOR_UITESTS=1 and ANCHOR_EXE, or run scripts/run-ui-tests.ps1.";
        else if (AnchorApp.ExecutablePath is null)
            Skip = "ANCHOR_UITESTS is set but ANCHOR_EXE does not point at an existing Anchor.exe.";
    }
}
