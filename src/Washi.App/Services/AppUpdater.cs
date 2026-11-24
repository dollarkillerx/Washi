// SPDX-License-Identifier: AGPL-3.0-or-later
using Velopack;
using Velopack.Sources;

namespace Washi.App.Services;

/// <summary>
/// Install and auto-update. Only a packaged build (tools/release) is "installed"; <c>dotnet run</c>, tests and
/// build/macos/bundle.sh report <see cref="IsInstalled"/> = false and never update.
/// </summary>
public interface IAppUpdater
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    /// <summary>Downloaded by an earlier run and not installed yet (installs on the next restart).</summary>
    string? PendingVersion { get; }
    /// <summary>The newer version on the release feed, or null when this one is current.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
    /// <summary>Downloads what <see cref="CheckAsync"/> found. <paramref name="progress"/> (0–100) runs on a worker thread.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken ct);
    /// <summary>Starts Velopack's updater, which waits for this process to exit, installs the update and starts Washi
    /// again. The caller then quits normally (the MCP server closes first).</summary>
    void ApplyAfterExit();
}

/// <summary>Velopack with the GitHub releases of the Washi repository as the feed.</summary>
public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/dollarkillerx/Washi";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private UpdateInfo? _update;

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Current;
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) =>
        _update is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync(_update, progress, ct);

    public void ApplyAfterExit() =>
        _manager.WaitExitThenApplyUpdates(_update?.TargetFullRelease ?? _manager.UpdatePendingRestart, silent: false, restart: true);
}

/// <summary>Not a packaged build (tests, <c>dotnet run</c>, a hand-built .app).</summary>
public sealed class NullAppUpdater : IAppUpdater
{
    public static readonly NullAppUpdater Instance = new();
    public bool IsInstalled => false;
    public string CurrentVersion => AppVersion.Current;
    public string? PendingVersion => null;
    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyAfterExit() { }
}

public static class AppVersion
{
    /// <summary>"0.1.0": the version the build was given (Directory.Build.props, or -p:Version in tools/release).</summary>
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
