using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Anchor.Models;

namespace Anchor.Services;

/// <summary>
/// Every type Anchor reads or writes as JSON, with the code to do it generated at build time rather than worked out by
/// reflection at run time: needed for a natively compiled (AOT) build, and quicker to start either way. The options
/// match what <c>dock.json</c> has always been written with: indented, nulls left out, enums by name.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(DockConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AnchorJson : JsonSerializerContext
{
    /// <summary>
    /// The generated options plus the one setting an attribute can't carry: text written as typed, not escaped
    /// ("Ctrl+Alt+A", "書類"). dock.json is documented as hand-editable and never embedded in HTML or a script, which
    /// is the only place that escaping guards.
    /// </summary>
    /// <remarks>
    /// Made on first use, not by a static initializer: <see cref="JsonSerializerContext.Default"/> is set up by the
    /// generated half of this class, and C# leaves the order of static initializers across a partial class's files
    /// unspecified — read too early, it isn't there yet.
    /// </remarks>
    public static JsonSerializerOptions Readable => _readable ??= new(Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonSerializerOptions? _readable;
    private static JsonTypeInfo<DockConfig>? _config;

    public static JsonTypeInfo<DockConfig> Config => _config ??= (JsonTypeInfo<DockConfig>)Readable.GetTypeInfo(typeof(DockConfig));

    public static JsonTypeInfo<Dictionary<string, string>> Strings => Default.DictionaryStringString;
}
