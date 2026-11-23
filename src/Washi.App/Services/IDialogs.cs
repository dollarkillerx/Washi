// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Washi.Core.Localization;

namespace Washi.App.Services;

/// <summary>File pickers, behind an interface so tests can answer them.</summary>
public interface IDialogs
{
    Task<string?> OpenPdfAsync();
    Task<IReadOnlyList<string>> OpenPdfsAsync();
    Task<string?> PickFolderAsync();
    Task<string?> OpenFieldsAsync();
    Task<string?> SaveFieldsAsync(string suggestedName);
    Task<string?> SavePdfAsync(string suggestedName);
    Task<string?> OpenFontAsync();
    /// <summary>A digital ID file (.p12 / .pfx).</summary>
    Task<string?> OpenDigitalIdAsync();
    Task<string?> SaveDigitalIdAsync(string suggestedName);
    /// <summary>Yes / no question; true only when the user chose the confirming button.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel);
}

public sealed class AvaloniaDialogs(TopLevel owner) : IDialogs
{
    private static FilePickerFileType Pdf => new(L.Get("Ui_PdfFiles")) { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] };
    private static FilePickerFileType Images => new(L.Get("Ui_ImageFiles"))
    {
        Patterns = [.. Washi.Core.Pages.PdfFromImages.Extensions.Select(e => "*" + e)],
        MimeTypes = ["image/jpeg", "image/png", "image/webp", "image/bmp", "image/gif"],
    };
    private static FilePickerFileType PdfOrImages => new(L.Get("Ui_PdfOrImageFiles"))
    {
        Patterns = ["*.pdf", .. Washi.Core.Pages.PdfFromImages.Extensions.Select(e => "*" + e)],
    };
    private static FilePickerFileType Json => new(L.Get("Ui_JsonFiles")) { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private static FilePickerFileType Fonts => new(L.Get("Ui_FontFiles")) { Patterns = ["*.ttf", "*.otf", "*.ttc", "*.otc"] };
    private static FilePickerFileType Ids => new(L.Get("Ui_IdFiles")) { Patterns = ["*.p12", "*.pfx"], MimeTypes = ["application/x-pkcs12"] };

    public Task<string?> OpenPdfAsync() => Open(Pdf);

    public async Task<IReadOnlyList<string>> OpenPdfsAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [PdfOrImages, Pdf, Images], AllowMultiple = true });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickFolderAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
    public Task<string?> OpenFieldsAsync() => Open(Json);
    public Task<string?> OpenFontAsync() => Open(Fonts);
    public Task<string?> OpenDigitalIdAsync() => Open(Ids);
    public Task<string?> SaveDigitalIdAsync(string suggestedName) => Save(Ids, suggestedName, "p12");
    public Task<string?> SaveFieldsAsync(string suggestedName) => Save(Json, suggestedName, "json");
    public Task<string?> SavePdfAsync(string suggestedName) => Save(Pdf, suggestedName, "pdf");

    public async Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel)
    {
        var yes = new Button { Content = confirm, Classes = { "accent" }, IsDefault = true };
        var no = new Button { Content = cancel, IsCancel = true };
        var dialog = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                                     HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } },
                },
            },
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return owner is Window w && await dialog.ShowDialog<bool>(w);
    }

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
