using Anchor.Services;
using Microsoft.UI.Xaml.Markup;

namespace Anchor.Localization;

/// <summary>
/// XAML markup extension for translated text: <c>Text="{loc:Localize Key=Settings.Theme}"</c>.
/// <para>
/// Resolved once, when the XAML is loaded, from the table <see cref="Loc"/> chose at startup —
/// not a live binding to the string table (see its remarks). A language change therefore rebuilds
/// every window in place (<see cref="Anchor.DockManager.SetLanguage"/>) rather than re-resolving
/// this extension, so the new table takes effect immediately without a restart.
/// </para>
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class LocalizeExtension : MarkupExtension
{
    /// <summary>The string-table key to look up (see <c>Strings\en.json</c>).</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.Get(Key);
}
