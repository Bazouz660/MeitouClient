using Velopack;
using Velopack.Sources;

namespace Meitou.Launcher;

/// <summary>
/// Self-update through Velopack (docs/launcher.md, "Self-update"): the feed of each <c>launcher-v*</c> release, one Velopack channel
/// per runtime (<see cref="Releases.Rid"/>). Only a launcher installed by its Setup or AppImage can update itself; a build run from
/// anywhere else reports <see cref="IsInstalled"/> false.
/// </summary>
public sealed class LauncherUpdater(Releases releases, bool prereleases)
{
    readonly UpdateManager manager = new(new LauncherReleaseSource(releases, prereleases), new UpdateOptions { ExplicitChannel = Releases.Rid });
    UpdateInfo? pending;

    public bool IsInstalled => manager.IsInstalled;

    /// <summary>The newer launcher version on GitHub, or null (none, or not installed).</summary>
    public async Task<string?> CheckAsync()
    {
        if (!manager.IsInstalled) return null;
        pending = await manager.CheckForUpdatesAsync();
        return pending?.TargetFullRelease.Version.ToString();
    }

    /// <summary>Downloads the update <see cref="CheckAsync"/> found, then restarts into it.</summary>
    public async Task UpdateAndRestartAsync(Action<int> progress, CancellationToken ct)
    {
        if (pending is null) return;
        await manager.DownloadUpdatesAsync(pending, progress, ct);
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }

    /// <summary>
    /// Velopack's GitHub source, given only this repository's <c>launcher-v*</c> releases from <see cref="Releases"/>' cached list:
    /// Velopack's own listing reads only the newest 10 releases, which game releases would soon crowd the launcher's out of.
    /// </summary>
    sealed class LauncherReleaseSource(Releases releases, bool prereleases)
        : GithubSource($"https://github.com/{Releases.Repository}", null, prereleases)
    {
        protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
        {
            var (list, _) = await releases.FetchAsync();
            return [.. list
                .Where(r => r.Kind == ReleaseKind.Launcher && (includePrereleases || !r.Prerelease))
                .Select(r => new GithubRelease
                {
                    Name = r.Title,
                    Prerelease = r.Prerelease,
                    PublishedAt = r.Published.UtcDateTime,
                    Assets = [.. r.Assets.Select(a => new GithubReleaseAsset { Name = a.Name, BrowserDownloadUrl = a.Url, Url = a.Url, ContentType = "application/octet-stream" })],
                })];
        }
    }
}
