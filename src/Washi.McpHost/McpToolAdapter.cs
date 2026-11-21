// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH (same authors, AGPL-3.0-or-later).
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Washi.Tools;

namespace Washi.McpHost;

/// <summary>
/// Publishes the shared tool layer as MCP tools through the SDK's handler API and routes every call to
/// <see cref="IToolInvoker"/>, which owns folder policy, approvals and the call log.
/// </summary>
internal sealed partial class McpToolAdapter(IToolInvoker invoker, ClientTracker tracker, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Server options for one MCP server: the HTTP endpoint (<paramref name="local"/> null) or one local connection.</summary>
    public void Configure(McpServerOptions options, McpHostOptions host, ClientTracker.Entry? local)
    {
        options.ServerInfo = new Implementation { Name = host.ServerName, Version = host.ServerVersion };
        options.ServerInstructions = host.Instructions;
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();

        var h = options.Handlers;
        h.ListToolsHandler = (ctx, ct) => { Client(ctx, local); return ValueTask.FromResult(ListTools()); };
        h.CallToolHandler = (ctx, ct) => CallToolAsync(ctx, local, ct);
    }

    internal ListToolsResult ListTools() => new()
    {
        Tools = invoker.Tools.Select(t => new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = t.Risk == ToolRisk.ReadOnly,
                DestructiveHint = t.Risk == ToolRisk.Destructive,
                IdempotentHint = t.Risk == ToolRisk.ReadOnly,
                OpenWorldHint = false,
            },
        }).ToList(),
    };

    private async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> ctx, ClientTracker.Entry? local, CancellationToken ct)
    {
        var p = ctx.Params ?? throw new McpProtocolException("missing params", McpErrorCode.InvalidParams);
        if (!invoker.Tools.Any(t => t.Name == p.Name))
            throw new McpProtocolException($"Unknown tool: '{p.Name}'", McpErrorCode.InvalidParams);
        var (entry, actor) = Client(ctx, local);
        using var _ = tracker.BeginCall(entry);
        ToolResult result;
        try
        {
            result = await invoker.InvokeAsync(p.Name, ToObject(p.Arguments), actor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // the client cancelled or went away; the SDK reports it
        }
        catch (Exception ex)
        {
            // The invoker reports tool failures as IsError; anything thrown is a bug. Don't leak details to the client.
            LogInvokerFailure(_logger, ex, p.Name, actor.Label);
            result = ToolResult.Error($"internal error while running {p.Name}");
        }
        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
        foreach (var img in result.Images ?? [])
            content.Add(ImageContentBlock.FromBytes(img.Data, img.MediaType));
        return new CallToolResult { IsError = result.IsError, Content = content };
    }

    // ------------------------------------------------------------------ clients

    private (ClientTracker.Entry Entry, ToolActor Actor) Client<T>(RequestContext<T> ctx, ClientTracker.Entry? local)
    {
        // Newer clients send clientInfo in every request's _meta; older ones only in initialize (absent in stateless HTTP).
        var info = ctx.JsonRpcRequest.Context?.ClientInfo ?? ctx.Server.ClientInfo;
        var name = McpActors.SanitizeName(info?.Name);
        var version = McpActors.SanitizeName(info?.Version);
        ClientTracker.Entry entry;
        if (local is not null)
        {
            entry = local;
            tracker.Touch(entry, name, version);
            name = entry.Name; // keep the name learnt at initialize for later requests
        }
        else
        {
            entry = tracker.Http(name, version);
            tracker.Touch(entry, name, version);
        }
        return (entry, McpActors.Create(entry.Transport, name));
    }

    // ------------------------------------------------------------------ JSON helpers (no reflection-based serialization)

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    internal static JsonElement ToObject(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return EmptyObject;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                w.WritePropertyName(key);
                value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP tool invoker threw for {Tool} ({Actor})")]
    private static partial void LogInvokerFailure(ILogger logger, Exception ex, string tool, string actor);
}
