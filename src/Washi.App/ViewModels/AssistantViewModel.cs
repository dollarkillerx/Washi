// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Ai;
using Washi.Ai.Chat;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Tools;

namespace Washi.App.ViewModels;

/// <summary>One row in the assistant chat.</summary>
public abstract partial class ChatItem : ObservableObject;

public sealed class UserChatItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed partial class AssistantChatItem : ChatItem
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasThinking))] private string _thinking = "";
    public bool HasThinking => Thinking.Length > 0;

    /// <summary>Reasoning stays folded behind a small toggle (after MySSH); the answer is what the user reads.</summary>
    [ObservableProperty] private bool _isThinkingExpanded;

    [RelayCommand] private void ToggleThinking() => IsThinkingExpanded = !IsThinkingExpanded;
}

public sealed partial class ToolChatItem(string id, string name) : ChatItem
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    [ObservableProperty] private string _summary = name;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _running = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _failed;
    [ObservableProperty] private string _detail = "";
    public string StatusText => Running ? "…" : Failed ? "✕" : "✓";
}

public sealed class ApprovalChatItem(ApprovalViewModel approval) : ChatItem
{
    public ApprovalViewModel Approval { get; } = approval;
}

public sealed class NoticeChatItem(string text, bool isError) : ChatItem
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

