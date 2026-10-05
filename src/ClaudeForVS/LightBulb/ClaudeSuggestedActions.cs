using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeForVS.Agent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace ClaudeForVS.LightBulb;

/// <summary>What a Claude light bulb action asks for.</summary>
internal enum LightBulbAction
{
    Fix,
    Explain,
    WriteTests,
    Document,
}

/// <summary>What the caret is on, worked out when the light bulb opens and handed to the action the user picks.</summary>
/// <param name="StartLine">First line of the code the action is about (1-based): the selection, or the type or member the caret is in.</param>
/// <param name="CaretStartLine">The lines the light bulb was opened on, which is where a problem to fix is looked for.</param>
internal sealed record LightBulbContext(string FilePath, int StartLine, int EndLine, string Text, int CaretStartLine, int CaretEndLine);

/// <summary>What the light bulb offers at the caret.</summary>
/// <param name="HasProblem">The line has an error or warning squiggle.</param>
/// <param name="HasCode">There is a selection, or the caret is in a type or member, for Explain and tests to be about.</param>
/// <param name="CanDocument">The caret is in a member that has no documentation comment.</param>
internal sealed record LightBulbOffer(LightBulbContext Context, bool HasProblem, bool HasCode, bool CanDocument);

/// <summary>
/// Adds Claude to the light bulb menu (Ctrl+.): Fix with Claude on a line with an error or warning, and Explain,
/// Write tests and Document on the type or member the caret is in (or the selection).
/// </summary>
[Export(typeof(ISuggestedActionsSourceProvider))]
[Name("Claude suggested actions")]
[ContentType("code")]
internal sealed class ClaudeSuggestedActionsSourceProvider : ISuggestedActionsSourceProvider
{
    [Import]
    internal ITextDocumentFactoryService TextDocuments { get; set; } = null!;

    [Import]
    internal IBufferTagAggregatorFactoryService TagAggregators { get; set; } = null!;

    public ISuggestedActionsSource? CreateSuggestedActionsSource(ITextView textView, ITextBuffer textBuffer) =>
        textView.TextBuffer == textBuffer ? new ClaudeSuggestedActionsSource(this, textBuffer) : null;
}

internal sealed class ClaudeSuggestedActionsSource : ISuggestedActionsSource
{
    private static readonly string[] SquiggleErrorTypes =
    [
        PredefinedErrorTypeNames.SyntaxError,
        PredefinedErrorTypeNames.CompilerError,
        PredefinedErrorTypeNames.OtherError,
        PredefinedErrorTypeNames.Warning,
    ];

    private readonly ClaudeSuggestedActionsSourceProvider provider;
    private readonly ITextBuffer buffer;
    private readonly ITagAggregator<IErrorTag> errorTags;

    public ClaudeSuggestedActionsSource(ClaudeSuggestedActionsSourceProvider provider, ITextBuffer buffer)
    {
        this.provider = provider;
        this.buffer = buffer;
        this.errorTags = provider.TagAggregators.CreateTagAggregator<IErrorTag>(buffer);
    }

#pragma warning disable CS0067 // Part of the interface; the actions available don't change on their own.
    public event EventHandler<EventArgs>? SuggestedActionsChanged;
#pragma warning restore CS0067

    public Task<bool> HasSuggestedActionsAsync(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken) =>
        Task.Run(() => this.Collect(range, cancellationToken) is not null, cancellationToken);

    public IEnumerable<SuggestedActionSet> GetSuggestedActions(ISuggestedActionCategorySet requestedActionCategories, SnapshotSpan range, CancellationToken cancellationToken)
    {
        if (this.Collect(range, cancellationToken) is not LightBulbOffer offer)
        {
            return [];
        }

        LightBulbContext context = offer.Context;
        var sets = new List<SuggestedActionSet>();
        if (offer.HasProblem && requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.ErrorFix))
        {
            sets.Add(new SuggestedActionSet(
                PredefinedSuggestedActionCategoryNames.ErrorFix,
                [new ClaudeSuggestedAction(LightBulbAction.Fix, "Fix with Claude", context)],
                priority: SuggestedActionSetPriority.Low));
        }

