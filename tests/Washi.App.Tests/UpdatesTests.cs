// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Washi.App;
using Washi.App.Services;
using Washi.App.ViewModels;

namespace Washi.App.Tests;

/// <summary>An installed app's updater, without the network: the "release feed" is a field.</summary>
internal sealed class FakeUpdater : IAppUpdater
{
    public string? Available, Pending;
    public Exception? Fails;
    public bool Applied;
    public bool IsInstalled { get; init; } = true;
    public string CurrentVersion => "0.1.0";
    public string? PendingVersion => Pending;
    public Task<string?> CheckAsync(CancellationToken ct) => Fails is { } e ? Task.FromException<string?>(e) : Task.FromResult(Available);
    public Task DownloadAsync(Action<int> progress, CancellationToken ct) { progress(50); progress(100); return Task.CompletedTask; }
    public void ApplyAfterExit() => Applied = true;
}

public sealed class UpdatesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("washi-updates-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_newer_release_is_downloaded_and_installed_on_restart()
    {
        var updater = new FakeUpdater { Available = "0.2.0" };
        bool quit = false;
        string? ready = null;
        var vm = new UpdatesViewModel(updater, () => quit = true);
        vm.UpdateReady += v => ready = v;
        Assert.Equal(UpdateState.Idle, vm.State);
        Assert.False(vm.RestartCommand.CanExecute(null));

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal((UpdateState.Ready, "0.2.0", "0.2.0"), (vm.State, vm.NewVersion, ready));
        Assert.Contains("0.2.0", vm.StatusText);
        Assert.False(vm.CheckCommand.CanExecute(null));             // nothing more to check until it is installed

        vm.RestartCommand.Execute(null);
        Assert.True(updater.Applied && quit);
    }

    [Fact]
    public async Task Up_to_date_failures_and_source_builds_say_so_and_the_startup_check_stays_quiet()
    {
        var current = new UpdatesViewModel(new FakeUpdater(), () => { });
        await current.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.UpToDate, current.State);

        // offline: the button reports it, the start-up check does not
        var offline = new FakeUpdater { Fails = new HttpRequestException("no network") };
        var shown = new UpdatesViewModel(offline, () => { });
        await shown.CheckCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, shown.State);
        Assert.Contains("no network", shown.StatusText);
        var quiet = new UpdatesViewModel(offline, () => { });
        await quiet.CheckInBackgroundAsync();
        Assert.Equal(UpdateState.Idle, quiet.State);
        Assert.Equal("", quiet.StatusText);

        // an update downloaded by an earlier run is ready straight away
        Assert.Equal(UpdateState.Ready, new UpdatesViewModel(new FakeUpdater { Pending = "0.2.0" }, () => { }).State);

        // dotnet run / a hand-built app never checks
        var source = new UpdatesViewModel(NullAppUpdater.Instance, () => { });
        Assert.Equal(UpdateState.NotInstalled, source.State);
        Assert.False(source.CheckCommand.CanExecute(null));
        await source.CheckInBackgroundAsync();
        Assert.Equal(UpdateState.NotInstalled, source.State);
    }

    [AvaloniaFact]
    public async Task The_header_offers_the_restart_once_an_update_is_ready_and_the_automatic_check_can_be_turned_off()
    {
        var before = AppServices.Instance;
        var settings = Path.Combine(_dir, "settings.json");
        var services = new AppServices(settings);
        var updater = new FakeUpdater { Available = "0.2.0" };
        bool quit = false;
        services.UseUpdater(updater, () => quit = true);
        AppServices.Instance = services;
        try
        {
            var w = new MainWindow(new FakeDialogs()) { Width = 1440, Height = 900 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var button = w.FindControl<Button>("RestartToUpdateButton")!;
            Assert.False(button.IsVisible);

            await w.ViewModel.Updates.CheckInBackgroundAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(button.IsVisible);
            button.Command!.Execute(null);
            Assert.True(updater.Applied && quit);

            // Settings → About: the switch is saved
            var vm = new SettingsViewModel(settings, services, new FakeDialogs());
            Assert.True(vm.CheckForUpdates);
            vm.CheckForUpdates = false;
            Assert.False(AppSettings.Load(settings).CheckForUpdates);
            Assert.Same(services.Updates, vm.Updates);
            w.Close();
        }
        finally { AppServices.Instance = before; }
    }
}
