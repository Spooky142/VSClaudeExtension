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

/// <summary>The code tools against a real Roslyn workspace holding a small solution.</summary>
[Collection("Visual Studio hosts")]
public sealed class CodeIntelligenceTests : IDisposable
{
    private const string Repo = @"C:\work\repo";
    private const string OrderPath = Repo + @"\src\Order.cs";
    private const string ReportPath = Repo + @"\src\Report.cs";

    private const string OrderSource =
        "namespace Shop\n" +
        "{\n" +
        "    /// <summary>A customer's order.</summary>\n" +
        "    public class Order\n" +
        "    {\n" +
        "        /// <summary>The sum of all lines.</summary>\n" +
        "        public decimal Total() => 10m;\n" +
        "\n" +
        "        public int Count { get; set; }\n" +
        "    }\n" +
        "}\n";

    private const string ReportSource =
        "namespace Shop\n" +
        "{\n" +
        "    public class Report\n" +
        "    {\n" +
        "        public decimal Run(Order order)\n" +
        "        {\n" +
        "            decimal sum = order.Total();\n" +
        "            return sum + order.Total();\n" +
        "        }\n" +
        "    }\n" +
        "}\n";

    private readonly AdhocWorkspace workspace = new();
    private readonly RoslynCodeIntelligence code;

    public CodeIntelligenceTests()
    {
        ProjectInfo project = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Shop", "Shop", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        Project added = this.workspace.AddProject(project);
        added = added.AddDocument("Order.cs", SourceText.From(OrderSource), filePath: OrderPath).Project;
        added = added.AddDocument("Report.cs", SourceText.From(ReportSource), filePath: ReportPath).Project;
        Assert.True(this.workspace.TryApplyChanges(added.Solution));
        this.code = new RoslynCodeIntelligence(() => this.workspace.CurrentSolution);
    }

    public void Dispose()
    {
        this.workspace.Dispose();
        CodeIntelligenceHost.Current = null;
    }

    private static Func<string, bool> AnyFile => _ => true;

    [Fact]
    public async Task FindsADefinitionFromAUse()
    {
        string result = await this.code.GoToDefinitionAsync(new CodeSite(ReportPath, 7, "Total"), Repo, AnyFile, CancellationToken.None);

        Assert.StartsWith("method decimal Order.Total()", result);
        Assert.Contains("Defined at src/Order.cs:7", result);
        Assert.Contains("7: public decimal Total() => 10m;", result);
        Assert.Contains("Documentation: The sum of all lines.", result);
    }

    [Fact]
    public async Task AcceptsAQualifiedNameAndACallOnTheLine()
    {
        string result = await this.code.GoToDefinitionAsync(new CodeSite(ReportPath, 7, "Order.Total()"), Repo, AnyFile, CancellationToken.None);

        Assert.Contains("Defined at src/Order.cs:7", result);
    }

