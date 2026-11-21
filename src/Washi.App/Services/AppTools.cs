// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Washi.Tools;

namespace Washi.App.Services;

/// <summary>What the editor shows right now (for the assistant's context and get_open_document).</summary>
/// <param name="Page">1-based.</param>
public sealed record DocumentSnapshot(string Path, int Page, int PageCount, int FieldCount);

/// <summary>The window side of the app tools: implemented by the main window's view model.</summary>
public interface IEditorHost
{
    /// <summary>Safe from any thread.</summary>
    DocumentSnapshot? Snapshot { get; }

    /// <summary>Opens a PDF in the editor (marshals to the UI thread). Page is 1-based, null keeps the first page.</summary>
    Task OpenAsync(string path, int? page);
}

/// <summary>Tools only the desktop app has: they see and drive the Washi window. Offered to the assistant and MCP.</summary>
public static class AppTools
{
    public static IEnumerable<IWashiTool> Create(PathPolicy policy, Func<IEditorHost?> editor) =>
        [new GetOpenDocumentTool(editor), new OpenDocumentTool(policy, editor)];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class GetOpenDocumentTool(Func<IEditorHost?> editor) : IWashiTool
    {
        public string Name => "get_open_document";
        public string Description => "The PDF open in the Washi window right now: its path, the page on screen (1-based), the page " +
                                     "count and how many form fields the editor holds. Use the path with the other tools.";
        public ToolRisk Risk => ToolRisk.ReadOnly;
        public JsonElement InputSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(editor()?.Snapshot is { } s
                ? new ToolResult(JsonSerializer.Serialize(new { path = s.Path, page = s.Page, pages = s.PageCount, fields_in_editor = s.FieldCount }, Json))
                : new ToolResult("No document is open in Washi."));
    }

    private sealed class OpenDocumentTool(PathPolicy policy, Func<IEditorHost?> editor) : IWashiTool
    {
        public string Name => "open_document";
        public string Description => "Shows a PDF in the Washi window (for example a file you just wrote), optionally at a page. " +
                                     "It replaces the document the user has open, so it may need the user's approval.";
        // replaces what the user has open (and any unsaved field edits): treat it like a write
        public ToolRisk Risk => ToolRisk.Write;
        public JsonElement InputSchema { get; } = Schema("""
            {"type":"object","properties":{
              "path":{"type":"string","description":"PDF file"},
              "page":{"type":"integer","minimum":1,"description":"1-based page to show"}},
             "required":["path"],"additionalProperties":false}
            """);

        public async Task<ToolResult> InvokeAsync(JsonElement a, CancellationToken cancellationToken = default)
        {
            if (editor() is not { } host) return ToolResult.Error("the Washi window is not open");
            string path;
            try
            {
                path = policy.Input(a.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "");
            }
            catch (ToolException e) { return ToolResult.Error(e.Message); }
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return ToolResult.Error("only PDF files can be opened");
            int? page = a.TryGetProperty("page", out var pg) && pg.TryGetInt32(out var n) ? n : null;
            await host.OpenAsync(path, page).ConfigureAwait(false);
            return new ToolResult($"Opened {path} in Washi" + (page is { } x ? $" at page {x}." : "."));
        }
    }
}
