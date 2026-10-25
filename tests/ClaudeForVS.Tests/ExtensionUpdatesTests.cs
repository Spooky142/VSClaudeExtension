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

    [Fact]
    public void TheRestartScriptGetsEveryPathQuoted()
    {
        string args = ExtensionUpdates.RestartArguments(
            @"C:\Temp\restart-and-install.ps1", 1234, @"C:\VS\Common7\IDE\VSIXInstaller.exe", @"C:\Temp\ClaudeForVS-1.0.0.vsix",
            @"C:\VS\Common7\IDE\devenv.exe", @"D:\My Repos\App.sln", "Exp");

        Assert.Equal(
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:\\Temp\\restart-and-install.ps1\" -VsPid 1234"
            + " -Installer \"C:\\VS\\Common7\\IDE\\VSIXInstaller.exe\" -Vsix \"C:\\Temp\\ClaudeForVS-1.0.0.vsix\""
            + " -Devenv \"C:\\VS\\Common7\\IDE\\devenv.exe\" -Solution \"D:\\My Repos\\App.sln\" -RootSuffix \"Exp\"",
            args);
    }

    [Fact]
    public void WithoutASolutionOrHiveThoseAreLeftOut()
    {
        string args = ExtensionUpdates.RestartArguments(@"C:\s.ps1", 1, @"C:\i.exe", @"C:\a.vsix", @"C:\devenv.exe", null, null);

        Assert.DoesNotContain("-Solution", args);
        Assert.DoesNotContain("-RootSuffix", args);
    }

    [Fact]
    public void TheVersionGoesLastForTheProgressWindow() =>
        Assert.EndsWith("-Version \"1.2.3\"", ExtensionUpdates.RestartArguments(@"C:\s.ps1", 1, @"C:\i.exe", @"C:\a.vsix", @"C:\devenv.exe", null, "Exp", "1.2.3"));

    [Fact]
    public void AFolderKeepsItsClosingQuote() =>
        Assert.EndsWith("-Solution \"D:\\Repos\\App\"", ExtensionUpdates.RestartArguments(@"C:\s.ps1", 1, @"C:\i.exe", @"C:\a.vsix", @"C:\devenv.exe", @"D:\Repos\App\", null));

    [Theory]
    [InlineData("Exp", @"C:\VS\devenv.exe", "/rootsuffix", "Exp")]
    [InlineData("Exp", @"C:\VS\devenv.exe", "/RootSuffix", "Exp", @"D:\App.sln")]
    [InlineData(null, @"C:\VS\devenv.exe", @"D:\App.sln")]
    [InlineData(null, @"C:\VS\devenv.exe", "/rootsuffix")]
    public void TheHiveComesFromTheCommandLine(string? expected, params string[] commandLine) =>
        Assert.Equal(expected, ExtensionUpdates.RootSuffix(commandLine));
}
