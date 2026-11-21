// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Washi.Ai;
using Washi.App.Services;
using Washi.Core.Localization;

namespace Washi.App.ViewModels;

/// <summary>An open-source component Washi uses, shown in About.</summary>
public sealed record Component(string Name, string License, string Url, string? Note = null);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string? _settingsPath;

    public SettingsViewModel(string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        var saved = AppSettings.Load(settingsPath).Language ?? "";
        _language = Languages.FirstOrDefault(l => l.Value == saved) ?? Languages[0];
        try
        {
            var ai = AiSettings.Load();
            AiModel = $"{ai.Model} ({ai.Provider})";
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { AiModel = "-"; }
    }

    /// <summary>Raised after the language was saved; the app rebuilds its window in that language.</summary>
    public event Action? LanguageChanged;

    public static IReadOnlyList<Choice<string>> Languages =>
    [
        new("", L.Get("Ui_LangSystem")), new("ja", "日本語"), new("zh-Hans", "简体中文"), new("zh-Hant", "繁體中文"),
        new("ko", "한국어"), new("en", "English"), new("fr", "Français"), new("de", "Deutsch"),
    ];

    [ObservableProperty] private Choice<string> _language;

    partial void OnLanguageChanged(Choice<string> value)
    {
        var settings = AppSettings.Load(_settingsPath) with { Language = value.Value.Length == 0 ? null : value.Value };
        settings.Save(_settingsPath);
        AppSettings.ApplyLanguage(settings);
        LanguageChanged?.Invoke();
    }

    public string AiModel { get; }

    public string Version => L.F("Ui_Version",
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    public const string SourceUrl = "https://github.com/dollarkillerx/Washi";
    public const string LicenseUrl = "https://github.com/dollarkillerx/Washi/blob/main/LICENSE";

    /// <summary>What ships in Washi, with each project's own licence (see THIRD_PARTY_NOTICES.md).</summary>
    public static IReadOnlyList<Component> Components =>
    [
        new(".NET", "MIT", "https://github.com/dotnet/runtime"),
        new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("PDFium", "BSD-3-Clause, Apache-2.0", "https://pdfium.googlesource.com/pdfium/"),
        new("pdfium-binaries", "Apache-2.0", "https://github.com/bblanchon/pdfium-binaries"),
        new("PDFiumCore", "Apache-2.0", "https://github.com/Dtronix/PDFiumCore"),
        new("PDFsharp 6.2", "MIT", "https://github.com/empira/PDFsharp"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Anthropic C# SDK", "Apache-2.0", "https://github.com/anthropics/anthropic-sdk-csharp"),
        new("OpenAI .NET SDK", "MIT", "https://github.com/openai/openai-dotnet"),
        new("Inter", "SIL OFL 1.1", "https://github.com/rsms/inter"),
        new("Tesseract", "Apache-2.0", "https://github.com/tesseract-ocr/tesseract", L.Get("Ui_NotBundled")),
    ];
}
