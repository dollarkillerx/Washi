// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace Washi.Ai;

/// <summary>
/// OpenAI GPT models, DeepSeek and local OpenAI-compatible servers (e.g. Ollama) through the
/// official OpenAI .NET SDK. OpenAI gets strict JSON-schema output; DeepSeek and local servers
/// get JSON mode with the schema spelled out in the instructions.
/// </summary>
public sealed class OpenAICompatibleModel : IAiModel
{
    private readonly ChatClient _client;
    private readonly AiSettings _settings;

    public OpenAICompatibleModel(AiSettings settings, string apiKey)
    {
        _settings = settings;
        var options = new OpenAIClientOptions();
        if (settings.Endpoint is { } e) options.Endpoint = e;
        _client = new ChatClient(settings.Model, new ApiKeyCredential(apiKey.Length > 0 ? apiKey : "local"), options);
    }

    public string Describe => $"{_settings.Model} ({_settings.Provider}{(_settings.Endpoint is { } e ? ", " + e.Host : "")})";
    public bool SendsDataOffDevice => _settings.SendsDataOffDevice;

    // DeepSeek's chat models are text-only.
    public bool AcceptsImages => _settings.Provider != AiProvider.DeepSeek;

    public async Task<string> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        bool strictSchema = _settings.Provider == AiProvider.OpenAI;
        var system = request.System;
        if (request.Schema is { } s && !strictSchema)
            system += "\n\nAnswer with one JSON object only, following this JSON Schema:\n" + s.GetRawText();

        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(request.Prompt) };
        if (AcceptsImages)
            foreach (var img in request.Images)
                parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(img.Data), img.MediaType));

        var options = new ChatCompletionOptions { MaxOutputTokenCount = request.MaxTokens };
        if (request.Schema is { } schema)
            options.ResponseFormat = strictSchema
                ? ChatResponseFormat.CreateJsonSchemaFormat("washi_result", BinaryData.FromString(schema.GetRawText()), jsonSchemaIsStrict: true)
                : ChatResponseFormat.CreateJsonObjectFormat();

        ChatCompletion completion = await _client.CompleteChatAsync(
            [new SystemChatMessage(system), new UserChatMessage(parts)], options, cancellationToken);
        if (completion.FinishReason == ChatFinishReason.Length)
            throw new AiException("the answer was cut off (length limit); try fewer pages or fields at once");
        if (completion.FinishReason == ChatFinishReason.ContentFilter)
            throw new AiException("the provider's content filter stopped the answer");
        return StripThinking(string.Concat(completion.Content.Select(c => c.Text)));
    }

    /// <summary>Local reasoning models (DeepSeek-R1 style) prepend &lt;think&gt;…&lt;/think&gt;; it is not part of the answer.</summary>
    internal static string StripThinking(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"^\s*<think>.*?</think>\s*", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
}
