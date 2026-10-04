using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeForVS.Agent;

/// <summary>One conversation with Claude about one repository, whichever backend runs it.</summary>
internal interface IChatEngine : IDisposable
{
    ChatEngineKind Kind { get; }

    string RepoRoot { get; }

    /// <summary>Fixed for the conversation; switching model starts a new one.</summary>
    ClaudeModel Model { get; }

    /// <summary>Can change between turns; takes effect on the next message.</summary>
    EffortLevel Effort { get; set; }

    /// <summary>Can change between turns; takes effect on the next message.</summary>
    AgentMode Mode { get; set; }

    /// <summary>
    /// Marks the conversation as it stands now, before the next message. Passing it to <see cref="Rewind"/> later
    /// drops that message and everything after it. Null when the engine can't tell where it is.
    /// </summary>
    string? Checkpoint { get; }

    /// <summary>Sends one user message and streams the reply (including tool calls) into <paramref name="sink"/>.</summary>
    Task SendAsync(ChatPrompt prompt, IChatSink sink, CancellationToken cancellationToken);

    /// <summary>Summarizes the conversation now to free up context.</summary>
    Task CompactAsync(IChatSink sink, CancellationToken cancellationToken);

    /// <summary>Goes back to a <see cref="Checkpoint"/>. Returns false if that point can no longer be reached (e.g. after compacting).</summary>
    bool Rewind(string checkpoint);
}
