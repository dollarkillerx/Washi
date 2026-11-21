// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Washi.App.ViewModels;

namespace Washi.App;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow() : this(new SettingsViewModel(), showAbout: false) { }

    public SettingsWindow(SettingsViewModel vm, bool showAbout)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        if (showAbout) Tabs.SelectedItem = AboutTab;
    }

    private void Open(string url) => _ = Launcher.LaunchUriAsync(new Uri(url));

    private void OnSourceClick(object? sender, RoutedEventArgs e) => Open(SettingsViewModel.SourceUrl);

    private void OnLicenseClick(object? sender, RoutedEventArgs e) => Open(SettingsViewModel.LicenseUrl);

    private void OnComponentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Component c }) Open(c.Url);
    }
}
