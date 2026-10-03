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

    /// <summary>Sends one user message and streams the reply (including tool calls) into <paramref name="sink"/>.</summary>
    Task SendAsync(string prompt, IChatSink sink, CancellationToken cancellationToken);
}
