using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>Tests that set Visual Studio's code model or NuGet, which are process-wide, so they can't run alongside each other.</summary>
[CollectionDefinition("Visual Studio hosts", DisableParallelization = true)]
public sealed class VisualStudioHostsCollection;

/// <summary>The Claude API engine's code and package tools, through its tool loop against a fake API.</summary>
[Collection("Visual Studio hosts")]
public sealed class AgentCodeToolTests : IDisposable
{
    private const string OrderSource =
        "namespace Shop\n" +
        "{\n" +
        "    public class Order\n" +
        "    {\n" +
        "        public decimal Total() => 10m;\n" +
        "    }\n" +
        "}\n";

    private const string ReportSource =
        "namespace Shop\n" +
        "{\n" +
        "    public class Report\n" +
        "    {\n" +
        "        public decimal Run(Order order) => order.Total();\n" +
        "    }\n" +
        "}\n";

    private readonly string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAnthropic api = new();
    private readonly RecordingSink sink = new();
    private readonly AdhocWorkspace workspace = new();
    private readonly FakeNuGet nuget = new();

    public AgentCodeToolTests()
    {
        Directory.CreateDirectory(Path.Combine(this.repo, "src"));
        string order = Path.Combine(this.repo, "src", "Order.cs");
        string report = Path.Combine(this.repo, "src", "Report.cs");
        File.WriteAllText(order, OrderSource);
        File.WriteAllText(report, ReportSource);

        Project project = this.workspace.AddProject("Shop", LanguageNames.CSharp).AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        project = project.AddDocument("Order.cs", SourceText.From(OrderSource), filePath: order).Project;
        project = project.AddDocument("Report.cs", SourceText.From(ReportSource), filePath: report).Project;
        Assert.True(this.workspace.TryApplyChanges(project.Solution));
        CodeIntelligenceHost.Current = new RoslynCodeIntelligence(() => this.workspace.CurrentSolution);
        PackageHostRegistry.Current = this.nuget;
    }

    public void Dispose()
    {
        CodeIntelligenceHost.Current = null;
        PackageHostRegistry.Current = null;
        this.workspace.Dispose();
        this.api.Dispose();
        Directory.Delete(this.repo, recursive: true);
    }

    private ClaudeAgent NewAgent(AgentMode mode) => new("test-key", this.repo, ClaudeModel.Opus, EffortLevel.Low, mode, this.api.Url);

    private Task SendAsync(ClaudeAgent agent, string text) => agent.SendAsync(new ChatPrompt(text), this.sink, CancellationToken.None);

    private string ToolResult(int request)
    {
        JsonElement messages = this.api.Requests[request].RootElement.GetProperty("messages");
        JsonElement result = messages[messages.GetArrayLength() - 1].GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
        JsonElement content = result.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(content.EnumerateArray().Select(b => b.GetProperty("text").GetString()));
    }

    private static string ToolTurn(string name, object input) => Sse.Turn(Sse.ToolUse("toolu_1", name, input), stopReason: "tool_use");

    private string Read(string relative) => File.ReadAllText(Path.Combine(this.repo, relative));

