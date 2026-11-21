// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Washi.Core.Localization;

namespace Washi.App.Services;

/// <summary>File pickers, behind an interface so tests can answer them.</summary>
public interface IDialogs
{
    Task<string?> OpenPdfAsync();
    Task<string?> OpenFieldsAsync();
    Task<string?> SaveFieldsAsync(string suggestedName);
    Task<string?> SavePdfAsync(string suggestedName);
    Task<string?> OpenFontAsync();
}

public sealed class AvaloniaDialogs(TopLevel owner) : IDialogs
{
    private static FilePickerFileType Pdf => new(L.Get("Ui_PdfFiles")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };
    private static FilePickerFileType Json => new(L.Get("Ui_JsonFiles")) { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private static FilePickerFileType Fonts => new(L.Get("Ui_FontFiles")) { Patterns = ["*.ttf", "*.otf", "*.ttc", "*.otc"] };

    public Task<string?> OpenPdfAsync() => Open(Pdf);
    public Task<string?> OpenFieldsAsync() => Open(Json);
    public Task<string?> OpenFontAsync() => Open(Fonts);
    public Task<string?> SaveFieldsAsync(string suggestedName) => Save(Json, suggestedName, "json");
    public Task<string?> SavePdfAsync(string suggestedName) => Save(Pdf, suggestedName, "pdf");

    private async Task<string?> Open(FilePickerFileType type)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [type], AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> Save(FilePickerFileType type, string suggested, string ext)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggested, DefaultExtension = ext, FileTypeChoices = [type], ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
