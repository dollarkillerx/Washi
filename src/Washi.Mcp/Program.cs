// SPDX-License-Identifier: AGPL-3.0-or-later
// washi-mcp: MCP server (stdio) exposing the Washi.Tools registry.
//
//   washi-mcp --allow ~/Documents/forms [--allow <dir> ...]
//
// Only files inside the allowed folders can be read or written (fonts may also come from the
// system font folders). Write tools create new files and never overwrite. Without --allow
// (or WASHI_ALLOWED_DIRS), the current folder is the only allowed folder.
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Washi.Tools;

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
if (args is ["--version"]) { Console.WriteLine($"washi-mcp {version}"); return 0; }

var roots = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--allow" && i + 1 < args.Length) roots.Add(args[++i]);
    else { Console.Error.WriteLine($"washi-mcp: unknown argument '{args[i]}'. Usage: washi-mcp --allow <folder> [--allow <folder> ...]"); return 2; }
}
if (Environment.GetEnvironmentVariable("WASHI_ALLOWED_DIRS") is { Length: > 0 } env)
    roots.AddRange(env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
if (roots.Count == 0) roots.Add(Directory.GetCurrentDirectory());

PathPolicy policy;
try { policy = new PathPolicy(roots); }
catch (ArgumentException) { Console.Error.WriteLine($"washi-mcp: none of the allowed folders exist: {string.Join(", ", roots)}"); return 2; }
var registry = WashiTools.CreateRegistry(policy);

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
// stdout carries the protocol: every log line goes to stderr
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "washi", Version = version };
        o.ServerInstructions =
            "Washi works on PDF files inside these folders: " + string.Join(", ", policy.Roots) + ". " +
            "Typical flow: pdf_info -> (flat form) detect_fields / make_fillable -> list_fields -> fill_fields -> render_page to check. " +
            "Pages are 1-based in tool arguments. Write tools create new files and never overwrite.";
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
    {
        Tools = [.. registry.All.Select(t => new Tool
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
        })],
    }))
    .WithCallToolHandler(async (request, ct) =>
    {
        var name = request.Params?.Name ?? "";
        var log = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("washi");
        if (registry.Find(name) is not { } tool)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"unknown tool '{name}'" }] };

        var arguments = JsonSerializer.SerializeToElement(
            request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var sw = Stopwatch.StartNew();
        var result = await tool.InvokeAsync(arguments, ct);
        log?.LogInformation("{Tool} {Status} in {Ms} ms", name, result.IsError ? "failed" : "ok", sw.ElapsedMilliseconds);

        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
        foreach (var img in result.Images ?? [])
            content.Add(ImageContentBlock.FromBytes(img.Data, img.MediaType));
        return new CallToolResult { Content = content, IsError = result.IsError };
    });

await builder.Build().RunAsync();
return 0;
