// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace Washi.Tools;

// The shared tool layer of the desktop app. The built-in AI and the in-app MCP server both call tools
// through IToolInvoker; neither builds a ToolRegistry itself. The app implements IApprovalService.

/// <summary>Who is calling a tool.</summary>
public enum ToolOrigin { Ai, Mcp }

/// <param name="Label">Log label, "ai:anthropic/claude-opus-5-5" or "mcp:claude-code".</param>
/// <param name="DisplayName">Shown in approval prompts, e.g. "Claude Code (MCP)".</param>
public sealed record ToolActor(ToolOrigin Origin, string Label, string DisplayName);

/// <summary>What AI and MCP callers may do with tools that write files.</summary>
public enum WriteAccess
{
    /// <summary>Read-only: write tools are refused.</summary>
    Deny,
    /// <summary>Every write waits for the user's approval in the Washi window (default).</summary>
    Ask,
    /// <summary>Writes run on their own. They still only create new files inside the allowed folders.</summary>
    Allow,
}

/// <param name="Folders">Folders the tools may read and write in.</param>
/// <param name="IncludeOpenDocumentFolder">Also allow the folder of the PDF open in the editor.</param>
public sealed record ToolAccessSettings(IReadOnlyList<string> Folders, WriteAccess Writes = WriteAccess.Ask, bool IncludeOpenDocumentFolder = true);

public enum ApprovalOutcome { Approved, Denied, TimedOut, Cancelled }

/// <param name="Summary">One line, e.g. "fill_fields: form.pdf → form_filled.pdf".</param>
/// <param name="Details">The arguments, for a closer look.</param>
public sealed record ApprovalRequest(
    Guid Id, ToolActor Actor, string ToolName, ToolRisk Risk, string Summary, string? Details, DateTimeOffset Deadline);

/// <summary>
/// Implemented by the GUI. Must not block the calling thread on the UI thread, must queue concurrent
/// requests and must return <see cref="ApprovalOutcome.Cancelled"/> when the token fires (the invoker adds the deadline).
/// </summary>
public interface IApprovalService
{
    Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Approves nothing: write tools are refused (headless use, tests).</summary>
public sealed class DenyAllApprovals : IApprovalService
{
    public static readonly DenyAllApprovals Instance = new();
    public Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(ApprovalOutcome.Denied);
}

/// <summary>The single entry point for AI and MCP: find → policy → approval → run → log.</summary>
public interface IToolInvoker
{
    /// <summary>The tools on offer (same set for every caller), sorted by name.</summary>
    IReadOnlyList<IWashiTool> Tools { get; }

    /// <summary>Never throws for tool failures (they come back as <see cref="ToolResult.IsError"/>);
    /// throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.</summary>
    Task<ToolResult> InvokeAsync(string name, JsonElement arguments, ToolActor actor, CancellationToken ct = default);
}

public sealed class ToolInvoker : IToolInvoker
{
    private readonly Func<ToolAccessSettings> _settings;
    private readonly Func<string?> _openDocument;
    private readonly IApprovalService _approvals;
    private readonly Func<PathPolicy, IEnumerable<IWashiTool>>? _extraTools;
    private readonly string? _logPath;
    private readonly Lock _logLock = new();

    /// <param name="settings">Read on every call, so changes in Settings apply at once.</param>
    /// <param name="openDocument">Path of the PDF open in the editor, if any.</param>
    /// <param name="extraTools">App tools (open document, ...), bound to the same folder policy.</param>
    /// <param name="logPath">One JSON line per call (tool, caller, decision, duration; no argument values). Null: no log.</param>
    public ToolInvoker(Func<ToolAccessSettings> settings, IApprovalService approvals, Func<string?>? openDocument = null,
        Func<PathPolicy, IEnumerable<IWashiTool>>? extraTools = null, string? logPath = null)
    {
        _settings = settings;
        _approvals = approvals;
        _openDocument = openDocument ?? (() => null);
        _extraTools = extraTools;
        _logPath = logPath;
    }

    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The folders tools may use right now.</summary>
    public PathPolicy CurrentPolicy()
    {
        var s = _settings();
        var roots = new List<string>(s.Folders);
        if (s.IncludeOpenDocumentFolder && _openDocument() is { } doc && Path.GetDirectoryName(doc) is { Length: > 0 } dir) roots.Add(dir);
        return new PathPolicy(roots, allowEmpty: true);
    }

    private ToolRegistry Registry(PathPolicy policy)
    {
        var registry = WashiTools.CreateRegistry(policy);
        foreach (var t in _extraTools?.Invoke(policy) ?? []) registry.Add(t);
        return registry;
    }

    public IReadOnlyList<IWashiTool> Tools => [.. Registry(CurrentPolicy()).All.OrderBy(t => t.Name, StringComparer.Ordinal)];

