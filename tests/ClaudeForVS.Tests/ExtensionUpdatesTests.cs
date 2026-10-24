using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public class ExtensionUpdatesTests
{
    [Fact]
    public void AReleaseGivesItsVersionAndVsix()
    {
        ExtensionRelease? release = ExtensionUpdates.ParseRelease("""
            {
              "tag_name": "v0.18.0", "draft": false, "prerelease": false,
              "html_url": "https://github.com/Spooky142/VSClaudeExtension/releases/tag/v0.18.0",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt" },
                { "name": "ClaudeForVS-0.18.0.vsix", "browser_download_url": "https://github.com/Spooky142/VSClaudeExtension/releases/download/v0.18.0/ClaudeForVS-0.18.0.vsix" }
              ]
            }
            """);

        Assert.NotNull(release);
        Assert.Equal("0.18.0", release!.Version);
        Assert.EndsWith("/ClaudeForVS-0.18.0.vsix", release.VsixUrl);
        Assert.EndsWith("/tag/v0.18.0", release.PageUrl);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v0.18.0", "assets": [] }""")] // no package attached
    [InlineData("""{ "tag_name": "v0.18.0", "prerelease": true, "assets": [ { "name": "a.vsix", "browser_download_url": "https://x/a.vsix" } ] }""")]
    [InlineData("""{ "tag_name": "nightly", "assets": [ { "name": "a.vsix", "browser_download_url": "https://x/a.vsix" } ] }""")]
    [InlineData("""{ "tag_name": "v1.0.0", "assets": [ { "name": "a.vsix", "browser_download_url": "http://x/a.vsix" } ] }""")] // not https
    [InlineData("""{ "message": "Not Found" }""")]
    [InlineData("not json")]
    public void AnythingElseIsIgnored(string json) => Assert.Null(ExtensionUpdates.ParseRelease(json));

    [Fact]
    public void OnlyANewerVersionIsAnUpdate()
    {
        string installed = ExtensionUpdates.InstalledVersion;
        Assert.Matches(@"^\d+\.\d+\.\d+$", installed);
        Assert.False(ExtensionUpdates.IsNewer(new ExtensionRelease(installed, "https://x/a.vsix", "https://x")));
        Assert.True(ExtensionUpdates.IsNewer(new ExtensionRelease("999.0.0", "https://x/a.vsix", "https://x")));
        Assert.False(ExtensionUpdates.IsNewer(new ExtensionRelease("0.0.1", "https://x/a.vsix", "https://x")));
    }
}
