using System.Net.Http;
using System.Text.Json;

namespace Anchor.Services;

/// <summary>What a release check found.</summary>
/// <param name="Version">The release's version, without a leading "v".</param>
/// <param name="Name">The release's title, or its tag when it has none.</param>
/// <param name="Url">The release page to send the user to.</param>
public sealed record ReleaseInfo(Version Version, string Name, string Url);

/// <summary>
/// The opt-in update check: asks GitHub for the latest release of <c>anchor-dock</c> and reports
/// whether it is newer than this build.
/// <para>
/// <b>Nothing here runs unless the user turns it on</b> (see <c>DockConfig.CheckForUpdates</c>,
/// which defaults to false). That matters more than the feature does: Anchor's privacy promise is
/// that the only thing it ever sends over the network is a request for a web link's favicon, and
/// an update check that shipped on by default would quietly make that untrue.
/// </para>
/// <para>
/// It reads the public releases API and nothing else — no token, no account, no identifier of any
/// kind beyond the HTTP request itself — and it never downloads or installs anything. Anchor is a
/// portable zip; the most useful thing this can do is say "there's a newer one" and open the
/// release page.
/// </para>
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/framedparadox/anchor-dock/releases/latest";

    /// <summary>Where the user is sent when they choose to get the update.</summary>
    public const string ReleasesPage =
        "https://github.com/framedparadox/anchor-dock/releases/latest";

    /// <summary>
    /// The newest release if it is newer than <paramref name="current"/>, otherwise null. Never
    /// throws: no network, a rate-limited API, a changed payload shape and a malformed version all
    /// mean the same thing here — "nothing to report" — and none of them is worth a dialog.
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken token = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // The GitHub API rejects requests with no User-Agent outright.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Anchor-Dock-UpdateCheck");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            await using var stream = await client.GetStreamAsync(LatestReleaseApi, token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = json.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
            if (ParseVersion(tag) is not { } version)
                return null;

            // Only ever report a strictly newer release: a local build ahead of the published one
            // (a developer's own) must not be told to "update" backwards.
            if (version <= current)
                return null;

            var name = root.TryGetProperty("name", out var releaseName)
                       && releaseName.GetString() is { Length: > 0 } n
                ? n
                : tag!;
            var url = root.TryGetProperty("html_url", out var htmlUrl)
                      && htmlUrl.GetString() is { Length: > 0 } u
                ? u
                : ReleasesPage;

            return new ReleaseInfo(version, name, url);
        }
        catch (Exception ex)
        {
            Diag.Log($"UpdateService: check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses a release tag such as <c>"v1.2.0"</c> into a version, or null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var trimmed = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    /// <summary>This build's version, normalized to major.minor.build so it compares cleanly
    /// against a release tag (which never carries a revision).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }
}
