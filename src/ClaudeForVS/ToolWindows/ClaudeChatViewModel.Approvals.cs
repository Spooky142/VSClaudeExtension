using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Exceptions;
using ClaudeForVS.Agent;
using ClaudeForVS.Git;

namespace ClaudeForVS.ToolWindows;

/// <summary>What Claude asks the user while it works: approvals (with partial edits) and questions.</summary>
internal sealed partial class ClaudeChatViewModel
{
    private TaskCompletionSource<ApprovalResult>? pendingApproval;
    private ChangeReview? approvalReview;
    private TaskCompletionSource<IReadOnlyDictionary<string, string>?>? pendingQuestions;
    private string approvalTitle = string.Empty;
    private IReadOnlyList<DiffLine> approvalLines = [];
    private FileChange? approvalChange;
    private string? allowAllLabel;

    public string ApprovalTitle { get => this.approvalTitle; private set => this.Set(ref this.approvalTitle, value); }

    public IReadOnlyList<DiffLine> ApprovalLines
    {
        get => this.approvalLines;
        private set
        {
            this.Set(ref this.approvalLines, value);
            this.OnPropertyChanged(nameof(this.HasApprovalDetail));
        }
    }

    public bool HasApprovalDetail => this.approvalLines.Count > 0 && this.approvalReview is null;

    /// <summary>The pending edit split into blocks with checkboxes, when it has several; null otherwise.</summary>
    public ChangeReview? ApprovalReview
    {
        get => this.approvalReview;
        private set
        {
            this.Set(ref this.approvalReview, value);
            this.OnPropertyChanged(nameof(this.IsReviewingBlocks));
            this.OnPropertyChanged(nameof(this.HasApprovalDetail));
            this.OnReviewChanged();
        }
    }

    public bool IsReviewingBlocks => this.approvalReview != null;

    /// <summary>False while every block of the change is unticked, which would apply nothing.</summary>
    public bool CanApprove => this.approvalReview is null || this.approvalReview.KeptCount > 0;

    public string ReviewSummary => this.approvalReview?.Summary ?? string.Empty;

    private void OnReviewChanged()
    {
        this.OnPropertyChanged(nameof(this.CanApprove));
        this.OnPropertyChanged(nameof(this.ReviewSummary));
    }

    /// <summary>The whole-file change behind the pending approval, for the diff viewer; null for commands.</summary>
    public FileChange? ApprovalChange
    {
        get => this.approvalChange;
        private set
        {
            this.Set(ref this.approvalChange, value);
            this.OnPropertyChanged(nameof(this.HasApprovalChange));
        }
    }

    public bool HasApprovalChange => this.approvalChange != null;

    public string? AllowAllLabel
    {
        get => this.allowAllLabel;
        private set
        {
            this.Set(ref this.allowAllLabel, value);
            this.OnPropertyChanged(nameof(this.CanAllowAll));
        }
    }

    public bool CanAllowAll => this.allowAllLabel != null;

    /// <summary>Answers the approval card. When some blocks of an edit were unticked, only the ticked ones are approved.</summary>
    public void Resolve(Approval approval)
    {
        if (approval != Approval.Deny && !this.CanApprove)
        {
            return;
        }

        string? partial = approval == Approval.Deny ? null : this.approvalReview?.PartialAfter();
        this.pendingApproval?.TrySetResult(new ApprovalResult(approval, partial));
    }

    /// <summary>Questions Claude is waiting on, shown in the question card; empty when there are none.</summary>
    public ObservableCollection<QuestionViewModel> PendingQuestions { get; } = [];

    public bool HasPendingQuestions => this.PendingQuestions.Count > 0;

    public bool CanSubmitAnswers => this.PendingQuestions.Count > 0 && this.PendingQuestions.All(q => q.IsAnswered);

    public string QuestionCardTitle => this.PendingQuestions.Count == 1 ? "Claude has a question" : $"Claude has {this.PendingQuestions.Count} questions";

    public void SubmitAnswers()
    {
        if (this.CanSubmitAnswers)
        {
            this.pendingQuestions?.TrySetResult(this.PendingQuestions.ToDictionary(q => q.Question, q => q.Answer!));
        }
    }

    public void SkipQuestions() => this.pendingQuestions?.TrySetResult(null);

    async Task<IReadOnlyDictionary<string, string>?> IChatSink.AskQuestionsAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingQuestions = tcs;

        this.PendingQuestions.Clear();
        foreach (UserQuestion question in questions)
        {
            this.PendingQuestions.Add(new QuestionViewModel(question, this.OnAnswersChanged));
        }

        this.OnQuestionsChanged();
        this.AttentionNeeded?.Invoke(this, this.QuestionCardTitle);
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                this.pendingQuestions = null;
                this.PendingQuestions.Clear();
                this.OnQuestionsChanged();
            }
        }
    }

    private void OnAnswersChanged() => this.OnPropertyChanged(nameof(this.CanSubmitAnswers));

    private void OnQuestionsChanged()
    {
        this.OnPropertyChanged(nameof(this.HasPendingQuestions));
        this.OnPropertyChanged(nameof(this.QuestionCardTitle));
        this.OnPropertyChanged(nameof(this.CanSubmitAnswers));
    }

    async Task<ApprovalResult> IChatSink.RequestApprovalAsync(
        ActivityKind kind, string title, string detail, string? allowAllLabel, CancellationToken cancellationToken, FileChange? change, bool canKeepSome)
    {
        this.currentAssistant = null;
        this.currentThinking = null;
        var tcs = new TaskCompletionSource<ApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.pendingApproval = tcs;
        this.ApprovalTitle = title;
        this.ApprovalLines = DiffLine.Parse(Truncate(detail), kind);
        this.ApprovalChange = change;
        this.ApprovalReview = canKeepSome && change != null ? ChangeReview.For(change, this.OnReviewChanged) : null;
        this.AllowAllLabel = allowAllLabel;
        this.HasPendingApproval = true;
        this.AttentionNeeded?.Invoke(this, "Claude needs your approval");
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                return await tcs.Task;
            }
            finally
            {
                this.HasPendingApproval = false;
                this.ApprovalChange = null;
                this.ApprovalReview = null;
                this.pendingApproval = null;
            }
        }
    }
}
