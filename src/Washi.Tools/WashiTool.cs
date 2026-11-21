// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Washi.Tools;

/// <summary>
/// How much a tool can change. The built-in AI and the MCP server read this to decide
/// whether a call may run on its own or needs the user's confirmation first.
/// </summary>
public enum ToolRisk
{
    /// <summary>Reads only (info, extract text, render a page, detect fields).</summary>
    ReadOnly,
    /// <summary>Writes a new output file; the input is left untouched.</summary>
    Write,
    /// <summary>Cannot be undone (overwrite in place, permanent redaction).</summary>
    Destructive,
}

/// <summary>
/// One PDF operation, defined once and exposed through every front end:
/// the CLI, the built-in AI (as a function the model can call) and the MCP server.
/// </summary>
public interface IWashiTool
{
    /// <summary>Stable snake_case name; it becomes the MCP tool name.</summary>
    string Name { get; }
    string Description { get; }
    ToolRisk Risk { get; }
    /// <summary>JSON Schema (object) of the arguments.</summary>
    JsonElement InputSchema { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed record ToolImage(string MediaType, byte[] Data);

public sealed record ToolResult(string Text, IReadOnlyList<ToolImage>? Images = null, bool IsError = false)
{
    public static ToolResult Error(string message) => new(message, IsError: true);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IWashiTool> _tools = new(StringComparer.Ordinal);

    public ToolRegistry Add(IWashiTool tool)
    {
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"tool '{tool.Name}' is already registered");
        return this;
    }

    public IWashiTool? Find(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyCollection<IWashiTool> All => _tools.Values;
}
