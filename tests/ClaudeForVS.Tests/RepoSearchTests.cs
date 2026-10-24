using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Anthropic.Models.Messages;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;
using ClaudeForVS.ToolWindows;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class RepoSearchTests : IDisposable
{
    private static readonly string[] Files =
    [
        "README.md",
        "src/App/App.csproj",
        "src/App/Program.cs",
        "src/App/Views/Main.xaml",
        "src/App/Views/Main.xaml.cs",
        "tests/AppTests/ProgramTests.cs",
    ];

    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public RepoSearchTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void PatternWithoutSlashMatchesNamesAtAnyDepth()
    {
        Assert.Equal(["src/App/Program.cs", "src/App/Views/Main.xaml.cs", "tests/AppTests/ProgramTests.cs"], RepoSearch.Glob(Files, "*.cs", string.Empty));
        Assert.Equal(["src/App/Program.cs", "tests/AppTests/ProgramTests.cs"], RepoSearch.Glob(Files, "program*.CS", string.Empty));
    }

    [Fact]
    public void PatternWithSlashMatchesPathsRelativeToTheFolder()
    {
        Assert.Equal(["src/App/Program.cs", "src/App/Views/Main.xaml.cs"], RepoSearch.Glob(Files, "src/**/*.cs", string.Empty));
        Assert.Equal(["src/App/Views/Main.xaml"], RepoSearch.Glob(Files, "Views/*.xaml", "src/App"));
        Assert.Equal(["src/App/App.csproj", "src/App/Views/Main.xaml"], RepoSearch.Glob(Files, "**/*.{csproj,xaml}", string.Empty));
        Assert.Empty(RepoSearch.Glob(Files, "*/Program.cs", string.Empty)); // * stays within one folder
    }

    [Fact]
    public void CharacterClassesAndBadPatterns()
    {
        Assert.Matches(RepoSearch.GlobToRegex("file[0-9].txt"), "file7.txt");
        Assert.DoesNotMatch(RepoSearch.GlobToRegex("file[!0-9].txt"), "file7.txt");
        Assert.Throws<ToolInputException>(() => RepoSearch.GlobToRegex("{a,b"));
    }

    [Fact]
    public void GrepShowsMatchesWithContext()
    {
        this.Write("a.cs", "using System;\nclass A\n{\n    void Run() { }\n}\n");
        this.Write("b.cs", "class B { void run() { } }\n");

        string content = RepoSearch.Grep(this.root, ["a.cs", "b.cs"], @"void Run\(", ignoreCase: false, GrepOutput.Content, context: 1, maxResults: 100, CancellationToken.None);
        Assert.Contains("a.cs-3-{\na.cs:4:    void Run() { }\na.cs-5-}", content.Replace("\r\n", "\n"));
        Assert.DoesNotContain("b.cs", content);
        Assert.EndsWith("[1 matching line in 1 file]", content);

        string files = RepoSearch.Grep(this.root, ["a.cs", "b.cs"], @"void run\(", ignoreCase: true, GrepOutput.FilesWithMatches, 0, 100, CancellationToken.None);
        Assert.StartsWith("a.cs\nb.cs\n", files);

        string counts = RepoSearch.Grep(this.root, ["a.cs", "b.cs"], "class", ignoreCase: false, GrepOutput.Count, 0, 100, CancellationToken.None);
        Assert.StartsWith("a.cs:1\nb.cs:1\n", counts);
    }

    [Fact]
    public void GrepSkipsBinaryFilesAndReportsNoMatches()
    {
        File.WriteAllBytes(Path.Combine(this.root, "bin.dat"), [0x66, 0x6F, 0x6F, 0x00, 0x01]);
        Assert.Equal("No matches.", RepoSearch.Grep(this.root, ["bin.dat"], "foo", false, GrepOutput.Content, 0, 100, CancellationToken.None));
    }

    [Fact]
    public void GrepCutsOffAtMaxResults()
    {
        this.Write("many.txt", "miss\nhit\nhit\nhit\n");
        string result = RepoSearch.Grep(this.root, ["many.txt"], "hit", false, GrepOutput.Content, 0, 2, CancellationToken.None);
        Assert.Equal(2, result.Split(["many.txt:"], StringSplitOptions.None).Length - 1);
        Assert.Contains("results were cut off", result);
    }

    [Fact]
    public void GrepRejectsInvalidRegex() =>
        Assert.Throws<ToolInputException>(() => RepoSearch.Grep(this.root, [], "(", false, GrepOutput.Content, 0, 10, CancellationToken.None));

    private void Write(string path, string text)
    {
        string full = Path.Combine(this.root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}

public class WebResultTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SearchResultsListTitlesAndUrls()
    {
        var (kind, title, detail) = ClaudeAgent.DescribeWebResult(
            "web_search_tool_result",
            Json("""{"query":"wpf flowdocument"}"""),
            Json("""[{"type":"web_search_result","title":"FlowDocument","url":"https://learn.microsoft.com/a","encrypted_content":"x"},{"type":"web_search_result","url":"https://example.com"}]"""));
        Assert.Equal(ActivityKind.Web, kind);
        Assert.Equal("Searched the web: wpf flowdocument", title);
        Assert.Equal("FlowDocument — https://learn.microsoft.com/a\nhttps://example.com", detail);
    }

    [Fact]
    public void ErrorsAreShownAsFailures()
    {
        var (kind, title, detail) = ClaudeAgent.DescribeWebResult(
            "web_fetch_tool_result",
            Json("""{"url":"https://example.com/x"}"""),
            Json("""{"type":"web_fetch_tool_result_error","error_code":"url_not_accessible"}"""));
        Assert.Equal(ActivityKind.Failed, kind);
        Assert.Equal("Couldn't fetch https://example.com/x", title);
        Assert.Equal("url not accessible", detail);
    }

    [Fact]
    public void FetchShowsThePageTitle()
    {
        var (kind, title, detail) = ClaudeAgent.DescribeWebResult(
            "web_fetch_tool_result",
            Json("""{"url":"https://example.com/x"}"""),
            Json("""{"type":"web_fetch_result","url":"https://example.com/x","content":{"type":"document","title":"Example"}}"""));
        Assert.Equal(ActivityKind.Web, kind);
        Assert.Equal("Fetched https://example.com/x", title);
        Assert.Equal("Example\nhttps://example.com/x", detail);
    }

    [Fact]
    public void WebToolsSerializeWithTheirTypeAndName()
    {
        IReadOnlyDictionary<string, JsonElement> search = new WebSearchTool20250305 { MaxUses = 8 }.RawData;
        IReadOnlyDictionary<string, JsonElement> fetch = new WebFetchTool20250910 { MaxUses = 8 }.RawData;
        Assert.Equal("web_search_20250305", search["type"].GetString());
        Assert.Equal("web_search", search["name"].GetString());
        Assert.Equal("web_fetch_20250910", fetch["type"].GetString());
        Assert.Equal("web_fetch", fetch["name"].GetString());

        IReadOnlyDictionary<string, JsonElement> filteredSearch = new WebSearchTool20260209 { MaxUses = 8 }.RawData;
        IReadOnlyDictionary<string, JsonElement> filteredFetch = new WebFetchTool20260209 { MaxUses = 8 }.RawData;
        Assert.Equal("web_search_20260209", filteredSearch["type"].GetString());
        Assert.Equal("web_search", filteredSearch["name"].GetString());
        Assert.Equal("web_fetch_20260209", filteredFetch["type"].GetString());
        Assert.Equal("web_fetch", filteredFetch["name"].GetString());
    }

    [Fact]
    public void HourLongCacheWritesCostTwiceTheInputPrice()
    {
        // Sonnet: $2 input, so $2.50 per million for five-minute writes and $4 for one-hour ones.
        double fiveMinutes = ModelPricing.EstimateCost(ClaudeModel.Sonnet, new TokenUsage(0, 1_000_000, 0, 0));
        double oneHour = ModelPricing.EstimateCost(ClaudeModel.Sonnet, new TokenUsage(0, 1_000_000, 0, 0, CacheWrite1h: 1_000_000));
        double mixed = ModelPricing.EstimateCost(ClaudeModel.Sonnet, new TokenUsage(0, 1_000_000, 0, 0, CacheWrite1h: 500_000));
        Assert.Equal(2.5, fiveMinutes, 6);
        Assert.Equal(4.0, oneHour, 6);
        Assert.Equal(3.25, mixed, 6);
    }

    [Fact]
    public void WebSearchesAddToTheCostEstimate()
    {
        double tokensOnly = ModelPricing.EstimateCost(ClaudeModel.Sonnet, new TokenUsage(1000, 0, 0, 1000));
        double withSearches = ModelPricing.EstimateCost(ClaudeModel.Sonnet, new TokenUsage(1000, 0, 0, 1000, WebSearches: 3));
        Assert.Equal(0.03, withSearches - tokensOnly, 6);
    }

    [Fact]
    public void HaikuPromptsOver100KTokensUseTheHigherRates()
    {
        // 100K cached input and 1K output: $0.001 + $0.0005 at the lower rates.
        double atLimit = ModelPricing.EstimateCost(ClaudeModel.Haiku, new TokenUsage(0, 0, 100_000, 1_000));
        Assert.Equal(0.0015, atLimit, 6);

        // One more token of prompt moves the whole request, output included, to the higher rates.
        double over = ModelPricing.EstimateCost(ClaudeModel.Haiku, new TokenUsage(1, 0, 100_000, 1_000));
        Assert.Equal((0.50 + (100_000 * 0.05) + (1_000 * 2.50)) / 1_000_000, over, 9);
    }
}

public class ChangesEntryTests
{
    private static readonly FileStat[] Stats = [new('M', "src/a.cs", 12, 3, false), new('A', "img.png", 0, 0, true), new('D', "old.txt", 0, 4, false)];

    [Fact]
    public void SummaryCountsFilesAndLines()
    {
        Assert.Equal("3 files changed · +12 −7", ChatEntry.Summarize(Stats));
        var entry = new ChatEntry(ChatEntryKind.Changes, string.Empty) { Files = Stats };
        Assert.Equal("3 files changed", entry.FilesChangedText);
        Assert.Equal("binary", entry.FileRows[1].AddedText);
        Assert.False(entry.FileRows[1].CanDiff);
        Assert.Equal("Deleted", entry.FileRows[2].StatusText);
        Assert.False(entry.CanUndo); // only prompts can be undone
    }

    [Fact]
    public void FilesSurviveSavingAndAreExported()
    {
        var saved = new SavedEntry { Kind = ChatEntryKind.Tool, Header = ChatEntry.Summarize(Stats), Activity = ActivityKind.Edit, Files = Stats };
        var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        SavedEntry back = JsonSerializer.Deserialize<SavedEntry>(JsonSerializer.Serialize(saved, options), options)!;
        Assert.Equal(Stats, back.Files);

        string md = ChatExporter.ToMarkdown(new SessionInfo { Title = "t", RepoPath = @"C:\r" }, [back]);
        Assert.Contains("_3 files changed · +12 −7_", md);
        Assert.Contains("- `src/a.cs` +12 −3", md);
        Assert.Contains("- `img.png` binary", md);
    }
}
