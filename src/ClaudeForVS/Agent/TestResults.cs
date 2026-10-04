using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ClaudeForVS.Agent;

/// <summary>A test that didn't pass.</summary>
/// <param name="Outcome">"Failed", "Error", "Timeout", "Aborted"…</param>
internal sealed record TestFailure(string Name, string Outcome, string Message, string StackTrace);

/// <summary>What a test run came to.</summary>
internal sealed record TestRunSummary(int Total, int Passed, int Failed, int Skipped, TimeSpan? Duration, IReadOnlyList<TestFailure> Failures);

/// <summary>Reads Visual Studio test result (TRX) files and formats them for Claude: the counts, then each failure.</summary>
internal static class TestResults
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private const int MaxFailuresShown = 30;
    private const int MaxMessageChars = 1_500;
    private const int MaxStackFrames = 8;

    /// <summary>Parses a TRX file's text, or returns null if it isn't one.</summary>
    public static TestRunSummary? Parse(string trx)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(trx);
        }
        catch (XmlException)
        {
            return null;
        }

        if (doc.Root is not { Name.LocalName: "TestRun" } run)
        {
            return null;
        }

        var failures = new List<TestFailure>();
        int passed = 0;
        int skipped = 0;
        int total = 0;
        foreach (XElement result in run.Descendants(Ns + "UnitTestResult"))
        {
            total++;
            string outcome = (string?)result.Attribute("outcome") ?? string.Empty;
            switch (outcome)
            {
                case "Passed":
                    passed++;
                    break;
                case "NotExecuted" or "Inconclusive" or "Pending" or "NotRunnable":
                    skipped++;
                    break;
                default:
                    XElement? error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
                    failures.Add(new TestFailure(
                        (string?)result.Attribute("testName") ?? "(unnamed test)",
                        outcome.Length > 0 ? outcome : "Failed",
                        ((string?)error?.Element(Ns + "Message") ?? string.Empty).Trim(),
                        ((string?)error?.Element(Ns + "StackTrace") ?? string.Empty).Trim()));
                    break;
            }
        }

        TimeSpan? duration = null;
        XElement? times = run.Element(Ns + "Times");
        if (times != null
            && DateTimeOffset.TryParse((string?)times.Attribute("start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset start)
            && DateTimeOffset.TryParse((string?)times.Attribute("finish"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset finish)
            && finish >= start)
        {
            duration = finish - start;
        }

        return new TestRunSummary(total, passed, failures.Count, skipped, duration, failures);
    }

    /// <summary>"248 passed, 2 failed" followed by each failure's name, message and the top of its stack trace.</summary>
    public static string Format(TestRunSummary summary, string repoRoot)
    {
        var sb = new StringBuilder();
        var parts = new List<string> { $"{summary.Passed} passed", $"{summary.Failed} failed" };
        if (summary.Skipped > 0)
        {
            parts.Add($"{summary.Skipped} skipped");
        }

        sb.Append("Tests: ").Append(string.Join(", ", parts));
        if (summary.Duration is TimeSpan duration)
        {
            sb.Append($" ({(duration.TotalSeconds < 10 ? duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) : ((int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture))}s)");
        }

        if (summary.Total == 0)
        {
            sb.Append("\nNo tests ran. Check the target and filter.");
        }

        foreach (TestFailure failure in summary.Failures.Take(MaxFailuresShown))
        {
            sb.Append("\n\n").Append(failure.Outcome.ToUpperInvariant()).Append(' ').Append(failure.Name);
            if (failure.Message.Length > 0)
            {
                string message = failure.Message.Length > MaxMessageChars ? failure.Message.Substring(0, MaxMessageChars) + " […]" : failure.Message;
                sb.Append('\n').Append(Indent(message));
            }

            string stack = RelativeStack(failure.StackTrace, repoRoot);
            if (stack.Length > 0)
            {
                sb.Append('\n').Append(Indent(stack));
            }
        }

        if (summary.Failures.Count > MaxFailuresShown)
        {
            sb.Append($"\n\n[{summary.Failures.Count - MaxFailuresShown} more failures not shown; narrow the run with a filter.]");
        }

        return sb.ToString();
    }

    /// <summary>The first few stack frames, with paths inside the repository made relative ("in src/A.cs:line 12" becomes "in src/A.cs:12").</summary>
    internal static string RelativeStack(string stack, string repoRoot)
    {
        if (stack.Length == 0)
        {
            return string.Empty;
        }

        string root = repoRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        IEnumerable<string> frames = stack.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(MaxStackFrames)
            .Select(line =>
            {
                int at = line.IndexOf(root, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    line = line.Remove(at, root.Length).Replace('\\', '/');
                }

                return Regex.Replace(line, @":line (\d+)", ":$1");
            });
        return string.Join("\n", frames);
    }

    private static string Indent(string text) => "  " + text.Replace("\r\n", "\n").Replace("\n", "\n  ");

    /// <summary>Quotes a value for a PowerShell command line.</summary>
    internal static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