    [Fact]
    public async Task GoToDefinitionAnswersFromTheCodeModel()
    {
        this.api.Reply(ToolTurn("go_to_definition", new { path = "src/Report.cs", line = 5, symbol = "Total" })).Reply(Sse.Turn(Sse.Text("Found it.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Where is Total?");

        Assert.Empty(this.sink.Approvals); // reading needs no approval
        Assert.StartsWith("method decimal Order.Total()", this.ToolResult(1));
        Assert.Contains("Defined at src/Order.cs:5", this.ToolResult(1));
        Assert.Contains(this.sink.Activities, a => a.Kind == ActivityKind.Search && a.Title == "Go to definition of Total (Report.cs:5)");
    }

    [Fact]
    public async Task FindReferencesAndSymbolsWork()
    {
        this.api.Reply(ToolTurn("find_references", new { path = "src/Order.cs", line = 5, symbol = "Total" })).Reply(Sse.Turn(Sse.Text("Done.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Plan);

        await this.SendAsync(agent, "Who uses Total?");

        Assert.Contains("src/Report.cs:5: public decimal Run(Order order) => order.Total();", this.ToolResult(1));
    }

    [Fact]
    public async Task FilesThePermissionRulesDenyAreRefused()
    {
        Directory.CreateDirectory(Path.Combine(this.repo, ".claude"));
        File.WriteAllText(Path.Combine(this.repo, ".claude", "settings.json"), """{ "permissions": { "deny": ["Read(./src/Report.cs)"] } }""");
        this.api.Reply(ToolTurn("go_to_definition", new { path = "src/Report.cs", line = 5, symbol = "Total" })).Reply(Sse.Turn(Sse.Text("Refused.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Auto);

        await this.SendAsync(agent, "Where is Total?");

        Assert.Contains("permission settings", this.ToolResult(1));
        Assert.DoesNotContain("Defined at", this.ToolResult(1));
    }

    [Fact]
    public async Task ARenameIsShownAsOneApprovalAndWrittenWhenAllowed()
    {
        this.api.Reply(ToolTurn("rename_symbol", new { path = "src/Report.cs", line = 5, symbol = "Total", new_name = "Sum" })).Reply(Sse.Turn(Sse.Text("Renamed.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Rename Total to Sum");

        Assert.Equal(["Rename Order.Total() to Sum in 2 files"], this.sink.Approvals);
        Assert.Contains("public decimal Sum() => 10m;", this.Read(@"src\Order.cs"));
        Assert.Contains("order.Sum()", this.Read(@"src\Report.cs"));
        Assert.StartsWith("Renamed Order.Total() to Sum in src\\Order.cs, src\\Report.cs", this.ToolResult(1));
    }

    [Fact]
    public async Task ADeclinedRenameChangesNothing()
    {
        this.sink.Decide = _ => Approval.Deny;
        this.api.Reply(ToolTurn("rename_symbol", new { path = "src/Report.cs", line = 5, symbol = "Total", new_name = "Sum" })).Reply(Sse.Turn(Sse.Text("Okay.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Rename Total to Sum");

        Assert.Equal(OrderSource, this.Read(@"src\Order.cs"));
        Assert.Equal(ReportSource, this.Read(@"src\Report.cs"));
        Assert.Contains("declined", this.ToolResult(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutoModeRenamesWithoutAsking()
    {
        this.api.Reply(ToolTurn("rename_symbol", new { path = "src/Order.cs", line = 5, symbol = "Total", new_name = "Sum" })).Reply(Sse.Turn(Sse.Text("Renamed.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Auto);

        await this.SendAsync(agent, "Rename Total to Sum");

        Assert.Empty(this.sink.Approvals);
        Assert.Contains("order.Sum()", this.Read(@"src\Report.cs"));
    }

    [Fact]
    public async Task PlanModeCannotRename()
    {
        this.api.Reply(ToolTurn("rename_symbol", new { path = "src/Order.cs", line = 5, symbol = "Total", new_name = "Sum" })).Reply(Sse.Turn(Sse.Text("Can't.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Plan);

        await this.SendAsync(agent, "Rename Total to Sum");

        Assert.Empty(this.sink.Approvals);
        Assert.Contains("Plan mode", this.ToolResult(1));
        Assert.Equal(OrderSource, this.Read(@"src\Order.cs"));
    }

    [Fact]
    public async Task ARenameIsRefusedWhenAFileChangedSinceVisualStudioLooked()
    {
        File.WriteAllText(Path.Combine(this.repo, "src", "Report.cs"), ReportSource + "// edited elsewhere\n");
        this.api.Reply(ToolTurn("rename_symbol", new { path = "src/Order.cs", line = 5, symbol = "Total", new_name = "Sum" })).Reply(Sse.Turn(Sse.Text("Stale.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Auto);

        await this.SendAsync(agent, "Rename Total to Sum");

        Assert.Contains("out of date", this.ToolResult(1));
        Assert.Equal(OrderSource, this.Read(@"src\Order.cs"));
    }

    [Fact]
    public async Task ARenameWaitsForVisualStudioToCatchUpWithAFileThatJustChanged()
    {
        string reportPath = Path.Combine(this.repo, "src", "Report.cs");
        string edited = ReportSource + "// edited\n";
        File.WriteAllText(reportPath, edited); // as if Claude had just written it; Visual Studio's copy is still the old one
        Dictionary<string, JsonElement> input = JsonSerializer.SerializeToElement(new { path = "src/Order.cs", line = 5, symbol = "Total", new_name = "Sum" })
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        Task<RenamePlan> planned = CodeTools.PlanRenameAsync(input, new RepoFiles(this.repo), this.repo, CancellationToken.None);
        await Task.Delay(700);
        Solution solution = this.workspace.CurrentSolution;
        Assert.True(this.workspace.TryApplyChanges(solution.WithDocumentText(solution.GetDocumentIdsWithFilePath(reportPath)[0], SourceText.From(edited))));
        RenamePlan plan = await planned;

        Assert.Contains("// edited", plan.Files.Single(f => f.FullPath == reportPath).After);
    }

    [Fact]
    public async Task ListingPackagesNeedsNoApproval()
    {
        this.api.Reply(ToolTurn("list_packages", new { })).Reply(Sse.Turn(Sse.Text("Listed.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "What packages?");

        Assert.Empty(this.sink.Approvals);
        Assert.Contains("Serilog 3.1.1", this.ToolResult(1));
    }

    [Fact]
    public async Task AddingAPackageAsksInCodingMode()
    {
        this.api.Reply(ToolTurn("add_package", new { project = "App", package = "Polly" })).Reply(Sse.Turn(Sse.Text("Added.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Add Polly");

        Assert.Equal(["Add NuGet package Polly (latest) to App"], this.sink.Approvals);
        Assert.Equal([("App", "Polly", (string?)null)], this.nuget.Installs);
        Assert.StartsWith("Added Polly", this.ToolResult(1));
    }

    [Fact]
    public async Task ADeclinedPackageChangeInstallsNothing()
    {
        this.sink.Decide = _ => Approval.Deny;
        this.api.Reply(ToolTurn("remove_package", new { project = "App", package = "Serilog" })).Reply(Sse.Turn(Sse.Text("Okay.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Coding);

        await this.SendAsync(agent, "Remove Serilog");

        Assert.Empty(this.nuget.Removals);
        Assert.Contains("declined", this.ToolResult(1), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanModeCannotChangePackages()
    {
        this.api.Reply(ToolTurn("add_package", new { project = "App", package = "Polly" })).Reply(Sse.Turn(Sse.Text("Can't.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Plan);

        await this.SendAsync(agent, "Add Polly");

        Assert.Empty(this.sink.Approvals);
        Assert.Empty(this.nuget.Installs);
        Assert.Contains("Plan mode", this.ToolResult(1));
    }

    [Fact]
    public async Task AnInvalidPackageRequestIsAnErrorForClaude()
    {
        this.api.Reply(ToolTurn("add_package", new { project = "App", package = "not a package" })).Reply(Sse.Turn(Sse.Text("Oops.")));
        using ClaudeAgent agent = this.NewAgent(AgentMode.Auto);

        await this.SendAsync(agent, "Add it");

        Assert.Contains("NuGet package ID", this.ToolResult(1));
        Assert.Empty(this.nuget.Installs);
    }

    private sealed class FakeNuGet : IPackageHost
    {
        private readonly List<PackageInfo> packages = [new PackageInfo("Serilog", "3.1.1")];

        public List<(string Project, string Package, string? Version)> Installs { get; } = [];

        public List<(string Project, string Package)> Removals { get; } = [];

        public Task<IReadOnlyList<PackageProject>> GetProjectsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PackageProject>>([new PackageProject("App", Path.Combine(Path.GetTempPath(), "App.csproj"), this.packages.ToList())]);

        public Task InstallAsync(string project, string packageId, string? version, CancellationToken cancellationToken)
        {
            this.Installs.Add((project, packageId, version));
            this.packages.Add(new PackageInfo(packageId, version ?? "1.0.0"));
            return Task.CompletedTask;
        }

        public Task UninstallAsync(string project, string packageId, CancellationToken cancellationToken)
        {
            this.Removals.Add((project, packageId));
            this.packages.RemoveAll(p => p.Id == packageId);
            return Task.CompletedTask;
        }
    }
}
