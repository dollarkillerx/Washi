// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH (same authors, AGPL-3.0-or-later).
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Washi.Tools;

namespace Washi.Ai.Chat;

public sealed record AgentOptions
{
    public int MaxSteps { get; init; } = 25;
    public int MaxToolOutputChars { get; init; } = 64 * 1024;
    public bool RedactSecrets { get; init; } = true;
}

/// <summary>One question to run through the loop.</summary>
/// <param name="ConversationId">Flows to the approval UI (<see cref="AiToolCallScope"/>) so the card lands in the right chat.</param>
/// <param name="History">Everything so far (append-only; not modified).</param>
/// <param name="UserText">Already composed (context + question) and redacted by <see cref="AiContextComposer"/>.</param>
public sealed record AgentTurn(
    Guid ConversationId,
    IReadOnlyList<AiMessage> History,
    string UserText,
    string Model,
    AiEffort Effort,
    IReadOnlyList<AiToolSpec> Tools,
    ToolActor Actor,
    string SystemPrompt = AiPrompts.Assistant,
    int MaxTokens = 32000);

public enum AgentStop { Completed, MaxTokens, Refusal, Error, StepLimit }

public abstract record AgentEvent;
/// <summary>A provider event, passed through for live rendering.</summary>
public sealed record AgentStreamEvent(ChatEvent Event) : AgentEvent;
/// <summary>Persist this entry (in order): the history is the list of these.</summary>
public sealed record AgentHistoryAppended(AiMessage Message) : AgentEvent;
public sealed record AgentToolStarted(ToolCall Call) : AgentEvent;
public sealed record AgentToolFinished(ToolCall Call, ToolCallOutput Result) : AgentEvent;
/// <summary>Secrets were masked in a tool result before it went to the provider.</summary>
public sealed record AgentRedacted(int Count) : AgentEvent;
public sealed record AgentFinished(AgentStop Stop, string? Detail, AiErrorKind ErrorKind, int Steps) : AgentEvent;

/// <summary>Which conversation / tool call the current tool invocation belongs to (flows into the approval service).</summary>
public sealed record AiToolCallInfo(Guid ConversationId, string ToolCallId, string ToolName);

public static class AiToolCallScope
{
    static readonly AsyncLocal<AiToolCallInfo?> s_current = new();
    public static AiToolCallInfo? Current { get => s_current.Value; internal set => s_current.Value = value; }
}

/// <summary>
/// The agent loop: stream a turn, execute the turn's tool calls through the shared tool layer (validation →
/// policy → approval → execution → audit), append all results as one message, repeat until the model stops, a limit is hit,
/// or the user cancels. History is append-only.
/// </summary>
public sealed class AiAgent(IChatProvider provider, IToolInvoker tools, AgentOptions? options = null)
{
    readonly AgentOptions _options = options ?? new();

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var history = new List<AiMessage>(turn.History);
        if (CompleteDanglingToolCalls(history) is { } repaired)
        {
            history.Add(repaired);
            yield return new AgentHistoryAppended(repaired);
        }
        var user = AiMessage.User(turn.UserText);
        history.Add(user);
        yield return new AgentHistoryAppended(user);

