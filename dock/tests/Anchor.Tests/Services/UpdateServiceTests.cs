using Anchor.Services;
using Xunit;

namespace Anchor.Tests.Services;

/// <summary>
/// The offline half of the update check: tag parsing and the "is this actually newer?" decision.
/// The network call itself is deliberately not exercised — a test that reached GitHub would fail
/// on a machine with no connection and would depend on what has been released since.
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V2.0", "2.0")]
    [InlineData("  v1.0.1  ", "1.0.1")]
    public void ParseVersion_reads_a_release_tag(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-2024")]
    public void ParseVersion_returns_null_for_anything_that_is_not_a_version(string? tag)
    {
        // A repo can tag whatever it likes; an unparseable tag has to mean "nothing to report"
        // rather than an exception on a background startup check.
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    [Fact]
    public void CurrentVersion_is_a_three_part_version_that_compares_against_a_tag()
    {
        // Release tags never carry a revision, so the build's own version is normalized to
        // major.minor.build — otherwise 1.0.0.0 would read as newer than the tag "v1.0.0".
        var current = UpdateService.CurrentVersion;

        Assert.Equal(-1, current.Revision);
        Assert.Equal(current, UpdateService.ParseVersion($"v{current}"));
    }
}
