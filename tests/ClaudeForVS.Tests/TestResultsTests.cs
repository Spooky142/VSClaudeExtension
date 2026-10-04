using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Xunit;

namespace ClaudeForVS.Tests;

public sealed class TestResultsTests : IDisposable
{
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="1" name="x" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2026-10-04T22:16:19+01:00" queuing="2026-10-04T22:16:19+01:00" start="2026-10-04T22:16:18.5+01:00" finish="2026-10-04T22:16:23.7+01:00" />
          <Results>
            <UnitTestResult testName="Demo.Tests.Adds" outcome="Passed" />
            <UnitTestResult testName="Demo.Tests.Subtracts" outcome="Passed" />
            <UnitTestResult testName="Demo.Tests.Divides" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure: Values differ
        Expected: 2
        Actual:   3</Message>
                  <StackTrace>   at Demo.Tests.Divides() in C:\work\repo\tests\MathTests.cs:line 42
           at System.RuntimeMethodHandle.InvokeMethod()</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="Demo.Tests.Slow" outcome="NotExecuted" />
          </Results>
          <ResultSummary outcome="Failed" />
        </TestRun>
        """;

    private readonly string root = Path.Combine(Path.GetTempPath(), "ClaudeForVS.Tests", Guid.NewGuid().ToString("N"));

    public TestResultsTests() => Directory.CreateDirectory(this.root);

    public void Dispose() => Directory.Delete(this.root, recursive: true);

    [Fact]
    public void ParsesCountsAndFailures()
    {
        TestRunSummary summary = TestResults.Parse(Trx)!;

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(TimeSpan.FromSeconds(5.2), summary.Duration);
        TestFailure failure = Assert.Single(summary.Failures);
        Assert.Equal("Demo.Tests.Divides", failure.Name);
        Assert.Equal("Failed", failure.Outcome);
        Assert.StartsWith("Assert.Equal() Failure", failure.Message);
    }

    [Fact]
    public void FormatsFailuresWithRelativePaths()
    {
        string text = TestResults.Format(TestResults.Parse(Trx)!, @"C:\work\repo");

        Assert.StartsWith("Tests: 2 passed, 1 failed, 1 skipped (5.2s)", text);
        Assert.Contains("FAILED Demo.Tests.Divides", text);
        Assert.Contains("Expected: 2", text);
        Assert.Contains("at Demo.Tests.Divides() in tests/MathTests.cs:42", text);
        Assert.DoesNotContain(@"C:\work", text);
        Assert.DoesNotContain("Demo.Tests.Adds", text); // passing tests aren't listed
    }

    [Fact]
    public void LimitsHowManyFailuresAreShown()
    {
        string results = string.Concat(Enumerable.Range(1, 40).Select(i => $"""<UnitTestResult testName="T{i}" outcome="Failed" />"""));
        string trx = $"""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results></TestRun>""";

        string text = TestResults.Format(TestResults.Parse(trx)!, this.root);

        Assert.Contains("FAILED T30", text);
        Assert.DoesNotContain("FAILED T31", text);
        Assert.Contains("10 more failures not shown", text);
    }

    [Fact]
    public void ReportsAnEmptyRun()
    {
        string trx = """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /></TestRun>""";
        Assert.Contains("No tests ran", TestResults.Format(TestResults.Parse(trx)!, this.root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Other />")]
    public void IgnoresWhatIsntATrxFile(string text) => Assert.Null(TestResults.Parse(text));

    [Fact]
    public void QuotesPowerShellValues() => Assert.Equal("'it''s'", TestResults.PsQuote("it's"));

    // ===== The tool, against the fake API =====

    private async Task<(string Result, RecordingSink Sink)> RunToolAsync(object input, AgentMode mode, Func<string, Approval>? decide = null)
    {
        using var api = new FakeAnthropic();
        api.Reply(Sse.Turn(Sse.ToolUse("toolu_1", "run_tests", input), stopReason: "tool_use")).Reply(Sse.Turn(Sse.Text("ok")));
        var sink = new RecordingSink();
        if (decide != null)
        {
            sink.Decide = decide;
        }

        using var agent = new ClaudeAgent("test-key", this.root, ClaudeModel.Opus, EffortLevel.Low, mode, api.Url);
        await agent.SendAsync(new ChatPrompt("Run the tests"), sink, CancellationToken.None);

        var messages = api.Requests[1].RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        var result = last.GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_result");
        var content = result.GetProperty("content");
        return (content.ValueKind == System.Text.Json.JsonValueKind.String ? content.GetString()! : content.ToString(), sink);
    }

    [Fact]
    public async Task PlanModeRefusesToRunTests()
    {
        (string result, RecordingSink sink) = await this.RunToolAsync(new { }, AgentMode.Plan);

        Assert.Contains("Plan mode", result);
        Assert.Empty(sink.Approvals);
    }

    [Fact]
    public async Task ADeclinedTestRunDoesntRun()
    {
        (string result, RecordingSink sink) = await this.RunToolAsync(new { }, AgentMode.Coding, _ => Approval.Deny);

        Assert.Contains("declined", result);
        Assert.Single(sink.Approvals);
        Assert.StartsWith("Run tests", sink.Approvals[0]);
    }

    [Fact]
    public async Task ATargetThatDoesntExistIsAnError()
    {
        (string result, _) = await this.RunToolAsync(new { target = "nope\\Missing.csproj" }, AgentMode.Auto);
        Assert.Contains("Target not found", result);
    }

    [Fact]
    public async Task ATargetOfTheWrongKindIsAnError()
    {
        File.WriteAllText(Path.Combine(this.root, "notes.txt"), "hi");
        (string result, _) = await this.RunToolAsync(new { target = "notes.txt" }, AgentMode.Auto);
        Assert.Contains("must be a solution", result);
    }

    [Fact]
    public async Task ARunWithNoResultsFileReportsTheOutput()
    {
        // An empty folder: dotnet test finds nothing to run, so no TRX is written.
        (string result, RecordingSink sink) = await this.RunToolAsync(new { }, AgentMode.Auto);

        Assert.Contains("No test results were produced", result);
        Assert.Contains(sink.Activities, a => a.Kind == ActivityKind.Failed);
    }
}
