// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            var window = new MainWindow();
            desktop.MainWindow = window;
            // washi-app form.pdf opens the file straight away
            if (desktop.Args is [var path, ..] && File.Exists(path))
                window.Opened += async (_, _) => await window.ViewModel.LoadAsync(Path.GetFullPath(path));
        }

        base.OnFrameworkInitializationCompleted();
    }
}