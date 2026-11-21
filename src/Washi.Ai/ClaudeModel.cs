// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Washi.Ai;

/// <summary>
/// Claude through the official Anthropic C# SDK (Messages API, beta path for refusal fallbacks).
/// Structured outputs guarantee the JSON shape; if the model's safeguards decline a request,
/// the server re-serves it with a fallback model ("default" routing) inside the same call.
/// </summary>
public sealed class ClaudeModel(string apiKey, string model) : IAiModel
{
    private readonly AnthropicClient _client = new() { ApiKey = apiKey };

    public string Describe => $"{model} (Anthropic)";
    public bool SendsDataOffDevice => true;
    public bool AcceptsImages => true;

    public async Task<string> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var img in request.Images)
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(img.Data), MediaType = img.MediaType },
            });
        content.Add(new BetaTextBlockParam { Text = request.Prompt });

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = [new BetaUserMessageParam { Content = content }],
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };
        if (request.Schema is { } schema)
        {
            parameters = parameters with
            {
                OutputConfig = new BetaOutputConfig
                {
                    Format = new BetaJsonOutputFormat
                    {
                        Schema = schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                    },
                },
            };
        }

        var response = await _client.Beta.Messages.Create(parameters, cancellationToken);
        if (response.StopReason == "refusal")
            throw new AiException($"the model declined this request{(response.StopDetails is { } d ? $" ({d.Category}: {d.Explanation})" : "")}",
                "Err_AiDeclined", response.StopDetails is { } sd ? $"{sd.Category}: {sd.Explanation}" : "");
        if (response.StopReason == "max_tokens")
            throw new AiException("the answer was cut off (max_tokens); try fewer pages or fields at once", "Err_AiCutOff");

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return text;
    }
}
