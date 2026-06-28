using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace FCAT.Services;

/// <summary>
/// In-app auto-update against the project's GitHub Releases (Velopack).
///
/// Only works in an INSTALLED build (one produced by `vpk pack` and installed via Setup.exe) —
/// when running from `dotnet run` / a loose build, <see cref="UpdateManager.IsInstalled"/> is
/// false and every call is a safe no-op. Prerelease is ON because releases are tagged "-beta".
/// </summary>
public class UpdaterService
{
    // Public repo that hosts the release assets.
    private const string RepoUrl = "https://github.com/MifuneSG/FCAT";

    private readonly UpdateManager _mgr;
    private UpdateInfo? _pending;

    public UpdaterService()
        => _mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: true));

    /// <summary>False when running from source — guards the UI so we don't offer updates that can't apply.</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>The version string of a downloaded, ready-to-apply update (null if none).</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Checks GitHub for a newer release and, if found, downloads it in the background.
    /// Returns the new version string when an update is staged and ready to apply, else null.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (!_mgr.IsInstalled) return null;

        var info = await _mgr.CheckForUpdatesAsync();
        if (info == null) return null;          // already up to date

        await _mgr.DownloadUpdatesAsync(info);
        _pending = info;
        return PendingVersion;
    }

    /// <summary>Applies the staged update and relaunches the app. Does not return on success.</summary>
    public void ApplyAndRestart()
    {
        if (_pending != null)
            _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
