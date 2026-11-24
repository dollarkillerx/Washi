// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.Ai;
using Washi.App.Services;
using Washi.Core.Localization;
using Washi.Tools;

namespace Washi.App.ViewModels;

/// <summary>An open-source component Washi uses, shown in About.</summary>
public sealed record Component(string Name, string License, string Url, string? Note = null);

/// <summary>A folder the AI and MCP tools may use, with its remove button.</summary>
public sealed class FolderRow(string path, IRelayCommand<string> remove)
{
    public string Path { get; } = path;
    public IRelayCommand<string> Remove { get; } = remove;
}

/// <summary>The Settings page (bottom of the left rail): general, AI model, AI &amp; MCP access, About.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string? _settingsPath;
    private readonly AppServices? _services;
    private readonly IDialogs? _dialogs;
    private bool _loading;

    public SettingsViewModel(string? settingsPath = null) : this(settingsPath, null, null) { }

    public SettingsViewModel(string? settingsPath, AppServices? services, IDialogs? dialogs)
    {
        _settingsPath = settingsPath;
        _services = services;
        _dialogs = dialogs;
        _loading = true;
        var app = AppSettings.Load(settingsPath);
        _language = Languages.FirstOrDefault(l => l.Value == (app.Language ?? "")) ?? Languages[0];
        LoadAi();
        _writes = WriteChoices.First(c => c.Value == app.AiWrites);
        _openDocumentFolder = app.AiOpenDocumentFolder;
        foreach (var f in app.AiFolders) Folders.Add(new FolderRow(f, RemoveFolderCommand));
        _checkForUpdates = app.CheckForUpdates;
        _loading = false;
        if (services is not null)
        {
            services.Mcp.Changed += () => Dispatcher.UIThread.Post(RefreshMcp);
            RefreshMcp();
        }
    }

    /// <summary>Raised after the language was saved; the app rebuilds its window in that language.</summary>
    public event Action? LanguageChanged;

    /// <summary>Raised after the AI model settings were saved (the assistant shows the new model).</summary>
    public event Action? AiChanged;

    /// <summary>Copies text to the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    // ------------------------------------------------------------------ general
    public static IReadOnlyList<Choice<string>> Languages =>
    [
        new("", L.Get("Ui_LangSystem")), new("ja", "日本語"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"),
        new("ko", "한국어"), new("en", "English"), new("fr", "Français"), new("de", "Deutsch"),
    ];

    [ObservableProperty] private Choice<string> _language;

    partial void OnLanguageChanged(Choice<string> value)
    {
        if (_loading) return;
        var settings = AppSettings.Update(s => s with { Language = value.Value.Length == 0 ? null : value.Value }, _settingsPath);
        AppSettings.ApplyLanguage(settings);
        LanguageChanged?.Invoke();
    }

    // ------------------------------------------------------------------ AI model
    public static IReadOnlyList<Choice<AiProvider>> Providers =>
    [
        new(AiProvider.Anthropic, "Claude (Anthropic)"), new(AiProvider.OpenAI, "OpenAI"),
        new(AiProvider.DeepSeek, "DeepSeek"), new(AiProvider.OpenAICompatible, L.Get("Ui_AiProviderLocal")),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyStatus), nameof(NeedsEndpoint), nameof(EndpointHint))]
    private Choice<AiProvider> _provider = null!;
    [ObservableProperty] private bool _loadingModels;
    [ObservableProperty] private string _modelsStatus = "";
    [ObservableProperty] private bool _modelsStatusIsError;

    /// <summary>Lists the provider's models (settings, key or null); tests replace it.</summary>
    public Func<AiSettings, string?, CancellationToken, Task<IReadOnlyList<string>>> ListModels { get; set; } = async (ai, key, ct) =>
    {
        var provider = Washi.Ai.Chat.AiProviders.Create(ai.ToProviderConfig(), key, AppServices.Instance.Http);
        var models = await provider.ListModelsAsync(ct);
        // Anthropic lists the newest first: keep that order; other providers come unordered
        return ai.Provider == AiProvider.Anthropic ? [.. models.Select(m => m.Id)] : [.. models.Select(m => m.Id).Order(StringComparer.Ordinal)];
    };

    /// <summary>Another provider: its saved model (or none yet), and its list is fetched when a key is at hand.</summary>
    partial void OnProviderChanged(Choice<AiProvider> value)
    {
        if (_loading) return;
        AiSettings? saved = null;
        try { saved = AiSettings.Load(); } catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        bool same = saved?.Provider == value.Value;
        Models.Clear();            // before the text: an editable list may reset its text when its items go
        Model = same ? saved!.Model : "";
        Endpoint = same ? saved!.Endpoint?.ToString() ?? "" : "";
        ApiKey = "";               // a key pasted for the other provider must not be tried (or saved) for this one
        ModelsStatus = "";
        SetAiStatus("", error: false);
        _ = LoadModelsAsync(quiet: true);
    }

    /// <summary>The "refresh" button next to the model: fetches the models the provider offers right now.</summary>
    [RelayCommand]
    private Task RefreshModelsAsync() => LoadModelsAsync(quiet: false);

    /// <summary>Called when the AI tab is shown: fills the list once, silently when there is no key yet.</summary>
    public Task LoadModelsOnceAsync() => Models.Count > 0 || LoadingModels ? Task.CompletedTask : LoadModelsAsync(quiet: true);

    private async Task LoadModelsAsync(bool quiet)
    {
        Uri? endpoint = null;
        if (Endpoint.Trim().Length > 0 && !Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out endpoint))
        {
            if (!quiet) { ModelsStatus = L.Get("Ui_AiBadEndpoint"); ModelsStatusIsError = true; }
            return;
        }
        var p = Provider.Value;
        string? key = ApiKey.Trim().Length > 0 ? ApiKey.Trim() : null;
        if (key is null)
        {
            try { key = ApiKeys.Find(p); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
        }
        if (key is null && p != AiProvider.OpenAICompatible)
        {
            ModelsStatus = quiet ? L.Get("Ui_AiModelsNeedKey") : L.Get("Ui_AiKeyMissing");
            ModelsStatusIsError = !quiet;
            return;
        }
        if (p == AiProvider.OpenAICompatible && endpoint is null)
        {
            if (!quiet) { ModelsStatus = L.Get("Ui_AiModelsNeedEndpoint"); ModelsStatusIsError = true; }
            return;
        }
        LoadingModels = true;
        ModelsStatusIsError = false;
        ModelsStatus = L.Get("Ui_AiModelsLoading");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ids = await ListModels(new AiSettings(p, Model.Trim().Length > 0 ? Model.Trim() : "-", endpoint), key, cts.Token);
            if (Provider.Value != p) return;   // switched provider meanwhile
            var current = Model;
            Models.Clear();
            foreach (var id in ids) Models.Add(id);
            Model = current;                   // the editable list keeps what was typed
            if (Model.Trim().Length == 0 && p == AiProvider.Anthropic && Models.Count > 0) Model = Models[0];
            ModelsStatus = ids.Count == 0 ? L.Get("Ui_AiModelsNone")
                : Model.Trim().Length > 0 && !ids.Contains(Model.Trim()) ? L.F("Ui_AiModelsNotListed", ids.Count, Model.Trim())
                : L.F("Ui_AiModelsLoaded", ids.Count);
            ModelsStatusIsError = false;
        }
        catch (Exception e) when (e is Washi.Ai.Chat.AiProviderException or AiException or HttpRequestException or OperationCanceledException)
        {
            ModelsStatus = L.F("Ai_Error", e is OperationCanceledException ? L.Get("Ui_AiModelsTimeout") : UiErrors.Text(e));
            ModelsStatusIsError = true;
        }
        finally { LoadingModels = false; }
    }
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private bool _vision = true;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private bool _aiStatusIsError;
    [ObservableProperty] private bool _testing;
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>The current model, as shown in the assistant and in General.</summary>
    public string AiModel => $"{Model} ({Provider.Value})";

    public bool NeedsEndpoint => Provider.Value == AiProvider.OpenAICompatible;
    public string EndpointHint => Provider.Value switch
    {
        AiProvider.OpenAICompatible => "http://localhost:11434/v1",
        AiProvider.DeepSeek => AiSettings.DeepSeekEndpoint.ToString(),
        AiProvider.OpenAI => "https://api.openai.com/v1",
        _ => "https://api.anthropic.com",
    };

    public string KeyStatus
    {
        get
        {
            var p = Provider.Value;
            if (ApiKeys.FromEnvironment(p)) return L.F("Ui_AiKeyFromEnv", AiSettings.KeyVariable(p));
            string? key = null;
            try { key = ApiKeys.Find(p); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            return key is { Length: > 0 } ? L.F("Ui_AiKeyStored", Mask(key)) : p == AiProvider.OpenAICompatible ? L.Get("Ui_AiKeyOptional") : L.Get("Ui_AiKeyMissing");
        }
    }

    private static string Mask(string key) => key.Length <= 8 ? "••••" : key[..4] + "…" + key[^4..];

    private void LoadAi()
    {
        AiSettings ai;
        try { ai = AiSettings.Load(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { ai = AiSettings.DefaultClaude; }
        var loading = _loading;
        _loading = true;
        Provider = Providers.First(p => p.Value == ai.Provider);
        _loading = loading;
        Model = ai.Model;
        Endpoint = ai.Endpoint?.ToString() ?? "";
        Vision = ai.Vision ?? true;
    }

    private AiSettings? BuildAi()
    {
        Uri? endpoint = null;
        if (Endpoint.Trim().Length > 0 && !Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out endpoint))
        {
            SetAiStatus(L.Get("Ui_AiBadEndpoint"), error: true);
            return null;
        }
        return new AiSettings(Provider.Value, Model.Trim(), endpoint, Vision ? null : false);
    }

    private void SetAiStatus(string text, bool error) { AiStatus = text; AiStatusIsError = error; }

    [RelayCommand]
    private void SaveAi()
    {
        if (BuildAi() is not { } ai) return;
        if (ai.Model.Length == 0) { SetAiStatus(L.Get("Err_AiNoModel"), error: true); return; }
        try
        {
            if (ApiKey.Trim().Length > 0)
            {
                ApiKeys.Store(ai.Provider, ApiKey.Trim());
                ApiKey = "";
            }
            ai.Save();
            OnPropertyChanged(nameof(KeyStatus));
            OnPropertyChanged(nameof(AiModel));
            SetAiStatus(L.Get("Ui_AiSaved"), error: false);
            AiChanged?.Invoke();
        }
        catch (Exception e) when (e is AiException or IOException or UnauthorizedAccessException)
        {
            SetAiStatus(L.F("Ai_Error", UiErrors.Text(e)), error: true);
        }
    }

    [RelayCommand]
    private void RemoveKey()
    {
        ApiKeys.Remove(Provider.Value);
        OnPropertyChanged(nameof(KeyStatus));
    }

    /// <summary>"Test connection": lists the models, then one tiny request with one tool.</summary>
    [RelayCommand]
    private async Task TestAiAsync()
    {
        if (BuildAi() is not { } ai) return;
        if (ApiKey.Trim().Length > 0) SaveAi();
        Testing = true;
        SetAiStatus(L.Get("Ui_AiTesting"), error: false);
        try
        {
            var provider = ai.CreateChatProvider(_services?.Http ?? AppServices.Instance.Http);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await provider.ProbeAsync(ai.Model, cts.Token);
            if (result.Models.Count > 0)
            {
                var current = Model;
                Models.Clear();
                foreach (var m in result.Models.Select(m => m.Id)) Models.Add(m);
                Model = current;
            }
            if (result.Ok)
                SetAiStatus(L.F("Ui_AiTestOk", ai.Model, result.Models.Count,
                    result.Detected.Tools ? L.Get("Ui_Yes") : L.Get("Ui_No")), error: false);
            else SetAiStatus(L.F("Ai_Error", result.Error ?? "?"), error: true);
        }
        catch (Exception e) when (e is AiException or OperationCanceledException or HttpRequestException)
        {
            SetAiStatus(L.F("Ai_Error", UiErrors.Text(e)), error: true);
        }
        finally { Testing = false; }
    }

    // ------------------------------------------------------------------ AI & MCP access
    public static IReadOnlyList<Choice<WriteAccess>> WriteChoices =>
    [
        new(WriteAccess.Ask, L.Get("Ui_WritesAsk")), new(WriteAccess.Allow, L.Get("Ui_WritesAllow")), new(WriteAccess.Deny, L.Get("Ui_WritesDeny")),
    ];

    public ObservableCollection<FolderRow> Folders { get; } = [];
    [ObservableProperty] private Choice<WriteAccess> _writes;
    [ObservableProperty] private bool _openDocumentFolder;

    partial void OnWritesChanged(Choice<WriteAccess> value)
    {
        if (!_loading) AppSettings.Update(s => s with { AiWrites = value.Value }, _settingsPath);
    }

    partial void OnOpenDocumentFolderChanged(bool value)
    {
        if (!_loading) AppSettings.Update(s => s with { AiOpenDocumentFolder = value }, _settingsPath);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_dialogs is null || await _dialogs.PickFolderAsync() is not { } folder) return;
        AddFolder(folder);
    }

    public void AddFolder(string folder)
    {
        if (Folders.Any(f => f.Path == folder)) return;
        Folders.Add(new FolderRow(folder, RemoveFolderCommand));
        SaveFolders();
    }

    [RelayCommand]
    private void RemoveFolder(string? path)
    {
        if (Folders.FirstOrDefault(f => f.Path == path) is { } row) Folders.Remove(row);
        SaveFolders();
    }

    private void SaveFolders() => AppSettings.Update(s => s with { AiFolders = [.. Folders.Select(f => f.Path)] }, _settingsPath);

    // ------------------------------------------------------------------ MCP server
    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private string _mcpStatus = "";
    [ObservableProperty] private bool _mcpStatusIsError;
    [ObservableProperty] private string _mcpToken = "";
    [ObservableProperty] private bool _showToken;
    [ObservableProperty] private string _claudeCodeCommand = "";
    [ObservableProperty] private string _claudeDesktopJson = "";
    [ObservableProperty] private string _httpJson = "";
    public ObservableCollection<McpClientRow> McpClients { get; } = [];
    public bool HasMcpClients => McpClients.Count > 0;
    public bool McpRunning => _services?.Mcp.IsRunning == true;
    public bool HasBridge => ClaudeDesktopJson.Length > 0;
    public string TokenText => ShowToken ? McpToken : McpToken.Length > 0 ? new string('•', 24) : "";

    partial void OnShowTokenChanged(bool value) => OnPropertyChanged(nameof(TokenText));

    private bool _mcpLoading;

    partial void OnMcpEnabledChanged(bool value)
    {
        if (_mcpLoading || _services is null) return;
        McpStatus = value ? L.Get("Mcp_Starting") : "";
        _ = _services.Mcp.SetEnabledAsync(value).ContinueWith(_ => Dispatcher.UIThread.Post(RefreshMcp), TaskScheduler.Default);
    }

    private void RefreshMcp()
    {
        if (_services?.Mcp is not { } mcp) return;
        _mcpLoading = true;
        McpEnabled = mcp.IsEnabled;
        _mcpLoading = false;
        McpStatusIsError = mcp.StartError is not null;
        McpStatus = mcp.StartError ?? (mcp.IsRunning
            ? L.F("Mcp_Running", mcp.HttpEndpoint?.ToString() ?? "-", mcp.LocalSocketPath ?? "-")
            : mcp.IsEnabled ? L.Get("Mcp_Starting") : L.Get("Mcp_Off"));
        McpToken = mcp.Token ?? "";
        OnPropertyChanged(nameof(TokenText));
        ClaudeCodeCommand = mcp.ClaudeCodeCommand;
        ClaudeDesktopJson = mcp.ClaudeDesktopJson;
        HttpJson = mcp.GenericHttpJson;
        OnPropertyChanged(nameof(McpRunning));
        OnPropertyChanged(nameof(HasBridge));
        McpClients.Clear();
        foreach (var c in mcp.Clients) McpClients.Add(c);
        OnPropertyChanged(nameof(HasMcpClients));
    }

    [RelayCommand]
    private async Task RegenerateTokenAsync()
    {
        if (_services is null) return;
        await _services.Mcp.RegenerateTokenAsync();
        RefreshMcp();
    }

    [RelayCommand]
    private async Task DisconnectAsync(string? id)
    {
        if (_services is not null && id is not null) await _services.Mcp.DisconnectAsync(id);
    }

    [RelayCommand]
    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || CopyToClipboard is null) return;
        await CopyToClipboard(text);
        McpStatusIsError = false;
        McpStatus = L.Get("Ui_Copied");
    }

    // ------------------------------------------------------------------ About
    /// <summary>Updates from the GitHub releases (a do-nothing one without the app's services).</summary>
    public UpdatesViewModel Updates => _services?.Updates ?? s_noUpdates;
    private static readonly UpdatesViewModel s_noUpdates = new(NullAppUpdater.Instance, () => { });

    [ObservableProperty] private bool _checkForUpdates = true;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_loading) AppSettings.Update(s => s with { CheckForUpdates = value }, _settingsPath);
    }

    public string Version => L.F("Ui_Version",
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public const string SourceUrl = "https://github.com/dollarkillerx/Washi";
    public const string LicenseUrl = "https://github.com/dollarkillerx/Washi/blob/main/LICENSE";

    /// <summary>What ships in Washi, with each project's own licence (see THIRD_PARTY_NOTICES.md).</summary>
    public static IReadOnlyList<Component> Components =>
    [
        new(".NET", "MIT", "https://github.com/dotnet/runtime"),
        new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("PDFium", "BSD-3-Clause, Apache-2.0", "https://pdfium.googlesource.com/pdfium/"),
        new("pdfium-binaries", "Apache-2.0", "https://github.com/bblanchon/pdfium-binaries"),
        new("PDFiumCore", "Apache-2.0", "https://github.com/Dtronix/PDFiumCore"),
        new("PDFsharp 6.2", "MIT", "https://github.com/empira/PDFsharp"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Anthropic C# SDK", "Apache-2.0", "https://github.com/anthropics/anthropic-sdk-csharp"),
        new("OpenAI .NET SDK", "MIT", "https://github.com/openai/openai-dotnet"),
        new("MCP C# SDK", "Apache-2.0", "https://github.com/modelcontextprotocol/csharp-sdk"),
        new("Inter", "SIL OFL 1.1", "https://github.com/rsms/inter"),
        new("Tesseract", "Apache-2.0", "https://github.com/tesseract-ocr/tesseract", L.Get("Ui_NotBundled")),
    ];
}
