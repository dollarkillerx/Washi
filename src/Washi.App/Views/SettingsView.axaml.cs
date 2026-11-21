// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Washi.App.ViewModels;

namespace Washi.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.CopyToClipboard = async text =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
                };
        };
    }

    /// <summary>Which tab to show: "general", "ai", "mcp" or "about".</summary>
    public void ShowSection(string section) => Tabs.SelectedItem = section switch
    {
        "ai" => AiTab,
        "mcp" => McpTab,
        "about" => AboutTab,
        _ => GeneralTab,
    };

    private void Open(string url) => _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));

    private void OnSourceClick(object? sender, RoutedEventArgs e) => Open(SettingsViewModel.SourceUrl);

    private void OnLicenseClick(object? sender, RoutedEventArgs e) => Open(SettingsViewModel.LicenseUrl);

    private void OnComponentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Component c }) Open(c.Url);
    }
}