    [Fact]
    public async Task SaysWhenTheNameIsNotOnTheLine()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.GoToDefinitionAsync(new CodeSite(ReportPath, 7, "Missing"), Repo, AnyFile, CancellationToken.None));

        Assert.Contains("Couldn't find a symbol named \"Missing\" on line 7", ex.Message);
        Assert.Contains("order.Total()", ex.Message);
    }

    [Fact]
    public async Task RejectsALineOutsideTheFile()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.GoToDefinitionAsync(new CodeSite(ReportPath, 99, "Total"), Repo, AnyFile, CancellationToken.None));

        Assert.Contains("line 99 doesn't exist", ex.Message);
    }

    [Fact]
    public async Task RejectsAFileThatIsNotInTheSolution()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.GoToDefinitionAsync(new CodeSite(Repo + @"\src\Other.cs", 1, "x"), Repo, AnyFile, CancellationToken.None));

        Assert.Contains("isn't part of the solution", ex.Message);
    }

    [Fact]
    public async Task ListsEveryLineThatUsesASymbol()
    {
        string result = await this.code.FindReferencesAsync(new CodeSite(OrderPath, 7, "Total"), Repo, AnyFile, 100, CancellationToken.None);

        Assert.Contains("2 lines in 1 file refer to method decimal Order.Total()", result);
        Assert.Contains("src/Report.cs:7: decimal sum = order.Total();", result);
        Assert.Contains("src/Report.cs:8: return sum + order.Total();", result);
    }

    [Fact]
    public async Task LeavesOutFilesThePermissionRulesHide()
    {
        string result = await this.code.FindReferencesAsync(new CodeSite(OrderPath, 7, "Total"), Repo, path => !path.EndsWith("Report.cs"), 100, CancellationToken.None);

        Assert.DoesNotContain("src/Report.cs:7", result);
        Assert.Contains("restricted by your permission settings", result);
    }

    [Fact]
    public async Task LimitsTheReferencesShown()
    {
        string result = await this.code.FindReferencesAsync(new CodeSite(OrderPath, 7, "Total"), Repo, AnyFile, 1, CancellationToken.None);

        Assert.Contains("src/Report.cs:7", result);
        Assert.DoesNotContain("src/Report.cs:8", result);
        Assert.Contains("1 more not shown", result);
    }

    [Fact]
    public async Task SearchesTheSolutionBySymbolName()
    {
        string result = await this.code.FindSymbolsAsync("Total", string.Empty, Repo, AnyFile, 50, CancellationToken.None);

        Assert.Contains("method decimal Order.Total()  src/Order.cs:7", result);

        string none = await this.code.FindSymbolsAsync("Total", "type", Repo, AnyFile, 50, CancellationToken.None);
        Assert.StartsWith("No type symbols match", none);
    }

    [Fact]
    public async Task OutlinesAFile()
    {
        string result = await this.code.OutlineAsync(OrderPath, Repo, CancellationToken.None);

        Assert.StartsWith("src/Order.cs:", result);
        Assert.Contains("4: class Order", result);
        Assert.Contains("7:   public decimal Total()", result);
        Assert.Contains("9:   public int Count", result);
        Assert.DoesNotContain("get_Count", result); // accessors aren't listed
    }

    [Fact]
    public async Task PlansARenameAcrossFiles()
    {
        RenamePlan plan = await this.code.PlanRenameAsync(new CodeSite(OrderPath, 7, "Total"), "Sum", CancellationToken.None);

        Assert.Equal("Order.Total()", plan.Symbol);
        Assert.Equal(2, plan.Files.Count);
        RenamedFile order = plan.Files.Single(f => f.FullPath == OrderPath);
        Assert.Contains("public decimal Sum() => 10m;", order.After);
        RenamedFile report = plan.Files.Single(f => f.FullPath == ReportPath);
        Assert.Contains("order.Sum()", report.After);
        Assert.DoesNotContain("Total", report.After);
        Assert.Contains("order.Total()", report.Before); // nothing was changed in the workspace
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("1Start")]
    [InlineData("")]
    public async Task RefusesToRenameToAnInvalidName(string name)
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.PlanRenameAsync(new CodeSite(OrderPath, 7, "Total"), name, CancellationToken.None));

        Assert.Contains("isn't a valid identifier", ex.Message);
    }

    [Fact]
    public async Task RefusesToRenameToTheSameName()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.PlanRenameAsync(new CodeSite(OrderPath, 7, "Total"), "Total", CancellationToken.None));

        Assert.Contains("already has that name", ex.Message);
    }

    [Fact]
    public async Task RefusesToRenameSomethingFromALibrary()
    {
        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => this.code.PlanRenameAsync(new CodeSite(ReportPath, 7, "decimal"), "Money", CancellationToken.None));

        Assert.Contains("can't be renamed", ex.Message);
    }

    [Fact]
    public async Task FormatsOnlyTheGivenLines()
    {
        const string Messy =
            "namespace Shop\n" +
            "{\n" +
            "    public class Tidy\n" +
            "    {\n" +
            "    public int   A;\n" + // not Claude's
            "            public int   B;\n" + // Claude's
            "    }\n" +
            "}\n";
        const string TidyPath = Repo + @"\src\Tidy.cs";
        this.workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(this.workspace.CurrentSolution.Projects.First().Id),
            "Tidy.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Messy), VersionStamp.Create())),
            filePath: TidyPath));

        FormatResult result = await this.code.FormatAsync(TidyPath, Messy, [(6, 6)], CancellationToken.None);

        Assert.Equal(FormatStatus.Formatted, result.Status);
        Assert.Contains("        public int B;\n", result.Text);
        Assert.Contains("    public int   A;\n", result.Text); // left as it was
    }

    [Fact]
    public async Task DoesNotFormatWhenVisualStudioHoldsOtherText()
    {
        FormatResult result = await this.code.FormatAsync(OrderPath, "something else entirely", [(1, 3)], CancellationToken.None);

        Assert.Equal(FormatStatus.OutOfDate, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task DoesNothingWhenTheLinesAreAlreadyFormatted()
    {
        FormatResult result = await this.code.FormatAsync(OrderPath, OrderSource, [(4, 5)], CancellationToken.None);

        Assert.Equal(FormatStatus.AlreadyFormatted, result.Status);

        FormatResult unknown = await this.code.FormatAsync(Repo + @"\src\Elsewhere.cs", OrderSource, [(1, 2)], CancellationToken.None);
        Assert.Equal(FormatStatus.NotInSolution, unknown.Status);
    }

    [Fact]
    public async Task FindsTheMemberTheCaretIsIn()
    {
        Document document = this.workspace.CurrentSolution.GetDocumentIdsWithFilePath(OrderPath).Select(id => this.workspace.CurrentSolution.GetDocument(id)!).First();
        SourceText text = await document.GetTextAsync();
        int inTotal = text.Lines[6].Start + 20;
        int inCount = text.Lines[8].Start + 20;

        EnclosingMember? total = await RoslynCodeIntelligence.FindEnclosingMemberAsync(document, inTotal, CancellationToken.None);
        EnclosingMember? count = await RoslynCodeIntelligence.FindEnclosingMemberAsync(document, inCount, CancellationToken.None);

        Assert.Equal(("Total", "method", 7, 7, true), (total!.Name, total.Kind, total.StartLine, total.EndLine, total.HasDocumentation));
        Assert.Equal(("Count", "property", 9, 9, false), (count!.Name, count.Kind, count.StartLine, count.EndLine, count.HasDocumentation));
    }

    [Fact]
    public async Task ReadsToolInputAndChecksARenameAgainstTheFilesOnDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            // The same solution again, but with its files under a real folder.
            using var disk = new AdhocWorkspace();
            Project project = disk.AddProject("Shop", LanguageNames.CSharp).AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            string order = Path.Combine(root, "src", "Order.cs");
            string report = Path.Combine(root, "src", "Report.cs");
            File.WriteAllText(order, OrderSource);
            File.WriteAllText(report, ReportSource);
            project = project.AddDocument("Order.cs", SourceText.From(OrderSource), filePath: order).Project;
            project = project.AddDocument("Report.cs", SourceText.From(ReportSource), filePath: report).Project;
            Assert.True(disk.TryApplyChanges(project.Solution));
            CodeIntelligenceHost.Current = new RoslynCodeIntelligence(() => disk.CurrentSolution);
            var files = new RepoFiles(root);

            Dictionary<string, JsonElement> input = Input(new { path = "src/Report.cs", line = 7, symbol = "Total", new_name = "Sum" });
            RenamePlan plan = await CodeTools.PlanRenameAsync(input, files, root, CancellationToken.None);

            Assert.Equal(2, plan.Files.Count);
            Assert.Equal("Rename Order.Total() to Sum in 2 files", CodeTools.Title(plan));
            string detail = CodeTools.Describe(plan, root);
            Assert.Contains("src/Order.cs", detail);
            Assert.Contains("+ " + "        public decimal Sum() => 10m;", detail);

            // The file on disk moved on since Visual Studio last looked.
            File.WriteAllText(report, ReportSource + "// edited\n");
            ToolInputException stale = await Assert.ThrowsAsync<ToolInputException>(() => CodeTools.PlanRenameAsync(input, files, root, CancellationToken.None, syncWait: TimeSpan.Zero));
            Assert.Contains("out of date", stale.Message);

            ToolInputException noLine = await Assert.ThrowsAsync<ToolInputException>(
                () => CodeTools.GoToDefinitionAsync(Input(new { path = "src/Report.cs", symbol = "Total" }), files, root, AnyFile, CancellationToken.None));
            Assert.Contains("`line`", noLine.Message);

            ToolInputException outside = await Assert.ThrowsAsync<ToolInputException>(
                () => CodeTools.GoToDefinitionAsync(Input(new { path = @"..\elsewhere.cs", line = 1, symbol = "x" }), files, root, AnyFile, CancellationToken.None));
            Assert.Contains("outside the repository", outside.Message);

            ToolInputException nothing = await Assert.ThrowsAsync<ToolInputException>(
                () => CodeTools.FindSymbolsAsync(Input(new { kind = "type" }), files, root, AnyFile, CancellationToken.None));
            Assert.Contains("`query`", nothing.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CodeToolsSayWhenThereIsNoCodeModel()
    {
        CodeIntelligenceHost.Current = null;
        var files = new RepoFiles(Path.GetTempPath());

        ToolInputException ex = await Assert.ThrowsAsync<ToolInputException>(
            () => CodeTools.FindSymbolsAsync(Input(new { query = "x" }), files, Path.GetTempPath(), AnyFile, CancellationToken.None));

        Assert.Contains("needs Visual Studio", ex.Message);
    }

    private static Dictionary<string, JsonElement> Input(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}

public sealed class ChangedLinesTests
{
    [Fact]
    public void ReportsAddedAndChangedRuns()
    {
        string before = "a\nb\nc\nd\ne\n";
        string after = "a\nB\nc\nd\nx\ny\n";

        Assert.Equal([(2, 2), (5, 6)], UnifiedDiff.ChangedLines(before, after));
    }

    [Fact]
    public void TreatsANewFileAsAllChanged()
    {
        Assert.Equal([(1, 3)], UnifiedDiff.ChangedLines(string.Empty, "a\nb\nc\n"));
    }

    [Fact]
    public void ReportsNothingForIdenticalOrOnlyShorterText()
    {
        Assert.Empty(UnifiedDiff.ChangedLines("a\nb\n", "a\nb\n"));
        Assert.Empty(UnifiedDiff.ChangedLines("a\nb\nc\n", "a\nc\n"));
    }

    [Fact]
    public void IgnoresTheKindOfLineEnding()
    {
        Assert.Equal([(2, 2)], UnifiedDiff.ChangedLines("a\r\nb\r\n", "a\nB\n"));
    }
}
