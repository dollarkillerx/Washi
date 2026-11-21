// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Ai;

public enum AiProvider
{
    /// <summary>Claude, through the official Anthropic C# SDK.</summary>
    Anthropic,
    /// <summary>OpenAI GPT models, through the official OpenAI .NET SDK.</summary>
    OpenAI,
    /// <summary>DeepSeek; its API is OpenAI-compatible, so it uses the OpenAI client with its own endpoint.</summary>
    DeepSeek,
    /// <summary>Any other OpenAI-compatible endpoint, e.g. a local Ollama server for offline use.</summary>
    OpenAICompatible,
}

/// <summary>
/// Which model the built-in AI talks to. Model ids are user settings, not constants:
/// providers rename and retire models faster than this app is released.
/// API keys are not part of this record; they live in the OS keychain.
/// </summary>
public sealed record AiSettings(AiProvider Provider, string Model, Uri? Endpoint = null)
{
    public static AiSettings DefaultClaude { get; } = new(AiProvider.Anthropic, "claude-opus-5-5");

    public static Uri DeepSeekEndpoint { get; } = new("https://api.deepseek.com");

    /// <summary>True when documents leave this machine with this setting.</summary>
    public bool SendsDataOffDevice => Provider != AiProvider.OpenAICompatible
        || Endpoint is null || !Endpoint.IsLoopback;
}
