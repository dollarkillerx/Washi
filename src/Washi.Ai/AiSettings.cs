// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Washi.Ai;

[JsonConverter(typeof(JsonStringEnumConverter<AiProvider>))]
public enum AiProvider
{
    /// <summary>Claude, through the official Anthropic C# SDK.</summary>
    Anthropic,
    /// <summary>OpenAI GPT models, through the official OpenAI .NET SDK.</summary>
    OpenAI,
    /// <summary>DeepSeek; OpenAI-compatible API at https://api.deepseek.com.</summary>
    DeepSeek,
    /// <summary>Any other OpenAI-compatible endpoint, e.g. a local Ollama server for offline use.</summary>
    OpenAICompatible,
}

/// <summary>
/// Which model the built-in AI talks to. Stored in ~/.config/washi/ai.json (no secrets).
/// Model ids are settings, not constants: providers rename and retire models.
/// API keys come from the environment or the macOS Keychain, never from this file.
/// </summary>
/// <param name="Vision">Whether page images are sent (default yes). Set false for text-only models.</param>
public sealed record AiSettings(AiProvider Provider, string Model, Uri? Endpoint = null, bool? Vision = null)
{
    public static AiSettings DefaultClaude { get; } = new(AiProvider.Anthropic, "claude-opus-5-5");

    public static Uri DeepSeekEndpoint { get; } = new("https://api.deepseek.com");

    /// <summary>True when documents leave this machine with this setting.</summary>
    public bool SendsDataOffDevice => Provider != AiProvider.OpenAICompatible
        || Endpoint is null || !Endpoint.IsLoopback;

    public static string ConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "washi", "ai.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static AiSettings Load(string? path = null)
    {
        path ??= ConfigPath;
        if (!File.Exists(path)) return DefaultClaude;
        return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), Json) ?? DefaultClaude;
    }

    public void Save(string? path = null)
    {
        path ??= ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static string KeyVariable(AiProvider p) => p switch
    {
        AiProvider.Anthropic => "ANTHROPIC_API_KEY",
        AiProvider.OpenAI => "OPENAI_API_KEY",
        AiProvider.DeepSeek => "DEEPSEEK_API_KEY",
        _ => "WASHI_AI_API_KEY",
    };

    public bool AcceptsImages => Vision ?? true;

    /// <summary>The model ids the provider offers right now (to check a model name).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var key = ApiKeys.Find(Provider) ?? "";
        if (key.Length == 0 && Provider != AiProvider.OpenAICompatible)
            throw new AiException($"no API key for {Provider}: set {KeyVariable(Provider)} or run: washi ai key {Provider.ToString().ToLowerInvariant()}", "Err_AiNoKey", Provider.ToString(), KeyVariable(Provider));
        if (Provider == AiProvider.Anthropic)
        {
            var client = new Anthropic.AnthropicClient { ApiKey = key };
            var page = await client.Models.List(cancellationToken: ct);
            return [.. page.Items.Select(m => m.ID)];
        }
        var options = new OpenAI.OpenAIClientOptions();
        var endpoint = Endpoint ?? (Provider == AiProvider.DeepSeek ? DeepSeekEndpoint : null);
        if (endpoint is not null) options.Endpoint = endpoint;
        var models = new OpenAI.Models.OpenAIModelClient(new System.ClientModel.ApiKeyCredential(key.Length > 0 ? key : "local"), options);
        var list = await models.GetModelsAsync(ct);
        return [.. list.Value.Select(m => m.Id).Order()];
    }

    /// <summary>The chat provider settings for the assistant (tool calling, streaming), from this one configuration.</summary>
    public Chat.AiProviderConfig ToProviderConfig()
    {
        var images = new Chat.ProviderCapabilities(Images: AcceptsImages);
        var config = Provider switch
        {
            AiProvider.Anthropic => Chat.AiProviderConfig.Claude() with { BaseUrl = Endpoint?.ToString().TrimEnd('/') ?? "https://api.anthropic.com" },
            AiProvider.OpenAI => Chat.AiProviderConfig.OpenAI() with { BaseUrl = Endpoint?.ToString().TrimEnd('/') ?? "https://api.openai.com/v1" },
            AiProvider.DeepSeek => Chat.AiProviderConfig.DeepSeek() with { BaseUrl = (Endpoint ?? DeepSeekEndpoint).ToString().TrimEnd('/') },
            _ => Chat.AiProviderConfig.Custom(Chat.AiProtocol.OpenAICompatible, Endpoint?.ToString().TrimEnd('/') ?? "http://127.0.0.1:11434/v1"),
        };
        return config with { Name = Provider == AiProvider.OpenAICompatible ? "Local" : config.Name, Model = Model, Capabilities = images };
    }

    /// <summary>The assistant's provider. Throws <see cref="AiException"/> when no model or key is set.</summary>
    public Chat.IChatProvider CreateChatProvider(HttpClient http)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new AiException("no model is set: choose one in Settings → AI", "Err_AiNoModel");
        var key = ApiKeys.Find(Provider) ?? "";
        if (key.Length == 0 && Provider != AiProvider.OpenAICompatible)
            throw new AiException($"no API key for {Provider}: add it in Settings → AI, or set {KeyVariable(Provider)}", "Err_AiNoKey", Provider.ToString(), KeyVariable(Provider));
        if (Provider == AiProvider.OpenAICompatible && Endpoint is null)
            throw new AiException("an OpenAI-compatible provider needs an endpoint, e.g. http://localhost:11434/v1", "Err_AiNoEndpoint");
        return Chat.AiProviders.Create(ToProviderConfig(), key.Length > 0 ? key : null, http);
    }

    /// <summary>Builds the model client. Throws <see cref="AiException"/> when it cannot.</summary>
    public IAiModel CreateModel()
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new AiException("no model is set; run: washi ai config <provider> <model>", "Err_AiNoModel");
        var key = ApiKeys.Find(Provider) ?? "";
        if (key.Length == 0 && Provider != AiProvider.OpenAICompatible)
            throw new AiException($"no API key for {Provider}: set {KeyVariable(Provider)} or run: washi ai key {Provider.ToString().ToLowerInvariant()}", "Err_AiNoKey", Provider.ToString(), KeyVariable(Provider));
        return Provider switch
        {
            AiProvider.Anthropic => new ClaudeModel(key, Model),
            AiProvider.DeepSeek => new OpenAICompatibleModel(this with { Endpoint = Endpoint ?? DeepSeekEndpoint }, key),
            AiProvider.OpenAICompatible when Endpoint is null => throw new AiException("an OpenAI-compatible provider needs an endpoint, e.g. http://localhost:11434/v1", "Err_AiNoEndpoint"),
            _ => new OpenAICompatibleModel(this, key),
        };
    }
}

