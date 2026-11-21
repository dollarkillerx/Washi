// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Ai.Chat;
using Washi.Core.Localization;
using Washi.Tools;

namespace Washi.App.Services;

/// <summary>One pending (or answered) approval: shown as a card in the assistant chat (AI calls) or over the document (MCP).</summary>
public sealed partial class ApprovalViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ApprovalOutcome> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApprovalViewModel(ApprovalRequest request)
    {
        Request = request;
    }

    public ApprovalRequest Request { get; }
    public string Title => L.F("Ui_ApprovalTitle", Request.Actor.DisplayName);
    public string Summary => Request.Summary;
    public string? Details => Request.Details;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPending), nameof(OutcomeText))]
    private ApprovalOutcome? _outcome;

    public bool IsPending => Outcome is null;

    public string OutcomeText => Outcome switch
    {
        ApprovalOutcome.Approved => L.Get("Ui_ApprovalApproved"),
        ApprovalOutcome.Denied => L.Get("Ui_ApprovalDenied"),
        ApprovalOutcome.TimedOut => L.Get("Ui_ApprovalTimedOut"),
        ApprovalOutcome.Cancelled => L.Get("Ui_ApprovalCancelled"),
        _ => "",
    };

    internal Task<ApprovalOutcome> Answer => _answer.Task;

    [RelayCommand] private void Approve() => Resolve(ApprovalOutcome.Approved);
    [RelayCommand] private void Deny() => Resolve(ApprovalOutcome.Denied);

    internal void Resolve(ApprovalOutcome outcome)
    {
        if (!_answer.TrySetResult(outcome)) return;
        Dispatcher.UIThread.Post(() => Outcome = outcome);
    }
}

/// <summary>
/// The app's <see cref="IApprovalService"/>. Calls from the built-in assistant land as inline cards in its chat
/// (<see cref="AiRequested"/>); everything else (MCP clients) in <see cref="Pending"/>, shown over the document.
/// No modal windows: the user answers when they look, and an unanswered request times out (the invoker's deadline).
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    /// <summary>MCP requests waiting for an answer (UI thread).</summary>
    public ObservableCollection<ApprovalViewModel> Pending { get; } = [];

    /// <summary>Raised on the UI thread for an assistant call; return true when the chat shows the card.</summary>
    public Func<ApprovalViewModel, Guid, bool>? AiRequested { get; set; }

    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var vm = new ApprovalViewModel(request);
        var conversation = AiToolCallScope.Current?.ConversationId;
        await using var _ = ct.Register(() => vm.Resolve(ApprovalOutcome.Cancelled));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var inChat = request.Actor.Origin == ToolOrigin.Ai && conversation is { } id && AiRequested?.Invoke(vm, id) == true;
            if (!inChat) Pending.Add(vm);
        });
        var outcome = await vm.Answer.ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => Pending.Remove(vm));
        return outcome;
    }
}
