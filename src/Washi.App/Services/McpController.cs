// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH's McpController (same authors, AGPL-3.0-or-later).
using Washi.Ai;
using Washi.Core.Localization;
using Washi.McpHost;
using Washi.Tools;

namespace Washi.App.Services;

/// <param name="Transport">"http" or "local".</param>
/// <param name="CanDisconnect">Local-socket sessions can be closed; stateless HTTP has none (regenerate the token instead).</param>
public sealed record McpClientRow(string Id, string Transport, string Name, DateTimeOffset LastSeen, long Calls, int InFlight, bool CanDisconnect)
{
    public string Detail => L.F("Ui_McpClientDetail", Transport == "local" ? "washi-mcp" : "HTTP", Calls, LastSeen.ToLocalTime().ToString("HH:mm:ss"));
}

/// <summary>
/// Owns the in-app MCP server: off by default, started on the thread pool when enabled, port and token kept so
/// client configurations keep working. Safe to call from the UI thread; <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class McpController : IAsyncDisposable
{
    public const string TokenAccount = "mcp-token";

    private readonly IToolInvoker _invoker;
    private readonly string? _settingsPath;
    private readonly Func<McpHostOptions, McpHostOptions> _configure;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpServerHost? _host;
    private int _changePending;

    /// <param name="configure">Tests override the socket path and port.</param>
    public McpController(IToolInvoker invoker, string? settingsPath = null, Func<McpHostOptions, McpHostOptions>? configure = null)
    {
        _invoker = invoker;
        _settingsPath = settingsPath;
        _configure = configure ?? (o => o);
        var bridge = McpClientSetup.DefaultBridgePath();
        BridgePath = File.Exists(bridge) ? bridge : null;
    }

    public bool IsEnabled => AppSettings.Load(_settingsPath).McpEnabled;
    public bool IsRunning => _host is not null;
    /// <summary>Last start failure (port in use, another Washi owns the socket, ...).</summary>
    public string? StartError { get; private set; }
    public Uri? HttpEndpoint => _host?.HttpEndpoint;
    public string? LocalSocketPath => _host?.LocalSocketPath;
    public string? Token { get; private set; }
    /// <summary>The washi-mcp stdio bridge next to the app, when present.</summary>
    public string? BridgePath { get; }
    public event Action? Changed;

    public IReadOnlyList<McpClientRow> Clients => _host?.Clients
        .Select(c => new McpClientRow(c.Id, c.Transport == McpClientTransport.Local ? "local" : "http", c.DisplayName,
            c.LastSeen, c.CallCount, c.InFlightCalls, CanDisconnect: c.Transport == McpClientTransport.Local && c.IsConnected))
        .ToList() ?? [];

    public string ClaudeCodeCommand => HttpEndpoint is { } e && Token is { } t ? McpClientSetup.ClaudeCodeCommand(e, t) : "";
    public string ClaudeDesktopJson => BridgePath is { } b ? McpClientSetup.ClaudeDesktopConfigJson(b) : "";
    public string GenericHttpJson => HttpEndpoint is { } e && Token is { } t ? McpClientSetup.HttpConfigJson(e, t) : "";

    /// <summary>At start-up: starts the server when it was left on. Never throws for start failures.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsEnabled) await StartAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        AppSettings.Update(s => s with { McpEnabled = enabled }, _settingsPath);
        if (enabled) await StartAsync(ct).ConfigureAwait(false);
        else await StopAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Invalidates the current token: HTTP clients must be set up again. No restart.</summary>
    public Task RegenerateTokenAsync()
    {
        var token = McpToken.Generate();
        SaveToken(token);
        Token = token;
        _host?.SetToken(token);
        RaiseChanged();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(string clientId)
    {
        if (_host is { } h) await Task.Run(() => h.DisconnectAsync(clientId)).ConfigureAwait(false);
        RaiseChanged();
    }

    private async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is not null) return;
            StartError = null;
            Token ??= await Task.Run(LoadToken, ct).ConfigureAwait(false);
            if (Token is null)
            {
                Token = McpToken.Generate();
                SaveToken(Token);
            }
            var port = AppSettings.Load(_settingsPath).McpPort;
            var options = _configure(new McpHostOptions
            {
                Port = port, Token = Token,
                ServerVersion = typeof(McpController).Assembly.GetName().Version?.ToString(3) ?? "0",
            });
            try
            {
                var host = await McpServerHost.StartAsync(options, _invoker, null, ct).ConfigureAwait(false);
                _host = host;
                host.ClientsChanged += RaiseChanged;
                if (host.Port is { } bound && bound != port && options.Port == 0)
                    AppSettings.Update(s => s with { McpPort = bound }, _settingsPath);   // first start: keep this port
            }
            catch (McpInstanceRunningException e)
            {
                StartError = L.F("Mcp_ErrInstance", e.Address ?? "");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                StartError = L.F("Mcp_ErrStart", e.Message);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is not { } h) return;
            _host = null;
            h.ClientsChanged -= RaiseChanged;
            await Task.Run(() => h.DisposeAsync().AsTask()).ConfigureAwait(false);
            StartError = null;
        }
        finally { _gate.Release(); }
    }

    private static string? LoadToken()
    {
        try { return ApiKeys.FindSecret(TokenAccount); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static void SaveToken(string token)
    {
        try { ApiKeys.StoreSecret(TokenAccount, "Washi MCP token", token); }
        catch (Exception e) when (e is AiException or IOException or UnauthorizedAccessException) { /* kept in memory for this run */ }
    }

    /// <summary>At most one Changed per 250 ms; client activity fires on every tool call.</summary>
    private void RaiseChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1) return;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            Volatile.Write(ref _changePending, 0);
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
