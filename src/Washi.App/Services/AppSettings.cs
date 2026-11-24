// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Washi.Ai;
using Washi.Tools;
using Washi.Core.Localization;

namespace Washi.App.Services;

/// <summary>Editor preferences in ~/.config/washi/settings.json (next to the AI settings). No secrets: API keys and the
/// MCP token live in the macOS Keychain.</summary>
public sealed record AppSettings(string? Language = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Folders the built-in AI and MCP clients may read and write in.</summary>
    public IReadOnlyList<string> AiFolders { get; init; } = [];

    /// <summary>Whether AI / MCP tools may write new files (default: ask each time).</summary>
    public WriteAccess AiWrites { get; init; } = WriteAccess.Ask;

    /// <summary>Also allow the folder of the PDF open in the editor.</summary>
    public bool AiOpenDocumentFolder { get; init; } = true;

    /// <summary>Look for a new version once after start-up (installed builds only).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The in-app MCP server (off by default).</summary>
    public bool McpEnabled { get; init; }

    /// <summary>0 until the first start picks a port; fixed afterwards so client configurations keep working.</summary>
    public int McpPort { get; init; }

    /// <summary>Providers whose data notice ("what you send goes to …") the user accepted in the assistant.</summary>
    public IReadOnlyList<string> AiNoticeAccepted { get; init; } = [];

    public ToolAccessSettings ToolAccess => new(AiFolders, AiWrites, AiOpenDocumentFolder);

    /// <summary>Loads, changes and saves in one step (settings are small; the last writer wins).</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change, string? path = null)
    {
        var next = change(Load(path));
        next.Save(path);
        return next;
    }

    public static string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(AiSettings.ConfigPath)!, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= Path;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();   // a broken settings file must not stop the editor
        }
    }

    public void Save(string? path = null)
    {
        path ??= Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>WASHI_LANG wins (for testing and scripts), then the saved choice, then the system language.</summary>
    public static void ApplyLanguage(AppSettings settings)
    {
        var env = Environment.GetEnvironmentVariable("WASHI_LANG");
        L.Culture = L.Resolve(string.IsNullOrWhiteSpace(env) ? settings.Language : env);
    }
}
