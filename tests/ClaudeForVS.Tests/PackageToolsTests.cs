using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

[Collection("Visual Studio hosts")]
public sealed class PackageToolsTests : IDisposable
{
    private const string Repo = @"C:\work\repo";

    private readonly FakePackageHost host = new();

    public PackageToolsTests() => PackageHostRegistry.Current = this.host;

    public void Dispose() => PackageHostRegistry.Current = null;

    [Fact]
    public async Task ListsProjectsAndTheirPackages()
    {
        string result = await PackageTools.ListAsync(Input(new { }), Repo, CancellationToken.None);

        Assert.Equal(
            "App (src/App/App.csproj):\n  Newtonsoft.Json 12.0.3\n  Serilog 3.1.1\nTests (src/Tests/Tests.csproj):\n  (no packages)",
            result);
    }

    [Fact]
    public async Task ListsOneProject()
    {
        string result = await PackageTools.ListAsync(Input(new { project = "tests" }), Repo, CancellationToken.None);

        Assert.StartsWith("Tests ", result);
        Assert.DoesNotContain("App", result);
    }

    [Fact]
    public async Task NamesTheProjectsWhenOneIsUnknown()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(() => PackageTools.ListAsync(Input(new { project = "Nope" }), Repo, CancellationToken.None));

        Assert.Contains("No project named \"Nope\"", ex.Message);
        Assert.Contains("App, Tests", ex.Message);
    }

    [Fact]
    public async Task AddsAPackageAtTheLatestVersion()
    {
        string result = await PackageTools.AddAsync(PackageTools.Parse(Input(new { project = "Tests", package = "xunit" }), withVersion: true), CancellationToken.None);

        Assert.StartsWith("Added xunit 9.9.9 in Tests.", result);
        Assert.Contains("build_solution", result);
        Assert.Equal([("Tests", "xunit", (string?)null)], this.host.Installs);
    }

    [Fact]
    public async Task ChangesTheVersionOfAPackageThatIsAlreadyThere()
    {
        string result = await PackageTools.AddAsync(PackageTools.Parse(Input(new { project = "App", package = "newtonsoft.json", version = "13.0.3" }), withVersion: true), CancellationToken.None);

        Assert.StartsWith("Changed Newtonsoft.Json 13.0.3 in App (was 12.0.3).", result);
    }

    [Fact]
    public async Task DoesNothingWhenThePackageIsAlreadyAtThatVersion()
    {
        string result = await PackageTools.AddAsync(PackageTools.Parse(Input(new { project = "App", package = "Serilog", version = "3.1.1" }), withVersion: true), CancellationToken.None);

        Assert.Equal("Serilog 3.1.1 is already in App.", result);
        Assert.Empty(this.host.Installs);
    }

    [Fact]
    public async Task SaysWhenVisualStudioEndsUpWithAnotherVersion()
    {
        this.host.InstalledVersionOverride = "1.0.0";

        string result = await PackageTools.AddAsync(PackageTools.Parse(Input(new { project = "Tests", package = "Foo", version = "2.0.0" }), withVersion: true), CancellationToken.None);

        Assert.Contains("you asked for 2.0.0, but Visual Studio reports 1.0.0", result);
    }

    [Fact]
    public async Task ReportsNuGetsReasonWhenAnAddFails()
    {
        this.host.FailWith = "Unable to find package 'Nope'.";

        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => PackageTools.AddAsync(PackageTools.Parse(Input(new { project = "App", package = "Nope" }), withVersion: true), CancellationToken.None));

        Assert.Equal("Unable to find package 'Nope'.", ex.Message);
    }

    [Fact]
    public async Task RemovesAPackage()
    {
        string result = await PackageTools.RemoveAsync(PackageTools.Parse(Input(new { project = "App", package = "serilog" }), withVersion: false), CancellationToken.None);

        Assert.StartsWith("Removed Serilog 3.1.1 from App.", result);
        Assert.Equal([("App", "Serilog")], this.host.Removals);
    }

    [Fact]
    public async Task ListsWhatAProjectHasWhenARemovalNamesSomethingElse()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => PackageTools.RemoveAsync(PackageTools.Parse(Input(new { project = "App", package = "Missing" }), withVersion: false), CancellationToken.None));

        Assert.Contains("doesn't reference Missing", ex.Message);
        Assert.Contains("Newtonsoft.Json, Serilog", ex.Message);
    }

    [Theory]
    [InlineData("", "Serilog", "")]
    [InlineData("App", "", "")]
    [InlineData("App", "bad id", "")]
    [InlineData("App", "Serilog", "1.0 beta")]
    [InlineData("App", "..\\evil", "")]
    public void RejectsInputThatIsNotAPackageRequest(string project, string package, string version)
    {
        Assert.Throws<ToolInputException>(() => PackageTools.Parse(Input(new { project, package, version }), withVersion: true));
    }

    [Fact]
    public void AcceptsPreReleaseVersions()
    {
        PackageRequest request = PackageTools.Parse(Input(new { project = "App", package = "Serilog", version = "4.0.0-beta.1" }), withVersion: true);

        Assert.Equal(new PackageRequest("App", "Serilog", "4.0.0-beta.1"), request);
        Assert.Equal("Add NuGet package Serilog 4.0.0-beta.1 to App", PackageTools.Title(request, remove: false));
        Assert.Equal("Remove NuGet package Serilog from App", PackageTools.Title(request, remove: true));
    }

    [Fact]
    public async Task SaysWhenThereIsNoNuGet()
    {
        PackageHostRegistry.Current = null;

        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(() => PackageTools.ListAsync(Input(new { }), Repo, CancellationToken.None));

        Assert.Contains("only be managed from Visual Studio", ex.Message);
    }

    private static Dictionary<string, JsonElement> Input(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private sealed class FakePackageHost : IPackageHost
    {
        private readonly List<PackageProject> projects =
        [
            new PackageProject("App", Repo + @"\src\App\App.csproj", [new PackageInfo("Newtonsoft.Json", "12.0.3"), new PackageInfo("Serilog", "3.1.1")]),
            new PackageProject("Tests", Repo + @"\src\Tests\Tests.csproj", []),
        ];

        public List<(string Project, string Package, string? Version)> Installs { get; } = [];

        public List<(string Project, string Package)> Removals { get; } = [];

        public string? InstalledVersionOverride { get; set; }

        public string? FailWith { get; set; }

        public Task<IReadOnlyList<PackageProject>> GetProjectsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PackageProject>>(this.projects.ToList());

        public Task InstallAsync(string project, string packageId, string? version, CancellationToken cancellationToken)
        {
            if (this.FailWith != null)
            {
                throw new ToolInputException(this.FailWith);
            }

            this.Installs.Add((project, packageId, version));
            this.Replace(project, packages =>
            {
                string id = packages.FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Id ?? packageId; // NuGet keeps the id as it was
                packages.RemoveAll(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                packages.Add(new PackageInfo(id, this.InstalledVersionOverride ?? version ?? "9.9.9"));
            });
            return Task.CompletedTask;
        }

        public Task UninstallAsync(string project, string packageId, CancellationToken cancellationToken)
        {
            this.Removals.Add((project, packageId));
            this.Replace(project, packages => packages.RemoveAll(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)));
            return Task.CompletedTask;
        }

        private void Replace(string project, Action<List<PackageInfo>> change)
        {
            int at = this.projects.FindIndex(p => p.Name.Equals(project, StringComparison.OrdinalIgnoreCase));
            List<PackageInfo> packages = this.projects[at].Packages.ToList();
            change(packages);
            this.projects[at] = this.projects[at] with { Packages = packages };
        }
    }
}

