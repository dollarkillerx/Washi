// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Washi.Ai;
using Washi.Core.Localization;

namespace Washi.App.Services;

/// <summary>Editor preferences in ~/.config/washi/settings.json (next to the AI settings).</summary>
public sealed record AppSettings(string? Language = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
