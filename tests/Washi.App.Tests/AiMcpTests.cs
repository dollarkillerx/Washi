// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Washi.Ai.Chat;
using Washi.App.Services;
using Washi.App.ViewModels;
using Washi.McpHost;
using Washi.Tools;
using Tool = Washi.App.ViewModels.Tool;

namespace Washi.App.Tests;

/// <summary>A chat provider that plays back one scripted turn per request and records what it was sent.</summary>
internal sealed class ScriptedChat(params Func<ChatRequest, AiMessage>[] turns) : IChatProvider
{
    private int _next;
    public List<ChatRequest> Requests { get; } = [];
    public string ProviderId => "test";
    public ProviderCapabilities Capabilities { get; } = new(Images: true);
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("scripted")]);
    public Task<ProviderProbeResult> ProbeAsync(string model, CancellationToken ct) =>
        Task.FromResult(new ProviderProbeResult(true, [new("scripted")], Capabilities));

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        await Task.Yield();
        var message = turns[Math.Min(_next++, turns.Length - 1)](request) with { RawProvider = ProviderId };
        if (message.Text is { Length: > 0 } text) yield return new TextDelta(text);
        foreach (var c in message.ToolCalls) yield return new ToolCallStarted(c.Id, c.Name);
        yield return new Stopped(message.ToolCalls.Count > 0 ? StopReason.ToolUse : StopReason.EndTurn, null, message);
    }

    public static AiMessage Call(string id, string tool, string json) => new()
    {
        Role = AiRole.Assistant,
        ToolCalls = [new ToolCall(id, tool, JsonDocument.Parse(json).RootElement.Clone(), json)],
    };

    public static AiMessage Say(string text) => new() { Role = AiRole.Assistant, Text = text };
}