    public async Task<ToolResult> InvokeAsync(string name, JsonElement arguments, ToolActor actor, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var settings = _settings();
        var tool = Registry(CurrentPolicy()).Find(name);
        if (tool is null)
        {
            Log(actor, name, "unknown", sw, null);
            return ToolResult.Error($"unknown tool '{name}'");
        }

        if (tool.Risk != ToolRisk.ReadOnly)
        {
            if (settings.Writes == WriteAccess.Deny)
            {
                Log(actor, name, "denied-by-policy", sw, null);
                return ToolResult.Error("Washi's AI access is read-only: tools that write files are not allowed. " +
                                        "Ask the user to allow writes in Washi's settings (AI & MCP).");
            }
            if (settings.Writes == WriteAccess.Ask || tool.Risk == ToolRisk.Destructive)
            {
                var outcome = await AskAsync(tool, arguments, actor, ct).ConfigureAwait(false);
                if (outcome != ApprovalOutcome.Approved)
                {
                    Log(actor, name, outcome.ToString().ToLowerInvariant(), sw, null);
                    ct.ThrowIfCancellationRequested();
                    return ToolResult.Error(outcome == ApprovalOutcome.TimedOut
                        ? "The user did not answer the approval request in time. Do not retry it in a loop; tell the user what you wanted to do."
                        : "The user declined this call. Treat it as final: do not retry it in another form; tell the user what you wanted to do and why.");
                }
            }
        }

        ToolResult result;
        try
        {
            result = await tool.InvokeAsync(arguments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log(actor, name, "cancelled", sw, null);
            throw;
        }
#pragma warning disable CA1031 // a tool call must never crash the MCP server or the agent loop
        catch (Exception e)
#pragma warning restore CA1031
        {
            result = ToolResult.Error($"internal error while running {name}: {e.GetType().Name}: {e.Message}");
        }
        Log(actor, name, result.IsError ? "failed" : "ok", sw, result.IsError ? result.Text : null);
        return result;
    }

    private async Task<ApprovalOutcome> AskAsync(IWashiTool tool, JsonElement arguments, ToolActor actor, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApprovalTimeout);
        var request = new ApprovalRequest(Guid.NewGuid(), actor, tool.Name, tool.Risk, Summarize(tool.Name, arguments),
            arguments.ValueKind == JsonValueKind.Object ? JsonSerializer.Serialize(arguments, WashiTools.Json) : null,
            DateTimeOffset.UtcNow + ApprovalTimeout);
        try
        {
            var outcome = await _approvals.RequestAsync(request, timeout.Token).ConfigureAwait(false);
            if (outcome == ApprovalOutcome.Cancelled && !ct.IsCancellationRequested) return ApprovalOutcome.TimedOut;
            return outcome;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApprovalOutcome.TimedOut;
        }
    }

    /// <summary>"fill_fields: form.pdf → form_filled.pdf" (the default output when none is given).</summary>
    public static string Summarize(string tool, JsonElement a)
    {
        static string? S(JsonElement a, string k) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var inputs = new List<string>();
        if (S(a, "path") is { } p) inputs.Add(p);
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
            inputs.AddRange(arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        // organize_pages: pages inserted from other files are inputs too
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            inputs.AddRange(pages.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => S(x, "path"))
                                 .OfType<string>().Where(x => !inputs.Contains(x)).Distinct());
        var output = S(a, "output") ?? S(a, "output_dir");
        if (output is null && inputs.Count > 0)
        {
            var suffix = tool switch
            {
                "make_fillable" => "_fillable.pdf", "fill_fields" => "_filled.pdf", "merge_pdfs" => "_merged.pdf",
                "split_pdf" => "_split/", "organize_pages" => "_organized.pdf", "add_comments" => "_commented.pdf", "ocr_pdf" => "_ocr.pdf", "erase_content" => "_erased.pdf", _ => null,
            };
            if (suffix is not null) output = Path.GetFileNameWithoutExtension(inputs[0]) + suffix;
        }
        var from = string.Join(", ", inputs.Select(Path.GetFileName));
        return output is null ? $"{tool}: {from}" : $"{tool}: {from} → {Path.GetFileName(output.TrimEnd('/')) + (output.EndsWith('/') ? "/" : "")}";
    }

    private void Log(ToolActor actor, string tool, string decision, Stopwatch sw, string? error)
    {
        if (_logPath is null) return;
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow.ToString("O"),
            ["actor"] = actor.Label,
            ["tool"] = tool.Length > 128 ? tool[..128] : tool,
            ["decision"] = decision,
            ["ms"] = sw.ElapsedMilliseconds,
            ["error"] = error is { Length: > 2000 } ? error[..2000] : error,
        });
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, line + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must not change the result */ }
    }
}
