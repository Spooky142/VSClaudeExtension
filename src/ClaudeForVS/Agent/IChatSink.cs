namespace ClaudeForVS.Agent;

/// <summary>How the agent reports progress back to the UI.</summary>
internal interface IChatSink
{
    /// <summary>Streams a chunk of Claude's visible answer.</summary>
    void AppendText(string text);

    /// <summary>Streams a chunk of Claude's summarized reasoning.</summary>
    void AppendThinking(string text);

    /// <summary>Shows a tool call and, once finished, its (possibly truncated) output.</summary>
    void ToolActivity(string title, string detail);

    void SetStatus(string? status);

    /// <summary>Asks the user to allow a command that can change the repository.</summary>
    Task<bool> RequestApprovalAsync(string description, CancellationToken cancellationToken);
}