        if (offer.HasCode && requestedActionCategories.Contains(PredefinedSuggestedActionCategoryNames.Refactoring))
        {
            var actions = new List<ISuggestedAction>
            {
                new ClaudeSuggestedAction(LightBulbAction.Explain, "Explain with Claude", context),
                new ClaudeSuggestedAction(LightBulbAction.WriteTests, "Write tests with Claude", context),
            };
            if (offer.CanDocument)
            {
                actions.Add(new ClaudeSuggestedAction(LightBulbAction.Document, "Document with Claude", context));
            }

            sets.Add(new SuggestedActionSet(PredefinedSuggestedActionCategoryNames.Refactoring, actions, priority: SuggestedActionSetPriority.Low));
        }

        return sets;
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = Guid.Empty;
        return false;
    }

    public void Dispose() => this.errorTags.Dispose();

    /// <summary>
    /// What to offer for <paramref name="range"/>: null if there's nothing (no file, or no selection, no member the caret
    /// is in and no problem on the line).
    /// </summary>
    private LightBulbOffer? Collect(SnapshotSpan range, CancellationToken cancellationToken)
    {
        if (!this.provider.TextDocuments.TryGetTextDocument(this.buffer, out ITextDocument document) || string.IsNullOrEmpty(document.FilePath))
        {
            return null;
        }

        ITextSnapshot snapshot = range.Snapshot;
        ITextSnapshotLine firstLine = snapshot.GetLineFromPosition(range.Start);
        ITextSnapshotLine lastLine = snapshot.GetLineFromPosition(range.End);
        int caretStart = firstLine.LineNumber + 1;
        int caretEnd = lastLine.LineNumber + 1;
        bool hasProblem = this.errorTags.GetTags(new SnapshotSpan(snapshot, Span.FromBounds(firstLine.Start, lastLine.End)))
            .Any(t => SquiggleErrorTypes.Contains(t.Tag.ErrorType));

        // A selection is what the user means; otherwise the member the caret is in.
        if (!range.IsEmpty && range.GetText().Trim().Length > 0)
        {
            return new LightBulbOffer(new LightBulbContext(document.FilePath, caretStart, caretEnd, range.GetText(), caretStart, caretEnd), hasProblem, HasCode: true, CanDocument: false);
        }

        if (FindMember(snapshot, range.Start, cancellationToken) is EnclosingMember member)
        {
            return new LightBulbOffer(
                new LightBulbContext(document.FilePath, member.StartLine, member.EndLine, member.Text, caretStart, caretEnd), hasProblem, HasCode: true, CanDocument: !member.HasDocumentation);
        }

        return hasProblem
            ? new LightBulbOffer(new LightBulbContext(document.FilePath, caretStart, caretEnd, string.Empty, caretStart, caretEnd), HasProblem: true, HasCode: false, CanDocument: false)
            : null;
    }
    /// <summary>The type or member containing the caret, from Roslyn; null for languages it doesn't cover or if it isn't available.</summary>
    private static EnclosingMember? FindMember(ITextSnapshot snapshot, int position, CancellationToken cancellationToken)
    {
        try
        {
            Document? roslynDocument = snapshot.GetOpenDocumentInCurrentContextWithChanges();
            return roslynDocument is null
                ? null
                : ThreadHelper.JoinableTaskFactory.Run(() => RoslynCodeIntelligence.FindEnclosingMemberAsync(roslynDocument, position, cancellationToken));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or MissingMemberException or TypeLoadException or OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>One Claude entry in the light bulb menu.</summary>
internal sealed class ClaudeSuggestedAction(LightBulbAction action, string text, LightBulbContext context) : ISuggestedAction
{
    public string DisplayText => text;

    public ImageMoniker IconMoniker => KnownMonikers.Comment;

    public string IconAutomationText => text;

    public string InputGestureText => string.Empty;

    public bool HasActionSets => false;

    public bool HasPreview => false;

    public Task<IEnumerable<SuggestedActionSet>?> GetActionSetsAsync(CancellationToken cancellationToken) => Task.FromResult<IEnumerable<SuggestedActionSet>?>(null);

    public Task<object?> GetPreviewAsync(CancellationToken cancellationToken) => Task.FromResult<object?>(null);

    public void Invoke(CancellationToken cancellationToken) => this.InvokeAsync().FileAndForget("ClaudeForVS/LightBulb");

    private async Task InvokeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await ClaudePackage.LoadAsync() is ClaudePackage package)
        {
            package.RunLightBulbAction(action, context);
        }
    }

    public bool TryGetTelemetryId(out Guid telemetryId)
    {
        telemetryId = Guid.Empty;
        return false;
    }

    public void Dispose()
    {
    }
}


