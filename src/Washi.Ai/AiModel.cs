// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Washi.Ai;

public sealed record AiImage(string MediaType, byte[] Data);

/// <summary>One request: instructions, the user's material, and optionally a JSON schema the answer must follow.</summary>
public sealed record AiRequest(
    string System,
    string Prompt,
    IReadOnlyList<AiImage> Images,
    JsonElement? Schema = null,
    int MaxTokens = 16000);

public sealed class AiException(string message) : Exception(message);

/// <summary>
/// The smallest surface Washi needs from a model. Each provider maps it onto its own API so
/// provider features stay available (Claude structured outputs and refusal fallbacks, OpenAI
/// JSON schema mode, DeepSeek JSON mode).
/// </summary>
public interface IAiModel
{
    /// <summary>"claude-opus-5-5 (Anthropic)" — shown to the user before anything is sent.</summary>
    string Describe { get; }

    /// <summary>False only for a local model (loopback endpoint): nothing leaves the machine.</summary>
    bool SendsDataOffDevice { get; }

    /// <summary>Whether page images can be sent (DeepSeek's chat models take text only).</summary>
    bool AcceptsImages { get; }

    /// <summary>Returns the answer text; with a schema, a JSON document following it.</summary>
    Task<string> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);
}
