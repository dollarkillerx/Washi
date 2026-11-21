<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/wordmark-on-dark.svg">
    <img src="assets/brand/wordmark.svg" alt="Washi 和紙" height="72">
  </picture>
</p>

# Washi 和紙

Open-source PDF toolkit for desktop, CLI and AI agents. It auto-detects form
fields, fills them, and can OCR, redact and merge PDFs. It has built-in AI
(Claude, GPT, DeepSeek or a local model) and an MCP server. Written in C# /
Avalonia.

> **Status: pre-alpha.** `washi form make` turns a flat PDF into a fillable one:
> it detects the fields (Masume) and writes a real AcroForm. The MCP server, the
> desktop app and the AI features are next. The Python reference implementation
> lives in [`prototype/`](prototype/).

## Why

Many PDF forms are "flat": printed tables with no fillable fields, sometimes
with prefilled data that is wrong. Making them fillable by hand means drawing
every field. Washi's **Masume** engine (マス目, the grid of boxes on a form)
finds the cells itself and suggests the fields. A person then reviews them.

Each 国税庁 年末調整 form below ships with fields made by hand. `washi masume
eval` ignores them, detects fields on the flat page, then compares:

| form | real fields | detected | recall (C#) | precision (C#) | Python prototype |
|---|---|---|---|---|---|
| 扶養控除等(異動)申告書 R9 | 193 | 130 | 78% | 100% | 78% / 100% |
| 保険料控除申告書 R8 | 143 | 135 | 84% | 87% | 84% / 90% |
| 基礎・配偶者・特定親族・所得金額調整 R8 | 83 | 78 | 83% | 88% | 83% / 90% |

- **Recall**: the share of hand-made fields whose centre is covered by a detected field.
- **Precision**: the share of detected fields that sit on a hand-made field.

On 保険料控除, C# and Python detect the same 135 fields: same
positions, same kinds. On the other two forms, PDFium's character boxes let the C# port find
a few more □ checkboxes. Each form takes 1–2 s. Method: [`prototype/README.md`](prototype/README.md).

## Making a form fillable

```bash
washi form make form.pdf -o form_fillable.pdf            # detect fields and write them
washi masume detect form.pdf -o out                      # or review first: out/form_fields.json
washi form make form.pdf --fields out/form_fields.json   # ...then write the reviewed fields
```

- **Field types**:
  - text
  - amount (right-aligned)
  - comb (`MaxLen`, one character per box, e.g. マイナンバー)
  - checkbox
- **Rotated pages** are straightened first, so fields and typed text stay upright in every viewer.
- **Form language** (`--lang auto|ja|zh-Hans|zh-Hant|ko|en|fr|de`) picks the font that
  viewers use for typed text. Each script needs its own font: a Japanese font has no Hangul,
  and the two Chinese standards differ.

  | language | font requested |
  |---|---|
  | ja | KozMinPr6N |
  | zh-Hans | STSong |
  | zh-Hant | MSung |
  | ko | HYSMyeongJo |
  | en / fr / de | Helvetica |

  `auto` reads the page text. If the text is unreadable, it uses your system language.
- **Protected PDFs** (an owner password forbids changes) are refused. Washi does not remove protection.

The fonts are referenced, not embedded, the same way the 国税庁 forms do it, so
files stay small. **Known limit:** viewers based on PDFium (Chrome, Edge) substitute
a system font and can drop rare CJK characters (e.g. 燁, 陳, 臺 in our tests on
macOS). Adobe Acrobat renders them. An option to embed a full font is planned.

## Languages

The interface speaks English, 日本語, 简体中文, 繁體中文, 한국어, Français and Deutsch. It
follows your system language. Set `WASHI_LANG` (e.g. `ja`, `zh-Hant`, `de`) to change it.
Strings live in [`src/Washi.Core/Localization/strings.py`](src/Washi.Core/Localization/strings.py),
which generates the `.resx` files. A test checks that every language has every string
with the same placeholders.

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
| `Washi.Core` | Reading and rendering PDFs (PDFium via PDFiumCore), writing AcroForms (PDFsharp 6.2, MIT), UI strings in 7 languages. Shared geometry (`PdfRect`, top-down points on the upright page), page snapshots, a small PNG writer |
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
dotnet run --project src/Washi.Cli -- masume detect form.pdf -o out   # fields.json + overlay PNGs
dotnet run --project src/Washi.Cli -- masume eval form.pdf -o out     # score against existing fields
dotnet run --project src/Washi.App
```

## Roadmap

1. ~~Port Masume to C# (PDFium render + text layer, managed connected components) and reach the prototype's scores.~~ Done.
2. ~~Write the AcroForm output (text, amount, comb, checkbox; fonts per language; rotated pages).~~ Done.
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
OpenCvSharp, the Anthropic, OpenAI and MCP SDKs) are compatible with this
licence. AGPL engines such as MuPDF can be used too.

PDFsharp is pinned to **6.2.4, the last MIT release**. Version 6.5 is dual
licensed (AGPL-3.0-only or commercial) and must be activated in code. Moving to
6.5 is a one-line change, but it would make a future commercial licence of
Washi depend on a PDFsharp licence as well.

The brand assets in [`assets/brand/`](assets/brand/) are part of this repository. Their lettering
is outlined from IBM Plex (SIL Open Font License 1.1).
