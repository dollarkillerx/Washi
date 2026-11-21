// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Washi.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            using (var png = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Washi.App/Assets/icon-macos-1024.png")))
            using (var ms = new MemoryStream())
            {
                png.CopyTo(ms);
                Platform.MacDock.SetIcon(ms.ToArray());
            }
            var window = new MainWindow();
            window.ViewModel.LanguageChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ReloadAsync(desktop));
            desktop.MainWindow = window;
            // the MCP server, when the user left it on (never blocks start-up)
            _ = Services.AppServices.Instance.Mcp.InitializeAsync();
            desktop.ShutdownRequested += (_, _) => Services.AppServices.Instance.Mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            // washi-app form.pdf opens the file straight away
            if (desktop.Args is [var path, ..] && File.Exists(path))
                window.Opened += async (_, _) => await window.ViewModel.LoadAsync(Path.GetFullPath(path));
            // Finder (double-click, Open With, drop on the Dock icon) sends files as an event
            if (this.TryGetFeature<Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime>() is { } activatable)
                activatable.Activated += async (_, e) =>
                {
                    if (e is Avalonia.Controls.ApplicationLifetimes.FileActivatedEventArgs files)
                    {
                        var paths = files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
                        if (paths.Count > 0 && desktop.MainWindow is MainWindow current) await current.ViewModel.DropAsync(paths);
                    }
                };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnAboutClick(object? sender, EventArgs e) => ShowSettings(about: true);

    private void OnSettingsClick(object? sender, EventArgs e) => ShowSettings(about: false);

    /// <summary>Settings (or its About section), as a page in the main window.</summary>
    public void ShowSettings(bool about)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main })
            main.ShowSettings(about ? "about" : "general");
    }

    /// <summary>Rebuilds the main window in the new language, keeping the open document and tool.</summary>
    private async Task ReloadAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not MainWindow old) return;
        // the application menu (macOS) belongs to the app, not the window: relabel it
        if (NativeMenu.GetMenu(this)?.Items is { } items)
        {
            if (items.Count > 0 && items[0] is NativeMenuItem about) about.Header = Washi.Core.Localization.L.Get("Ui_About");
            if (items.Count > 2 && items[2] is NativeMenuItem settings) settings.Header = Washi.Core.Localization.L.Get("Ui_SettingsMenu");
        }
        var fresh = new MainWindow
        {
            Width = old.Width, Height = old.Height, Position = old.Position, WindowState = old.WindowState,
        };
        fresh.ViewModel.LanguageChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(async () => await ReloadAsync(desktop));
        if (old.ViewModel.IsSettingsTool) fresh.ShowSettings("general");
        else fresh.ViewModel.ActiveTool = old.ViewModel.ActiveTool;
        desktop.MainWindow = fresh;
        fresh.Show();
        old.Close();
        if (old.ViewModel.PdfPath is { } pdf) await fresh.ViewModel.LoadAsync(pdf);
    }
}