        for (var step = 1; ; step++)
        {
            if (step > _options.MaxSteps)
            {
                yield return new AgentFinished(AgentStop.StepLimit, null, AiErrorKind.None, step - 1);
                yield break;
            }
            var request = new ChatRequest
            {
                Model = turn.Model,
                SystemPrompt = turn.SystemPrompt,
                History = history.ToList(),
                Tools = provider.Capabilities.Tools ? turn.Tools : [],
                Effort = turn.Effort,
                MaxTokens = turn.MaxTokens,
            };
            Stopped? stopped = null;
            await foreach (var ev in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                if (ev is Stopped s) stopped = s;
                yield return new AgentStreamEvent(ev);
            }
            stopped ??= new Stopped(StopReason.Error, "The provider ended the response without a stop event.", null, AiErrorKind.MalformedResponse);

            if (stopped.Message is { } assistant)
            {
                history.Add(assistant);
                yield return new AgentHistoryAppended(assistant);
            }

            switch (stopped.Reason)
            {
                case StopReason.ToolUse when stopped.Message is { ToolCalls.Count: > 0 } withCalls:
                    var results = new List<ToolCallOutput>();
                    await foreach (var ev in ExecuteAsync(turn, withCalls.ToolCalls, results, ct).ConfigureAwait(false))
                        yield return ev;
                    var resultMessage = AiMessage.Results(results);
                    history.Add(resultMessage);
                    yield return new AgentHistoryAppended(resultMessage);
                    continue;

                case StopReason.MaxTokens:
                    // A cut-off turn may hold tool calls with truncated input: never run them, but answer them so the
                    // history stays valid for the next question.
                    if (stopped.Message is { ToolCalls.Count: > 0 } cut)
                    {
                        var skipped = AiMessage.Results([.. cut.ToolCalls.Select(c => new ToolCallOutput(c.Id,
                            "Not executed: the response was cut off (max_tokens) before this call was complete.", true))]);
                        history.Add(skipped);
                        yield return new AgentHistoryAppended(skipped);
                    }
                    yield return new AgentFinished(AgentStop.MaxTokens, stopped.Detail, AiErrorKind.None, step);
                    yield break;

                case StopReason.Refusal:
                    yield return new AgentFinished(AgentStop.Refusal, stopped.Detail, AiErrorKind.None, step);
                    yield break;

                case StopReason.Error:
                    yield return new AgentFinished(AgentStop.Error, stopped.Detail, stopped.ErrorKind, step);
                    yield break;

                default:
                    yield return new AgentFinished(AgentStop.Completed, stopped.Detail, AiErrorKind.None, step);
                    yield break;
            }
        }
    }

    /// <summary>All calls of one turn run concurrently (approvals queue in the UI); results keep the call order.</summary>
    async IAsyncEnumerable<AgentEvent> ExecuteAsync(AgentTurn turn, IReadOnlyList<ToolCall> calls, List<ToolCallOutput> results,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var events = Channel.CreateUnbounded<AgentEvent>();
        var slots = new ToolCallOutput[calls.Count];
        var tasks = calls.Select((call, i) => Task.Run(async () =>
        {
            await events.Writer.WriteAsync(new AgentToolStarted(call), ct).ConfigureAwait(false);
            var (result, redactions) = await RunOneAsync(turn, call, ct).ConfigureAwait(false);
            slots[i] = result;
            if (redactions > 0) await events.Writer.WriteAsync(new AgentRedacted(redactions), ct).ConfigureAwait(false);
            await events.Writer.WriteAsync(new AgentToolFinished(call, result), ct).ConfigureAwait(false);
        }, ct)).ToArray();
        var all = Task.WhenAll(tasks);
        _ = all.ContinueWith(t => events.Writer.TryComplete(t.Exception?.GetBaseException()), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await foreach (var ev in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            yield return ev;
        await all.ConfigureAwait(false); // rethrows cancellation / unexpected errors
        results.AddRange(slots);
    }

    async Task<(ToolCallOutput Result, int Redactions)> RunOneAsync(AgentTurn turn, ToolCall call, CancellationToken ct)
    {
        if (call.InputError is not null || call.Input is not { } input)
        {
            // Invalid / truncated JSON from eager input streaming: never executed, returned so the model can retry.
            var invalid = new JsonObject { ["INVALID_JSON"] = call.RawInput }.ToJsonString();
            return (new ToolCallOutput(call.Id, invalid, true), 0);
        }
        AiToolCallScope.Current = new AiToolCallInfo(turn.ConversationId, call.Id, call.Name);
        Washi.Tools.ToolResult raw;
        try
        {
            raw = await tools.InvokeAsync(call.Name, input, turn.Actor, ct).ConfigureAwait(false);
        }
        finally
        {
            AiToolCallScope.Current = null;
        }
        var text = raw.Text;
        if (text.Length > _options.MaxToolOutputChars)
            text = text[.._options.MaxToolOutputChars] + $"\n[truncated: {text.Length - _options.MaxToolOutputChars} more characters]";
        var redactions = 0;
        if (_options.RedactSecrets)
        {
            var r = Redactor.Redact(text);
            text = r.Text;
            redactions = r.Count;
        }
        IReadOnlyList<AiImage>? images = null;
        if (raw.Images is { Count: > 0 } imgs)
        {
            if (provider.Capabilities.Images) images = [.. imgs.Select(i => new AiImage(i.MediaType, i.Data))];
            else text += "\n[The page image is not shown: this model takes text only. Use extract_text instead.]";
        }
        return (new ToolCallOutput(call.Id, text, raw.IsError, images), redactions);
    }

    /// <summary>
    /// A cancelled or crashed turn can leave an assistant message whose tool calls never got results; the next request
    /// would be rejected. Returns the error results to append, or null when the history is consistent.
    /// </summary>
    public static AiMessage? CompleteDanglingToolCalls(IReadOnlyList<AiMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || history[^1] is not { Role: AiRole.Assistant, ToolCalls.Count: > 0 } last) return null;
        return AiMessage.Results([.. last.ToolCalls.Select(c => new ToolCallOutput(c.Id, "Not executed: the user stopped this turn.", true))]);
    }

    /// <summary>Audit actor for the tool layer: "ai:&lt;provider&gt;/&lt;model&gt;".</summary>
    public static ToolActor Actor(AiProviderConfig provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new ToolActor(ToolOrigin.Ai, $"ai:{provider.RawProviderId}/{model}", $"{provider.Name} ({model})");
    }
}
