using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ClaudeForVS.Tests;

/// <summary>The light bulb's code fixes, against an in-memory workspace with a fixer standing in for Visual Studio's.</summary>
[Collection("Visual Studio hosts")]
public sealed class CodeFixTests : IDisposable
{
    private const string BuilderSource =
        "namespace Shop\n" +
        "{\n" +
        "    public class Builder\n" +
        "    {\n" +
        "        public object Make() => new StringBuilder();\n" +
        "    }\n" +
        "}\n";

    private readonly string repo = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));
    private readonly string builderPath;
    private readonly AdhocWorkspace workspace = new();
    private readonly RoslynCodeIntelligence code;

    public CodeFixTests()
    {
        Directory.CreateDirectory(Path.Combine(this.repo, "src"));
        this.builderPath = Path.Combine(this.repo, "src", "Builder.cs");
        File.WriteAllText(this.builderPath, BuilderSource);

        Project project = this.workspace.AddProject("Shop", LanguageNames.CSharp).AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        project = project.AddDocument("Builder.cs", SourceText.From(BuilderSource), filePath: this.builderPath).Project;
        Assert.True(this.workspace.TryApplyChanges(project.Solution));
        this.code = new RoslynCodeIntelligence(() => this.workspace.CurrentSolution, _ => [new MissingTypeFixer()]);
        CodeIntelligenceHost.Current = this.code;
    }

    public void Dispose()
    {
        CodeIntelligenceHost.Current = null;
        this.workspace.Dispose();
        Directory.Delete(this.repo, recursive: true);
    }

    [Fact]
    public async Task ListsTheDiagnosticsOnALineWithTheirFixes()
    {
        string result = await this.code.GetCodeFixesAsync(this.builderPath, 5, string.Empty, this.repo, CancellationToken.None);

        Assert.StartsWith("src/Builder.cs:5 has 1 diagnostic:", result);
        Assert.Contains("error CS0246: The type or namespace name 'StringBuilder' could not be found", result);
        Assert.Contains("  - using System.Text;", result);
        Assert.Contains("  - Generate type › Generate class 'StringBuilder' in new file", result); // nested fixes are flattened
        Assert.Contains("apply_code_fix", result);
    }

    [Fact]
    public async Task SaysWhenALineHasNoProblems()
    {
        string result = await this.code.GetCodeFixesAsync(this.builderPath, 3, string.Empty, this.repo, CancellationToken.None);
        Assert.Equal("There are no errors or warnings on src/Builder.cs:3.", result);

        string other = await this.code.GetCodeFixesAsync(this.builderPath, 5, "CS0168", this.repo, CancellationToken.None);
        Assert.StartsWith("There's no CS0168 on src/Builder.cs:5", other);
    }

    [Fact]
    public async Task PlansAFixWithoutChangingAnything()
    {
        CodeFixPlan plan = await this.code.PlanCodeFixAsync(this.builderPath, 5, "CS0246", "using System.Text;", this.repo, CancellationToken.None);

        Assert.Equal(("CS0246", "using System.Text;"), (plan.Diagnostic, plan.Fix));
        RenamedFile file = Assert.Single(plan.Files);
        Assert.StartsWith("using System.Text;", file.After);
        Assert.Equal(BuilderSource, file.Before);
        Assert.False(file.IsNew);
        Assert.Equal(BuilderSource, File.ReadAllText(this.builderPath));
    }

    [Fact]
    public async Task AFixCanCreateAFile()
    {
        CodeFixPlan plan = await this.code.PlanCodeFixAsync(
            this.builderPath, 5, "cs0246", "Generate type > Generate class 'StringBuilder' in new file", this.repo, CancellationToken.None);

        RenamedFile file = Assert.Single(plan.Files);
        Assert.True(file.IsNew);
        Assert.Equal(Path.Combine(this.repo, "src", "StringBuilder.cs"), file.FullPath);
        Assert.Contains("public class StringBuilder", file.After);
    }

    [Fact]
    public async Task AnUnknownFixListsTheOnesThereAre()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.PlanCodeFixAsync(this.builderPath, 5, "CS0246", "Add a reference", this.repo, CancellationToken.None));

        Assert.Contains("No fix called \"Add a reference\"", ex.Message);
        Assert.Contains("\"using System.Text;\"", ex.Message);
    }

    [Fact]
    public async Task ToolInputIsCheckedAndAFixIsWrittenWithTheNewFile()
    {
        var files = new RepoFiles(this.repo);
        ToolInputException noFix = await Assert.ThrowsAsync<ToolInputException>(
            () => CodeTools.PlanCodeFixAsync(Input(new { path = "src/Builder.cs", line = 5, diagnostic = "CS0246" }), files, this.repo, CancellationToken.None));
        Assert.Contains("`fix`", noFix.Message);

        CodeFixPlan plan = await CodeTools.PlanCodeFixAsync(
            Input(new { path = "src/Builder.cs", line = 5, diagnostic = "CS0246", fix = "Generate type › Generate class 'StringBuilder' in new file" }), files, this.repo, CancellationToken.None);
        Assert.Equal("Apply \"Generate type › Generate class 'StringBuilder' in new file\" (CS0246)", CodeTools.Title(plan));
        Assert.Contains("src/StringBuilder.cs (new file)", CodeTools.Describe(plan, this.repo));

        IReadOnlyList<FileChange> changes = CodeTools.ApplyCodeFix(plan, files, this.repo);

        FileChange created = Assert.Single(changes);
        Assert.Equal(string.Empty, created.Before);
        Assert.Contains("public class StringBuilder", File.ReadAllText(Path.Combine(this.repo, "src", "StringBuilder.cs")));
    }

    [Fact]
    public async Task TheApiEngineAppliesAnApprovedFix()
    {
        using var api = new FakeAnthropic();
        var sink = new RecordingSink();
        api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "get_code_fixes", new { path = "src/Builder.cs", line = 5 }), stopReason: "tool_use"))
            .Reply(Sse.Turn(Sse.ToolUse("toolu_2", "apply_code_fix", new { path = "src/Builder.cs", line = 5, diagnostic = "CS0246", fix = "using System.Text;" }), stopReason: "tool_use"))
            .Reply(Sse.Turn(Sse.Text("Fixed.")));
        using var agent = new ClaudeAgent("test-key", this.repo, ClaudeModel.Opus, EffortLevel.Low, AgentMode.Coding, api.Url);

        await agent.SendAsync(new ChatPrompt("Fix the build"), sink, CancellationToken.None);

        Assert.Equal(["Apply \"using System.Text;\" (CS0246)"], sink.Approvals); // listing the fixes needs no approval
        Assert.StartsWith("using System.Text;", File.ReadAllText(this.builderPath));
    }

    [Fact]
    public async Task PlanModeCannotApplyAFix()
    {
        using var api = new FakeAnthropic();
        var sink = new RecordingSink();
        api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "apply_code_fix", new { path = "src/Builder.cs", line = 5, diagnostic = "CS0246", fix = "using System.Text;" }), stopReason: "tool_use"))
            .Reply(Sse.Turn(Sse.Text("Can't.")));
        using var agent = new ClaudeAgent("test-key", this.repo, ClaudeModel.Opus, EffortLevel.Low, AgentMode.Plan, api.Url);

        await agent.SendAsync(new ChatPrompt("Fix it"), sink, CancellationToken.None);

        Assert.Empty(sink.Approvals);
        Assert.Equal(BuilderSource, File.ReadAllText(this.builderPath));
    }

    private static Dictionary<string, JsonElement> Input(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>Offers to add a using for a missing type, or (nested, as Visual Studio's "Generate type" is) to create it in a new file.</summary>
    private sealed class MissingTypeFixer : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["CS0246"];

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            Document document = context.Document;
            context.RegisterCodeFix(
                CodeAction.Create(
                    "using System.Text;",
                    async ct => document.WithText((await document.GetTextAsync(ct)).WithChanges(new TextChange(new TextSpan(0, 0), "using System.Text;\n\n"))),
                    "using"),
                context.Diagnostics);

            string newPath = Path.Combine(Path.GetDirectoryName(document.FilePath)!, "StringBuilder.cs");
            CodeAction generate = CodeAction.Create(
                "Generate class 'StringBuilder' in new file",
                ct => Task.FromResult(document.Project.AddDocument("StringBuilder.cs", SourceText.From("namespace Shop\n{\n    public class StringBuilder\n    {\n    }\n}\n"), filePath: newPath).Project.Solution),
                "generate");
            context.RegisterCodeFix(CodeAction.Create("Generate type", [generate], isInlinable: false), context.Diagnostics);
            return Task.CompletedTask;
        }
    }
}
