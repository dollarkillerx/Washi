// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH (same authors, AGPL-3.0-or-later).
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Washi.McpHost;

/// <summary>The exact snippets the MCP settings page shows for copying into MCP clients.</summary>
public static class McpClientSetup
{
    /// <summary>Server key used in every snippet (<c>claude mcp add … washi …</c>, <c>"mcpServers": {"washi": …}</c>).</summary>
    public const string ServerKey = "washi";

    /// <summary><c>claude mcp add --transport http washi http://127.0.0.1:PORT/mcp --header "Authorization: Bearer TOKEN"</c></summary>
    public static string ClaudeCodeCommand(Uri endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return $"claude mcp add --transport http {ServerKey} {endpoint.AbsoluteUri} --header \"Authorization: Bearer {token}\"";
    }

    /// <summary>
    /// Claude Desktop <c>claude_desktop_config.json</c> using the stdio bridge:
    /// <c>{"mcpServers":{"washi":{"command":"/path/to/washi-mcp","args":["--app"]}}}</c> (indented). <paramref name="socketPath"/> adds
    /// <c>"--socket", path</c>; pass it only when the server does not use the default path.
    /// </summary>
    public static string ClaudeDesktopConfigJson(string bridgePath, string? socketPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(bridgePath);
        return WriteJson(w =>
        {
            w.WriteString("command", bridgePath);
            w.WriteStartArray("args");
            w.WriteStringValue("--app");
            if (!string.IsNullOrEmpty(socketPath))
            {
                w.WriteStringValue("--socket");
                w.WriteStringValue(socketPath);
            }
            w.WriteEndArray();
        });
    }

    /// <summary>
    /// Generic config for clients that speak Streamable HTTP (Claude Code <c>.mcp.json</c>, Cursor, …):
    /// <c>{"mcpServers":{"washi":{"type":"http","url":"…","headers":{"Authorization":"Bearer …"}}}}</c> (indented).
    /// </summary>
    public static string HttpConfigJson(Uri endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        return WriteJson(w =>
        {
            w.WriteString("type", "http");
            w.WriteString("url", endpoint.AbsoluteUri);
            w.WriteStartObject("headers");
            w.WriteString("Authorization", "Bearer " + token);
            w.WriteEndObject();
        });
    }

    /// <summary>Where the app ships the bridge: <c>mcp/washi-mcp</c> next to the app executable (in the .app bundle:
    /// <c>Contents/MacOS/mcp/washi-mcp</c>), in its own folder so its dependencies never mix with the app's.</summary>
    public static string DefaultBridgePath() =>
        Path.Combine(AppContext.BaseDirectory, "mcp", OperatingSystem.IsWindows() ? "washi-mcp.exe" : "washi-mcp");

    private static string WriteJson(Action<Utf8JsonWriter> writeServer)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(ServerKey);
            writeServer(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
