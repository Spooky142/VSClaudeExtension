using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class ChangeFormatterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS-fmt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCode code = new();

    public ChangeFormatterTests() => Directory.CreateDirectory(Path.Combine(this.root, "src"));

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    private string Write(string name, string text)
    {
        string path = Path.Combine(this.root, "src", name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private Task<IReadOnlyList<string>> FormatAsync(IReadOnlyList<FileChange> changes, Func<string, bool>? unsaved = null) =>
        ChangeFormatter.FormatAsync(changes, this.root, this.code, unsaved ?? (_ => false), CancellationToken.None, syncWait: TimeSpan.FromSeconds(2));

    [Fact]
    public async Task FormatsTheLinesClaudeChangedAndKeepsTheFilesEncoding()
    {
        const string Before = "a\nb\nc\n";
        const string After = "a\nB\nc\n";
        string path = this.Write("A.cs", After);
        this.code.Result = "a\n  B\nc\n";

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, Before, After)]);

        Assert.Equal(["src/A.cs"], formatted);
        Assert.Equal("a\n  B\nc\n", File.ReadAllText(path));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(path).Take(3).Select(b => (int)b)); // still UTF-8 with a BOM
        Assert.Equal([(2, 2)], this.code.Lines);
        Assert.Equal(After, this.code.ExpectedText);
    }

    [Fact]
    public async Task TreatsSeveralChangesToAFileAsOne()
    {
        string path = this.Write("A.cs", "a\nB\nC\n");
        this.code.Result = "a\nB\n  C\n";

        await this.FormatAsync([new FileChange(path, "a\nb\nc\n", "a\nB\nc\n"), new FileChange(path, "a\nB\nc\n", "a\nB\nC\n")]);

        Assert.Equal([(2, 3)], this.code.Lines); // from how the file began to how it ended
    }

    [Fact]
    public async Task LeavesAFileAloneThatChangedSinceClaudeWroteIt()
    {
        string path = this.Write("A.cs", "a\nsomething else\n");
        this.code.Result = "formatted";

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")]);

        Assert.Empty(formatted);
        Assert.Equal("a\nsomething else\n", File.ReadAllText(path));
        Assert.Equal(0, this.code.Calls);
    }

    [Fact]
    public async Task LeavesAFileAloneThatHasUnsavedChangesInTheEditor()
    {
        string path = this.Write("A.cs", "a\nB\n");
        this.code.Result = "formatted";

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")], unsaved: _ => true);

        Assert.Empty(formatted);
        Assert.Equal("a\nB\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task LeavesAFileAloneThatChangesWhileFormatting()
    {
        string path = this.Write("A.cs", "a\nB\n");
        this.code.Result = "a\n  B\n";
        this.code.OnFormat = () => File.WriteAllText(path, "the user typed this\n");

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")]);

        Assert.Empty(formatted);
        Assert.Equal("the user typed this\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task SkipsDeletedFilesFilesOutsideTheRepositoryAndUnchangedOnes()
    {
        string gone = Path.Combine(this.root, "src", "Gone.cs");
        string outside = Path.Combine(Path.GetTempPath(), "ClaudeForVS-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(outside, "x\n");
        try
        {
            string same = this.Write("Same.cs", "a\n");
            this.code.Result = "formatted";

            IReadOnlyList<string> formatted = await this.FormatAsync(
            [
                new FileChange(gone, "a\n", string.Empty),
                new FileChange(outside, "w\n", "x\n"),
                new FileChange(same, "a\n", "a\n"),
            ]);

            Assert.Empty(formatted);
            Assert.Equal(0, this.code.Calls);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task WaitsForVisualStudioToCatchUpWithAFileJustWritten()
    {
        string path = this.Write("A.cs", "a\nB\n");
        this.code.Result = "a\n  B\n";
        this.code.First.Enqueue(FormatStatus.OutOfDate); // Visual Studio still has the file as it was
        this.code.First.Enqueue(FormatStatus.OutOfDate);

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")]);

        Assert.Equal(["src/A.cs"], formatted);
        Assert.Equal(3, this.code.Calls);
        Assert.Equal("a\n  B\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task GivesUpWhenVisualStudioNeverCatchesUp()
    {
        string path = this.Write("A.cs", "a\nB\n");
        for (int i = 0; i < 100; i++)
        {
            this.code.First.Enqueue(FormatStatus.OutOfDate);
        }

        IReadOnlyList<string> formatted = await ChangeFormatter.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")], this.root, this.code, _ => false, CancellationToken.None, syncWait: TimeSpan.FromMilliseconds(600));

        Assert.Empty(formatted);
        Assert.Equal("a\nB\n", File.ReadAllText(path));
        Assert.InRange(this.code.Calls, 2, 12);
    }

    [Fact]
    public async Task DoesNothingWhenThereIsNothingToFormat()
    {
        string path = this.Write("A.cs", "a\nB\n");
        this.code.Result = null; // already tidy, or not a file Roslyn knows

        IReadOnlyList<string> formatted = await this.FormatAsync([new FileChange(path, "a\nb\n", "a\nB\n")]);

        Assert.Empty(formatted);
        Assert.Equal("a\nB\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task FormatsOnlyWhatClaudeChangedWithRealRoslyn()
    {
        const string Before =
            "namespace Shop\n" +
            "{\n" +
            "    public class Tidy\n" +
            "    {\n" +
            "    public int   Old;\n" + // untidy, but not Claude's
            "    }\n" +
            "}\n";
        const string After =
            "namespace Shop\n" +
            "{\n" +
            "    public class Tidy\n" +
            "    {\n" +
            "    public int   Old;\n" +
            "            public int   New;\n" + // Claude's, badly indented
            "    }\n" +
            "}\n";
        string path = this.Write("Tidy.cs", After);

        using var workspace = new AdhocWorkspace();
        Project project = workspace.AddProject("Shop", LanguageNames.CSharp);
        project = project.AddDocument("Tidy.cs", SourceText.From(After), filePath: path).Project;
        Assert.True(workspace.TryApplyChanges(project.Solution));
        var roslyn = new RoslynCodeIntelligence(() => workspace.CurrentSolution);

        IReadOnlyList<string> formatted = await ChangeFormatter.FormatAsync([new FileChange(path, Before, After)], this.root, roslyn, _ => false, CancellationToken.None);

        Assert.Equal(["src/Tidy.cs"], formatted);
        string text = File.ReadAllText(path);
        Assert.Contains("        public int New;\n", text);
        Assert.Contains("    public int   Old;\n", text); // left as it was
        Assert.DoesNotContain("\r\n", text); // the file's line endings were kept
    }

    private sealed class FakeCode : ICodeIntelligence
    {
        public string? Result { get; set; }

        /// <summary>Answers before the real one, e.g. OutOfDate while Visual Studio catches up.</summary>
        public Queue<FormatStatus> First { get; } = new();

        public int Calls { get; private set; }

        public IReadOnlyList<(int Start, int End)> Lines { get; private set; } = [];

        public string? ExpectedText { get; private set; }

        public Action? OnFormat { get; set; }

        public Task<FormatResult> FormatAsync(string fullPath, string expectedText, IReadOnlyList<(int Start, int End)> lines, CancellationToken cancellationToken)
        {
            this.Calls++;
            this.Lines = lines;
            this.ExpectedText = expectedText;
            this.OnFormat?.Invoke();
            if (this.First.Count > 0)
            {
                return Task.FromResult(new FormatResult(this.First.Dequeue()));
            }

            return Task.FromResult(this.Result is null ? new FormatResult(FormatStatus.AlreadyFormatted) : new FormatResult(FormatStatus.Formatted, this.Result));
        }

        public Task<string> GoToDefinitionAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> FindReferencesAsync(CodeSite site, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> FindSymbolsAsync(string query, string kind, string repoRoot, Func<string, bool> canRead, int maxResults, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> OutlineAsync(string fullPath, string repoRoot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RenamePlan> PlanRenameAsync(CodeSite site, string newName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
