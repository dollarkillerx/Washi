# Third-party notices

Washi is licensed under the GNU Affero General Public License v3.0 or later
([`LICENSE`](LICENSE)). It is built on the open-source projects below. Each one
keeps its own licence. The licence texts come with each package (NuGet) or project.

## Shipped with the app, CLI and MCP server

| component | licence | project |
|---|---|---|
| .NET runtime and libraries (incl. System.Security.Cryptography.Pkcs) | MIT | https://github.com/dotnet/runtime |
| Avalonia (UI framework) | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter (UI font, via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| PDFium (PDF rendering, text, forms) | BSD-3-Clause, Apache-2.0 | https://pdfium.googlesource.com/pdfium/ |
| pdfium-binaries (prebuilt PDFium) | Apache-2.0 | https://github.com/bblanchon/pdfium-binaries |
| PDFiumCore (.NET bindings) | Apache-2.0 | https://github.com/Dtronix/PDFiumCore |
| PDFsharp 6.2.4 (PDF writing). Later versions are under other terms, so Washi pins 6.2.4 | MIT | https://github.com/empira/PDFsharp |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Anthropic C# SDK | Apache-2.0 | https://github.com/anthropics/anthropic-sdk-csharp |
| OpenAI .NET SDK | MIT | https://github.com/openai/openai-dotnet |
| Model Context Protocol C# SDK (MCP server only) | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |

## Used if installed, not shipped

| component | licence | project |
|---|---|---|
| Tesseract OCR | Apache-2.0 | https://github.com/tesseract-ocr/tesseract |
| Apple Vision (OCR on macOS) | part of macOS | https://developer.apple.com/documentation/vision |

## Brand assets

The lettering in `assets/brand` is IBM Plex Sans and IBM Plex Sans JP (SIL Open Font
License 1.1), outlined to paths. The font files are not part of this repository.