public sealed class AiMcpTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    // short: Unix socket paths are limited to ~104 bytes
    private readonly string _dir = Directory.CreateTempSubdirectory("wm").FullName;
    private readonly string _settings;
    private readonly AppServices? _before;

    public AiMcpTests()
    {
        _settings = Path.Combine(_dir, "settings.json");
        AppSettings.Update(s => s with { AiFolders = [_dir] }, _settings);
        _before = AppServices.Instance;
        AppServices.Instance = new AppServices(_settings);
    }

    public void Dispose()
    {
        AppServices.Instance = _before!;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Form(string name)
    {
        var path = Path.Combine(_dir, name);
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(842);
        page.Height = XUnit.FromPoint(595);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var pen = new XPen(XColors.Black, 1);
            g.DrawRectangle(pen, 40, 40, 400, 30);
            g.DrawRectangle(pen, 40, 140, 8, 8);
        }
        doc.Save(path);
        return path;
    }

    private static async Task<MainWindow> OpenWindow(string? pdf)
    {
        var w = new MainWindow(new FakeDialogs()) { Width = 1440, Height = 900 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        if (pdf is not null) await w.ViewModel.LoadAsync(pdf);
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    private static AiProviderConfig Remote => AiProviderConfig.Claude() with { BaseUrl = "https://api.example.invalid" };

    // ------------------------------------------------------------------ the assistant

    [AvaloniaFact]
    public async Task Assistant_asks_once_where_data_goes_then_runs_tools_and_answers()
    {
        var pdf = Form("a.pdf");
        var w = await OpenWindow(pdf);
        var chat = new ScriptedChat(
            _ => ScriptedChat.Call("c1", "get_open_document", "{}"),
            r => ScriptedChat.Say("It is a one-page form."));
        var a = w.ViewModel.Assistant;
        a.ProviderFactory = () => (chat, Remote);
        w.ViewModel.ActiveTool = Tool.Ask;

        a.Input = "What is this?";
        await a.SendCommand.ExecuteAsync(null);
        Assert.True(a.HasNotice);                         // nothing sent before the notice is accepted
        Assert.Empty(chat.Requests);

        await a.AcceptNoticeCommand.ExecuteAsync(null);
        Assert.Equal(2, chat.Requests.Count);
        var first = chat.Requests[0];
        Assert.Contains("<open_document", first.History[^1].Text);    // the open PDF rides along as context
        Assert.Contains(pdf, first.History[^1].Text);
        Assert.Contains(first.Tools, t => t.Name == "fill_fields");   // same tools as MCP
        Assert.Contains(first.Tools, t => t.Name == "open_document");
        var result = chat.Requests[1].History[^1].ToolResults.Single();
        Assert.False(result.IsError);
        Assert.Contains("\"pages\": 1", result.Content);

        Assert.IsType<UserChatItem>(a.Items[0]);
        var tool = Assert.IsType<ToolChatItem>(a.Items[1]);
        Assert.False(tool.Running);
        Assert.False(tool.Failed);
        Assert.Equal("It is a one-page form.", Assert.IsType<AssistantChatItem>(a.Items[^1]).Text);

        // accepted once: the next question goes straight out
        a.Input = "Thanks";
        await a.SendCommand.ExecuteAsync(null);
        Assert.False(a.HasNotice);
        Assert.Equal(3, chat.Requests.Count);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_write_waits_for_the_inline_approval()
    {
        var pdf = Form("b.pdf");
        var w = await OpenWindow(pdf);
        AppSettings.Update(s => s with { AiNoticeAccepted = ["Claude:https://api.example.invalid"] }, _settings);
        var json = JsonSerializer.Serialize(new { path = pdf });
        var chat = new ScriptedChat(_ => ScriptedChat.Call("c1", "make_fillable", json), _ => ScriptedChat.Call("c2", "make_fillable", json),
            _ => ScriptedChat.Say("done"));
        var a = w.ViewModel.Assistant;
        a.ProviderFactory = () => (chat, Remote);

        // first call: denied in the chat card
        a.Input = "Make it fillable";
        var run = a.SendCommand.ExecuteAsync(null);
        var card = await WaitFor(() => a.Items.OfType<ApprovalChatItem>().FirstOrDefault());
        Assert.Empty(w.ViewModel.McpApprovals);           // AI approvals stay in the chat
        Assert.Contains("b_fillable.pdf", card.Approval.Summary);
        card.Approval.DenyCommand.Execute(null);

        // second call: approved; the file is written
        var second = await WaitFor(() => a.Items.OfType<ApprovalChatItem>().Skip(1).FirstOrDefault());
        second.Approval.ApproveCommand.Execute(null);
        await run;
        var results = chat.Requests[^1].History.Where(m => m.Role == AiRole.ToolResults).SelectMany(m => m.ToolResults).ToList();
        Assert.True(results[0].IsError);
        Assert.Contains("declined", results[0].Content);
        Assert.False(results[1].IsError, results[1].Content);
        Assert.True(File.Exists(Path.Combine(_dir, "b_fillable.pdf")));
        w.Close();
    }

    [AvaloniaFact]
    public async Task Read_only_access_refuses_writes_without_asking()
    {
        AppSettings.Update(s => s with { AiWrites = WriteAccess.Deny }, _settings);
        var pdf = Form("c.pdf");
        var r = await AppServices.Instance.Invoker.InvokeAsync("make_fillable", JsonSerializer.SerializeToElement(new { path = pdf }),
            new ToolActor(ToolOrigin.Mcp, "mcp:test", "test (MCP)"), Ct);
        Assert.True(r.IsError);
        Assert.Contains("read-only", r.Text);
        Assert.False(File.Exists(Path.Combine(_dir, "c_fillable.pdf")));

        // outside the allowed folders: refused even for reads
        var other = Directory.CreateTempSubdirectory("wo").FullName;
        try
        {
            File.Copy(pdf, Path.Combine(other, "x.pdf"));
            var outside = await AppServices.Instance.Invoker.InvokeAsync("pdf_info", JsonSerializer.SerializeToElement(new { path = Path.Combine(other, "x.pdf") }),
                new ToolActor(ToolOrigin.Mcp, "mcp:test", "test (MCP)"), Ct);
            Assert.True(outside.IsError);
            Assert.Contains("outside", outside.Text);
        }
        finally { Directory.Delete(other, true); }
    }

    // ------------------------------------------------------------------ the MCP server

    private McpHostOptions TestOptions => new() { Port = 0, LocalSocketPath = Path.Combine(_dir, "m.sock") };

    [AvaloniaFact]
    public async Task Mcp_over_http_needs_the_token_and_shows_approvals_as_cards()
    {
        var pdf = Form("d.pdf");
        var w = await OpenWindow(pdf);
        var services = AppServices.Instance;
        await using var host = await McpServerHost.StartAsync(TestOptions, services.Invoker, null, Ct);

        using (var http = new HttpClient())
        {
            var anonymous = await http.PostAsync(host.HttpEndpoint, new StringContent("{}"), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = host.HttpEndpoint!,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + host.Token },
        }), new McpClientOptions { ClientInfo = new Implementation { Name = "claude-code", Version = "1" } }, cancellationToken: Ct);

        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        Assert.Contains(tools, t => t.Name == "pdf_info" && t.ProtocolTool.Annotations!.ReadOnlyHint == true);
        Assert.Contains(tools, t => t.Name == "fill_fields" && t.ProtocolTool.Annotations!.ReadOnlyHint == false);

        var open = await client.CallToolAsync("get_open_document", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.Contains(pdf, Assert.IsType<TextContentBlock>(open.Content[0]).Text);

        var page = await client.CallToolAsync("render_page", new Dictionary<string, object?> { ["path"] = pdf, ["page"] = 1 }, cancellationToken: Ct);
        Assert.False(page.IsError);
        Assert.Contains(page.Content, c => c is ImageContentBlock);

        // a write: a card over the page (not a window); approve it there
        var write = client.CallToolAsync("make_fillable", new Dictionary<string, object?> { ["path"] = pdf }, cancellationToken: Ct).AsTask();
        var card = await WaitFor(() => w.ViewModel.McpApprovals.FirstOrDefault());
        Assert.Contains("claude-code", card.Title);
        card.ApproveCommand.Execute(null);
        var written = await write;
        Assert.True(written.IsError != true, Assert.IsType<TextContentBlock>(written.Content[0]).Text);
        Assert.True(File.Exists(Path.Combine(_dir, "d_fillable.pdf")));
        await WaitFor(() => w.ViewModel.McpApprovals.Count == 0 ? "" : null);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Claude_desktop_reaches_the_app_through_washi_mcp_app()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket test");
        var pdf = Form("e.pdf");
        var options = TestOptions with { EnableHttp = false };
        await using var host = await McpServerHost.StartAsync(options, AppServices.Instance.Invoker, null, Ct);

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [BridgeDll(), "--app", "--socket", options.LocalSocketPath!, "--no-launch"],
            Name = "bridge",
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        }), new McpClientOptions { ClientInfo = new Implementation { Name = "claude-desktop", Version = "1" } }, cancellationToken: Ct);

        var info = await client.CallToolAsync("pdf_info", new Dictionary<string, object?> { ["path"] = pdf }, cancellationToken: Ct);
        Assert.Contains("\"pages\"", Assert.IsType<TextContentBlock>(info.Content[0]).Text);
        var c = await WaitFor(() => host.Clients.FirstOrDefault(x => x.Name == "claude-desktop"));
        Assert.Equal(McpClientTransport.Local, c.Transport);
    }

    [AvaloniaFact]
    public void Settings_page_saves_folders_and_write_access()
    {
        var dialogs = new FakeDialogs { Folder = Directory.CreateTempSubdirectory("wf").FullName };
        try
        {
            var vm = new SettingsViewModel(_settings, AppServices.Instance, dialogs);
            vm.AddFolderCommand.Execute(null);
            vm.Writes = SettingsViewModel.WriteChoices.Single(c => c.Value == WriteAccess.Allow);
            vm.OpenDocumentFolder = false;
            var saved = AppSettings.Load(_settings);
            Assert.Equal([_dir, dialogs.Folder], saved.AiFolders);
            Assert.Equal(WriteAccess.Allow, saved.AiWrites);
            Assert.False(saved.AiOpenDocumentFolder);
            vm.RemoveFolderCommand.Execute(_dir);
            Assert.Equal([dialogs.Folder], AppSettings.Load(_settings).AiFolders);
        }
        finally { Directory.Delete(dialogs.Folder, true); }
    }

    [AvaloniaFact]
    public async Task Settings_is_a_page_at_the_bottom_of_the_rail_not_a_window()
    {
        var w = await OpenWindow(Form("f.pdf"));
        w.ShowSettings("mcp");
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.ViewModel.IsSettingsTool);
        Assert.False(w.ViewModel.ShowToolPanel);
        Assert.False(w.ViewModel.ShowRightRail);
        var page = w.FindControl<Washi.App.Views.SettingsView>("SettingsPage")!;
        Assert.True(page.IsVisible);
        Assert.IsType<SettingsViewModel>(page.DataContext);   // its own view model, not the window's
        Assert.True(page.FindControl<Button>("SaveAiButton")!.IsEffectivelyEnabled);
        Assert.False(w.FindControl<Avalonia.Controls.Panel>("DocumentArea")!.IsVisible);
        Assert.Single(Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
            ? d.Windows.Where(x => x.IsVisible) : [w]);
        w.ViewModel.ActiveTool = Tool.Form;                 // back to the document
        Assert.True(w.FindControl<Avalonia.Controls.Panel>("DocumentArea")!.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_model_is_picked_from_the_providers_own_list()
    {
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test");   // a key from the environment: no Keychain
        try
        {
            var vm = new SettingsViewModel(_settings, AppServices.Instance, new FakeDialogs());
            var asked = new List<(Washi.Ai.AiProvider, string?)>();
            vm.ListModels = (ai, key, _) =>
            {
                asked.Add((ai.Provider, key));
                return Task.FromResult<IReadOnlyList<string>>(["gpt-5.5", "gpt-5.5-mini"]);
            };

            vm.Provider = SettingsViewModel.Providers.Single(p => p.Value == Washi.Ai.AiProvider.OpenAI);
            await WaitFor(() => vm.LoadingModels || vm.Models.Count == 0 ? null : "");
            Assert.Equal((Washi.Ai.AiProvider.OpenAI, "sk-test"), Assert.Single(asked));
            Assert.Equal(["gpt-5.5", "gpt-5.5-mini"], vm.Models);
            Assert.Equal("", vm.Model);                 // another provider: Claude's model is not carried over
            Assert.Contains("2", vm.ModelsStatus);

            vm.Model = "gpt-4-old";
            await vm.RefreshModelsCommand.ExecuteAsync(null);
            Assert.Equal("gpt-4-old", vm.Model);        // typed text survives a refresh
            Assert.Contains("gpt-4-old", vm.ModelsStatus);   // ...and is reported as not listed

            vm.ListModels = (_, _, _) => throw new Washi.Ai.Chat.AiProviderException(Washi.Ai.Chat.AiErrorKind.Authentication, "401 invalid key");
            await vm.RefreshModelsCommand.ExecuteAsync(null);
            Assert.True(vm.ModelsStatusIsError);
            Assert.Contains("401", vm.ModelsStatus);
        }
        finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", env); }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 400; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } v) return v;
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("condition not reached");
    }

    /// <summary>src/Washi.Mcp/bin/&lt;config&gt;/net10.0/washi-mcp.dll, built through the project reference.</summary>
    private static string BridgeDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = dir.Parent!.Name;                      // .../bin/Debug/net10.0/
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Washi.slnx"))) dir = dir.Parent;
        var dll = Path.Combine(dir!.FullName, "src", "Washi.Mcp", "bin", config, "net10.0", "washi-mcp.dll");
        Assert.True(File.Exists(dll), dll);
        return dll;
    }
}
