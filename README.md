# Washi 和紙

Open-source PDF toolkit for desktop, CLI and AI agents. It auto-detects form
fields, fills them, and can OCR, redact and merge PDFs. It has built-in AI
(Claude, GPT, DeepSeek or a local model) and an MCP server. Written in C# /
Avalonia.

> **Status: pre-alpha.** The solution layout and the core contracts are in
> place. The form-field detector (Masume) works as a Python reference
> implementation in [`prototype/`](prototype/) and is being ported to C#.

## Why

Many PDF forms are "flat": printed tables with no fillable fields, sometimes
with prefilled data that is wrong. Making them fillable by hand means drawing
every field. Washi's **Masume** engine (マス目, the grid of boxes on a form)
finds the cells itself and suggests the fields. A person then reviews them.

On three official 国税庁 年末調整 forms, the prototype recovers 78–84% of the
hand-made fields, and 90–100% of its suggestions sit on a real field. See
[`prototype/README.md`](prototype/README.md).

## Architecture

Every PDF operation is defined **once**, as a tool in `Washi.Tools`. The CLI,
the built-in AI and the MCP server all call the same tools.

```
Washi.App (Avalonia)    Washi.Cli    Washi.Mcp (MCP server: stdio / HTTP)
        │                   │               │
        └─────────── Washi.Ai (model providers, confirmation before writes)
                            │
                     Washi.Tools  (registry: name + JSON schema + risk level)
                            │
        Washi.Core (PDF I/O, geometry)    Washi.Masume (form-field detection)
```

| project | role |
|---|---|
| `Washi.Core` | PDF reading, rendering and writing. Shared geometry (`PdfRect`, top-down points on the upright page) |
| `Washi.Masume` | form-field detection. `fields.json` has the same shape as the prototype's output |
| `Washi.Tools` | `IWashiTool` + `ToolRegistry`. Each tool has a `ToolRisk` level: `ReadOnly`, `Write` (new file) or `Destructive` (cannot be undone) |
| `Washi.Ai` | providers behind `Microsoft.Extensions.AI`'s `IChatClient`:<br>• **Claude** through the official Anthropic C# SDK<br>• **GPT** through the official OpenAI SDK<br>• **DeepSeek** and **local models** (e.g. Ollama) through OpenAI-compatible endpoints |
| `Washi.Mcp` | MCP server that exposes the tool registry (official C# MCP SDK) |
| `Washi.Cli` | `washi` command line |
| `Washi.App` | Avalonia desktop app: viewer, field review editor, AI panel |

### Privacy rules (by design)

- A document is sent to a cloud model only after the user consents for that document.
- Sensitive numbers (マイナンバー, bank accounts) can be masked before sending.
- With a local model (OpenAI-compatible endpoint on loopback), nothing leaves the machine.
- API keys are kept in the OS keychain, never in plain config files.
- `Write` and `Destructive` tools need the user's confirmation when the AI calls them.
- The MCP server writes copies by default, and only inside folders the user allows.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build Washi.slnx
dotnet test Washi.slnx
dotnet run --project src/Washi.Cli -- --version
dotnet run --project src/Washi.App
```

## Roadmap

1. Port Masume to C# (PDFium render, PdfPig letters, connected components) and reach the prototype's scores.
2. Write the AcroForm output (text, amount, comb, checkbox; Japanese font handling).
3. First tools: `pdf_info`, `extract_text`, `render_page`, `detect_fields`, `fill_fields`.
4. MCP server over the tool registry.
5. Avalonia field review editor.
6. Built-in AI: name and type fields from the page image, fill from a profile, chat with a document.
7. OCR, redaction, merge and split.

## License

Washi is licensed under the **GNU Affero General Public License v3.0 or later**
([`LICENSE`](LICENSE)).

You may use, modify and redistribute it. Anyone who distributes a modified
version, **or runs one as a network service** (for example a hosted MCP server
or a web API), must publish the complete corresponding source under the same
licence.

Dependencies under MIT, BSD and Apache-2.0 (Avalonia, PDFium, PdfPig,
PDFsharp, OpenCvSharp, the Anthropic, OpenAI and MCP SDKs) are compatible with
this licence. AGPL engines such as MuPDF can be used too.
