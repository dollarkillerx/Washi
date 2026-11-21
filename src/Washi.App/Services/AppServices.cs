// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Washi.Tools;

namespace Washi.App.Services;

/// <summary>
/// What lives as long as the app, not the window (the window is rebuilt when the language changes): the tool layer
/// shared by the assistant and MCP, approvals, the MCP server and one HTTP client for the AI providers.
/// </summary>
public sealed class AppServices
{
    private static AppServices? s_instance;

    /// <summary>The app's services; tests may replace them before creating a window.</summary>
    public static AppServices Instance { get => s_instance ??= new AppServices(); set => s_instance = value; }

    public AppServices(string? settingsPath = null)
    {
        SettingsPath = settingsPath;
        Approvals = new ApprovalService();
        Invoker = new ToolInvoker(
            () => AppSettings.Load(settingsPath).ToolAccess,
            Approvals,
            () => Editor?.Snapshot?.Path,
            policy => AppTools.Create(policy, () => Editor),
            logPath: Path.Combine(Path.GetDirectoryName(settingsPath ?? AppSettings.Path)!, "tool-calls.jsonl"));
        Mcp = new McpController(Invoker, settingsPath);
    }

    public string? SettingsPath { get; }
    public ApprovalService Approvals { get; }
    public ToolInvoker Invoker { get; }
    public McpController Mcp { get; }

    /// <summary>The main window's editor (set by the window; replaced when it is rebuilt).</summary>
    public IEditorHost? Editor { get; set; }

    /// <summary>No overall timeout: answers stream for minutes; each request is cancelled by its own token.</summary>
    public HttpClient Http { get; } = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