/// <summary>API keys: environment variable first, then the macOS Keychain (service "washi").</summary>
public static class ApiKeys
{
    private const string Service = "washi";

    public static string? Find(AiProvider provider)
    {
        if (Environment.GetEnvironmentVariable(AiSettings.KeyVariable(provider)) is { Length: > 0 } env) return env;
        if (!OperatingSystem.IsMacOS()) return null;
        var (code, output) = Security("find-generic-password", "-s", Service, "-a", Account(provider), "-w");
        return code == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary>Stores a key in the macOS Keychain (replacing an older one).</summary>
    public static void Store(AiProvider provider, string key)
    {
        if (!OperatingSystem.IsMacOS())
            throw new AiException($"the keychain is only supported on macOS for now; set {AiSettings.KeyVariable(provider)} instead", "Err_AiKeychainMacOnly", AiSettings.KeyVariable(provider));
        var (code, output) = Security("add-generic-password", "-U", "-s", Service, "-a", Account(provider), "-l", $"Washi {provider} API key", "-w", key);
        if (code != 0) throw new AiException($"could not store the key in the Keychain: {output.Trim()}", "Err_AiKeychain", output.Trim());
    }

    /// <summary>Another secret of Washi's (e.g. "mcp-token"): the macOS Keychain, or a 0600 file elsewhere.</summary>
    public static string? FindSecret(string account)
    {
        if (OperatingSystem.IsMacOS())
        {
            var (code, output) = Security("find-generic-password", "-s", Service, "-a", account, "-w");
            return code == 0 && output.Trim().Length > 0 ? output.Trim() : null;
        }
        var file = SecretFile(account);
        return File.Exists(file) ? File.ReadAllText(file).Trim() is { Length: > 0 } v ? v : null : null;
    }

    public static void StoreSecret(string account, string label, string value)
    {
        if (OperatingSystem.IsMacOS())
        {
            var (code, output) = Security("add-generic-password", "-U", "-s", Service, "-a", account, "-l", label, "-w", value);
            if (code != 0) throw new AiException($"could not store the secret in the Keychain: {output.Trim()}", "Err_AiKeychain", output.Trim());
            return;
        }
        var file = SecretFile(account);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, value);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string SecretFile(string account) => Path.Combine(Path.GetDirectoryName(AiSettings.ConfigPath)!, account);

    /// <summary>Removes a stored key from the macOS Keychain (no-op when there is none).</summary>
    public static void Remove(AiProvider provider)
    {
        if (OperatingSystem.IsMacOS()) Security("delete-generic-password", "-s", Service, "-a", Account(provider));
    }

    /// <summary>True when the key comes from the environment (it then wins over the Keychain).</summary>
    public static bool FromEnvironment(AiProvider provider) =>
        Environment.GetEnvironmentVariable(AiSettings.KeyVariable(provider)) is { Length: > 0 };

    private static string Account(AiProvider p) => p.ToString().ToLowerInvariant();

    private static (int Code, string Output) Security(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