/// <summary>
/// The built-in assistant: a chat that uses the same tools as the MCP server (validation, folder policy and approvals
/// in <see cref="IToolInvoker"/>). The open document is attached as context; nothing is sent before the user has
/// accepted the provider's data notice once.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppServices _services;
    private readonly List<AiMessage> _history = [];
    private Guid _conversation = Guid.NewGuid();
    private CancellationTokenSource? _cts;

    public AssistantViewModel(MainViewModel main, AppServices services)
    {
        _main = main;
        _services = services;
        services.Approvals.AiRequested = OnApprovalRequested;
        RefreshModel();
    }

    public ObservableCollection<ChatItem> Items { get; } = [];

    /// <summary>Builds the provider from the AI settings; tests replace it.</summary>
    public Func<(IChatProvider Provider, AiProviderConfig Config)> ProviderFactory { get; set; } = () =>
    {
        var settings = AiSettings.Load();
        return (settings.CreateChatProvider(AppServices.Instance.Http), settings.ToProviderConfig());
    };

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _input = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand)), NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _busy;
    [ObservableProperty] private bool _attachDocument = true;
    [ObservableProperty] private string _modelText = "";

    /// <summary>The data notice for an off-device provider not accepted yet (shown inline above the input).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _noticeText = "";
    public bool HasNotice => NoticeText.Length > 0;
    private string? _noticeKey;
    private string? _pendingQuestion;

    public bool CanEdit => !Busy;
    public bool HasItems => Items.Count > 0;

    public string DocumentChip => _main.PdfPath is { } p ? L.F("Ui_AssistantDocChip", Path.GetFileName(p), _main.PageIndex + 1) : "";

    internal void OnDocumentChanged() => OnPropertyChanged(nameof(DocumentChip));

    /// <summary>Re-reads the AI settings (after Settings saved them).</summary>
    public void RefreshModel()
    {
        try
        {
            var s = AiSettings.Load();
            ModelText = $"{s.Model} · {s.Provider}";
        }
        catch (Exception e) when (e is IOException or JsonException) { ModelText = ""; }
    }

    private bool CanSend() => !Busy && Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        (IChatProvider Provider, AiProviderConfig Config) p;
        try { p = ProviderFactory(); }
        catch (AiException e) { Items.Add(new NoticeChatItem(L.F("Ai_Error", UiErrors.Text(e)), true)); OnPropertyChanged(nameof(HasItems)); return; }

        // Once per provider: what is sent, and where. Local endpoints keep everything on this machine.
        var key = $"{p.Config.Kind}:{p.Config.BaseUrl}";
        var offDevice = !Uri.TryCreate(p.Config.BaseUrl, UriKind.Absolute, out var u) || !u.IsLoopback;
        if (offDevice && !AppSettings.Load(_services.SettingsPath).AiNoticeAccepted.Contains(key))
        {
            _noticeKey = key;
            _pendingQuestion = question;
            NoticeText = L.F("Ui_AssistantNotice", p.Config.Name, p.Config.BaseUrl);
            return;
        }
        Input = "";
        await RunTurnAsync(p.Provider, p.Config, question);
    }

    [RelayCommand]
    private async Task AcceptNoticeAsync()
    {
        if (_noticeKey is { } key)
            AppSettings.Update(s => s with { AiNoticeAccepted = [.. s.AiNoticeAccepted.Append(key).Distinct()] }, _services.SettingsPath);
        NoticeText = "";
        if (_pendingQuestion is { } q && !Busy)
        {
            _pendingQuestion = null;
            Input = "";
            (IChatProvider Provider, AiProviderConfig Config) p;
            try { p = ProviderFactory(); }
            catch (AiException e) { Items.Add(new NoticeChatItem(L.F("Ai_Error", UiErrors.Text(e)), true)); return; }
            await RunTurnAsync(p.Provider, p.Config, q);
        }
    }

    [RelayCommand] private void DeclineNotice() { NoticeText = ""; _pendingQuestion = null; }

    [RelayCommand(CanExecute = nameof(Busy))] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        _cts?.Cancel();
        _history.Clear();
        Items.Clear();
        _conversation = Guid.NewGuid();
        OnPropertyChanged(nameof(HasItems));
    }

    private async Task RunTurnAsync(IChatProvider provider, AiProviderConfig config, string question)
    {
        Items.Add(new UserChatItem(question));
        OnPropertyChanged(nameof(HasItems));
        var context = new List<AiContextItem>();
        if (AttachDocument && _main.PdfPath is { } pdf)
            context.Add(new AiContextItem(AiContextKind.Document, Path.GetFileName(pdf),
                AiContextComposer.DescribeDocument(pdf, _main.PageIndex + 1, _main.PageCount, _main.Fields.Count)));
        var folders = AppSettings.Load(_services.SettingsPath).AiFolders;
        if (folders.Count > 0)
            context.Add(new AiContextItem(AiContextKind.Folders, "folders", string.Join("\n", folders)));
        var composed = AiContextComposer.Compose(question, context, redact: true);

        var model = config.Model ?? "";
        var turn = new AgentTurn(_conversation, [.. _history], composed.Text, model, config.Effort,
            AiTools.From(_services.Invoker.Tools), AiAgent.Actor(config, model), MaxTokens: config.MaxOutputTokens ?? 32000);
        var agent = new AiAgent(provider, _services.Invoker);

        Busy = true;
        _cts = new CancellationTokenSource();
        AssistantChatItem? current = null;
        var tools = new Dictionary<string, ToolChatItem>();
        try
        {
            await foreach (var ev in agent.RunAsync(turn, _cts.Token))
            {
                switch (ev)
                {
                    case AgentHistoryAppended h:
                        _history.Add(h.Message);
                        break;
                    case AgentStreamEvent { Event: TextDelta t }:
                        if (current is null) { current = new AssistantChatItem(); Items.Add(current); }
                        current.Text += t.Text;
                        break;
                    case AgentStreamEvent { Event: ThinkingDelta t }:
                        if (current is null) { current = new AssistantChatItem(); Items.Add(current); }
                        current.Thinking += t.Text;
                        break;
                    case AgentStreamEvent { Event: ToolCallStarted s }:
                        current = null;
                        if (!tools.ContainsKey(s.Id)) Items.Add(tools[s.Id] = new ToolChatItem(s.Id, s.Name));
                        break;
                    case AgentToolStarted s:
                        current = null;
                        if (!tools.TryGetValue(s.Call.Id, out var item)) Items.Add(item = tools[s.Call.Id] = new ToolChatItem(s.Call.Id, s.Call.Name));
                        item.Summary = s.Call.Input is { } input ? ToolInvoker.Summarize(s.Call.Name, input) : s.Call.Name;
                        break;
                    case AgentToolFinished f when tools.TryGetValue(f.Call.Id, out var done):
                        done.Running = false;
                        done.Failed = f.Result.IsError;
                        done.Detail = f.Result.Content.Length > 400 ? f.Result.Content[..400] + "…" : f.Result.Content;
                        break;
                    case AgentRedacted r:
                        Items.Add(new NoticeChatItem(L.F("Ui_AssistantRedacted", r.Count), false));
                        break;
                    case AgentFinished fin:
                        foreach (var t in tools.Values.Where(t => t.Running)) t.Running = false;
                        if (fin.Stop != AgentStop.Completed) Items.Add(new NoticeChatItem(StopText(fin), fin.Stop is AgentStop.Error or AgentStop.Refusal));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Items.Add(new NoticeChatItem(L.Get("Ui_AssistantStopped"), false));
            // a stopped turn may leave tool calls without results: the next question repairs the history
        }
        finally
        {
            foreach (var t in tools.Values.Where(t => t.Running)) { t.Running = false; t.Failed = true; }
            _cts.Dispose();
            _cts = null;
            Busy = false;
            OnPropertyChanged(nameof(HasItems));
        }
    }

    private static string StopText(AgentFinished f) => f.Stop switch
    {
        AgentStop.MaxTokens => L.Get("Ui_AssistantMaxTokens"),
        AgentStop.Refusal => L.F("Ui_AssistantRefused", f.Detail ?? ""),
        AgentStop.StepLimit => L.Get("Ui_AssistantStepLimit"),
        _ => L.F("Ai_Error", UiErrors.Provider(f.ErrorKind, f.Detail)),
    };

    private bool OnApprovalRequested(ApprovalViewModel approval, Guid conversation)
    {
        if (conversation != _conversation) return false;
        Items.Add(new ApprovalChatItem(approval));
        return true;
    }
}
