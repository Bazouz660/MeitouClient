// Game releases (v*) from GitHub, read once at build time; empty when GitHub cannot be reached.
// A row's `seen` older than the second newest full release is shown as not rechecked: it predates the last two releases.
export interface Release { tag: string; date: string; nightly: boolean; }

async function load(): Promise<Release[]> {
  try {
    const response = await fetch('https://api.github.com/repos/VlcekM/MeitouClient/releases?per_page=100', { headers: { 'User-Agent': 'meitou-site' } });
    if (!response.ok) return [];
    const list: { tag_name: string; prerelease: boolean; draft: boolean; published_at: string | null }[] = await response.json();
    return list
      .filter((r) => r.tag_name.startsWith('v') && !r.draft && r.published_at)
      .map((r) => ({ tag: r.tag_name.slice(1), date: r.published_at!.slice(0, 10), nightly: r.prerelease }))
      .sort((a, b) => b.date.localeCompare(a.date));
  } catch {
    return [];
  }
}

const all = await load();
export const fullReleases = all.filter((r) => !r.nightly);
/** The builds a report can name: the newest full release and the newest nightly after it. */
export const reportBuilds = [fullReleases[0], all.find((r) => r.nightly && (!fullReleases[0] || r.date >= fullReleases[0].date))]
  .filter((r): r is Release => !!r).map((r) => r.tag);
/** Evidence dated before this is from before the last two releases. */
export const recheckBefore: string | undefined = fullReleases[1]?.date;
