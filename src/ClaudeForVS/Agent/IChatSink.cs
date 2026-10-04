using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

internal enum Approval
{
    Deny,
    Allow,

    /// <summary>
    /// Allow this and every later request of the same kind: for edits, every edit in this conversation; for
    /// commands, that command in this repository from now on.
    /// </summary>
    AllowAll,
}

/// <summary>What a tool call did; the UI picks an icon and diff colouring from it.</summary>
internal enum ActivityKind
{
    Git,
    Read,

    /// <summary>Detail is a "- old" / "+ new" diff.</summary>
    Edit,

    /// <summary>Detail is the new file contents.</summary>
    Create,
    Declined,
    Failed,

    /// <summary>A shell command (Claude Code's Bash/PowerShell tools).</summary>
    Shell,

    /// <summary>A file or text search (Glob, Grep).</summary>
    Search,

    /// <summary>A web search or fetch.</summary>
    Web,

    /// <summary>Anything else, e.g. subagents, todo lists, MCP tools.</summary>
    Other,

    /// <summary>Claude asked the user multiple-choice questions.</summary>
    Question,
}

/// <summary>How the agent reports progress back to the UI. Called on the UI thread.</summary>
internal interface IChatSink
{
    /// <summary>Streams a chunk of Claude's visible answer.</summary>
    void AppendText(string text);

    /// <summary>Streams a chunk of Claude's summarized reasoning.</summary>
    void AppendThinking(string text);

    /// <summary>Shows a completed tool call and its (possibly truncated) output.</summary>
    /// <param name="change">For file edits, the whole file before and after, so it can be opened in a diff viewer.</param>
    void ToolActivity(ActivityKind kind, string title, string detail, FileChange? change = null);

    void SetStatus(string? status);

    /// <summary>Tokens used by one API response (Claude API engine).</summary>
    void ReportTokenUsage(ClaudeModel model, TokenUsage usage);

    /// <summary>Total tokens used answering one prompt (Claude Code engine, which doesn't report per response).</summary>
    void ReportPromptUsage(TokenUsage usage);

    /// <summary>Running total of tokens (in + out) for the prompt in progress, updated as the response streams. Estimated until each response completes.</summary>
    void ReportLiveTokens(long totalTokens);

    /// <summary>Tokens in the conversation's context after the latest response (everything sent, cached or not, plus the reply).</summary>
    void ReportContext(long tokens);

    /// <summary>The conversation was replaced by a summary to free up context.</summary>
    /// <param name="tokensBefore">Tokens in the context just before, if known (0 if not).</param>
    void ReportCompacted(long tokensBefore);

    /// <summary>Per-minute rate-limit headroom from the latest API response (Claude API engine).</summary>
    void ReportApiRateLimits(ApiRateLimits limits);

    /// <summary>A plan usage window reported by Claude Code (Claude Code engine).</summary>
    void ReportPlanLimit(PlanLimit limit);

    /// <summary>
    /// Shows Claude's multiple-choice questions. Returns each question's answer keyed by question text
    /// (selected labels joined with ", ", or the user's own text), or null if the user skipped them.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken);

    /// <summary>Asks the user to allow an action that changes the repository.</summary>
    /// <param name="kind">Git for git commands; Edit or Create for file changes (controls the preview's diff colouring).</param>
    /// <param name="title">One-line summary, e.g. the git command line or "Edit src/Foo.cs".</param>
    /// <param name="detail">Optional longer preview, e.g. a diff.</param>
    /// <param name="allowAllLabel">If not null, offers a third button that approves all similar requests.</param>
    /// <param name="change">For file edits, the whole file before and after, so it can be opened in a diff viewer.</param>
    Task<Approval> RequestApprovalAsync(ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change = null);
}
