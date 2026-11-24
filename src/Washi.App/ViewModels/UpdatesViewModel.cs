// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Washi.App.Services;
using Washi.Core.Localization;

namespace Washi.App.ViewModels;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// Updates (Settings → About, and a button in the header once one is ready): the version, "Check for updates", download
/// progress, "Restart to update". A check runs once after start-up unless turned off; it stays quiet unless an update
/// is ready. Lives as long as the app (the window is rebuilt when the language changes).
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IAppUpdater _updater;
    private readonly Action _quit;

    /// <param name="quit">Quits the app the normal way (Velopack installs after the process exits).</param>
    public UpdatesViewModel(IAppUpdater updater, Action quit)
    {
        _updater = updater;
        _quit = quit;
        if (!updater.IsInstalled) _state = UpdateState.NotInstalled;
        else if (updater.PendingVersion is { } pending)
        {
            _newVersion = pending;
            _state = UpdateState.Ready;
        }
        else _state = UpdateState.Idle;
    }

    public string CurrentVersion => _updater.CurrentVersion;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady), nameof(IsBusy), nameof(IsChecking), nameof(CanCheck), nameof(IsInstalled))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(RestartCommand))]
    private UpdateState _state;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(ReadyText))] private string? _newVersion;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private int _progress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string? _error;

    public bool IsInstalled => State != UpdateState.NotInstalled;
    public bool IsReady => State == UpdateState.Ready;
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool IsChecking => State == UpdateState.Checking;
    public bool CanCheck => State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => L.Get("Ui_UpdNotInstalled"),
        UpdateState.Checking => L.Get("Ui_UpdChecking"),
        UpdateState.UpToDate => L.Get("Ui_UpdUpToDate"),
        UpdateState.Downloading => L.F("Ui_UpdDownloading", NewVersion ?? "", Progress),
        UpdateState.Ready => ReadyText,
        UpdateState.Failed => L.F("Ui_UpdFailed", Error ?? ""),
        _ => "",
    };

    public string ReadyText => L.F("Ui_UpdReady", NewVersion ?? "");

    /// <summary>Raised (on the UI thread) when an update finished downloading.</summary>
    public event Action<string>? UpdateReady;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync(CancellationToken ct) => RunAsync(background: false, ct);

    /// <summary>The start-up check: failures (offline, rate-limited) are not shown.</summary>
    public Task CheckInBackgroundAsync(CancellationToken ct = default) =>
        State == UpdateState.Idle ? RunAsync(background: true, ct) : Task.CompletedTask;

    private async Task RunAsync(bool background, CancellationToken ct)
    {
        State = UpdateState.Checking;
        Error = null;
        try
        {
            var version = await _updater.CheckAsync(ct);
            if (version is null)
            {
                State = background ? UpdateState.Idle : UpdateState.UpToDate;
                return;
            }
            NewVersion = version;
            Progress = 0;
            State = UpdateState.Downloading;
            var ui = SynchronizationContext.Current;
            await _updater.DownloadAsync(p =>
            {
                if (ui is null) Progress = p;
                else ui.Post(_ => Progress = p, null);
            }, ct);
            State = UpdateState.Ready;
            UpdateReady?.Invoke(version);
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Idle;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or TimeoutException or NotSupportedException or ArgumentException)
        {
            Error = e.Message;
            State = background ? UpdateState.Idle : UpdateState.Failed;
        }
    }

    private bool CanRestart() => State == UpdateState.Ready;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart()
    {
        if (State != UpdateState.Ready) return;
        _updater.ApplyAfterExit();
        _quit();
    }
}