public sealed class ProjectPackagesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS-pkg-" + Guid.NewGuid().ToString("N"));

    public ProjectPackagesTests() => Directory.CreateDirectory(Path.Combine(this.root, "src", "App"));

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private string Write(string relative, string content)
    {
        string path = Path.Combine(this.root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadsPackageReferences()
    {
        string project = this.Write(@"src\App\App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog">
                  <Version>3.1.1</Version>
                </PackageReference>
                <PackageReference Update="Newtonsoft.Json" Version="14.0.0" />
                <PackageReference Include="Newtonsoft.Json" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal([new PackageInfo("Newtonsoft.Json", "13.0.3"), new PackageInfo("Serilog", "3.1.1")], ProjectPackages.Read(project));
    }

    [Fact]
    public void TakesVersionsFromCentralPackageManagement()
    {
        this.Write("Directory.Packages.props",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);
        string project = this.Write(@"src\App\App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
                <PackageReference Include="Other" />
                <PackageReference Include="Pinned" VersionOverride="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal([new PackageInfo("Serilog", "3.1.1"), new PackageInfo("Other", "(no version)"), new PackageInfo("Pinned", "2.0.0")], ProjectPackages.Read(project));
    }

    [Fact]
    public void ReadsAPackagesConfig()
    {
        string project = this.Write(@"src\App\App.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>");
        this.Write(@"src\App\packages.config", "<packages><package id=\"NUnit\" version=\"3.13.3\" targetFramework=\"net48\" /></packages>");

        Assert.Equal([new PackageInfo("NUnit", "3.13.3")], ProjectPackages.Read(project));
    }

    [Fact]
    public void ReadsProjectsWithAnXmlNamespace()
    {
        string project = this.Write(@"src\App\Old.csproj",
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup><PackageReference Include=\"A\" Version=\"1.0\" /></ItemGroup></Project>");

        Assert.Equal([new PackageInfo("A", "1.0")], ProjectPackages.Read(project));
    }

    [Fact]
    public void ReturnsNothingForAFileThatCannotBeRead()
    {
        Assert.Empty(ProjectPackages.Read(this.Write(@"src\App\Broken.csproj", "<Project><unclosed>")));
        Assert.Empty(ProjectPackages.Read(Path.Combine(this.root, "missing.csproj")));
    }
}
