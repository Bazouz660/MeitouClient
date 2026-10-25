// Writes src/data/history.json from every version of src/data/roadmap.ts in git: status counts per day (oldest first) and each row's
// status changes (newest first). Run from site/ before `npm run build` (the site image workflow does); without it the page has no history.
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 64 << 20 });
const path = 'site/src/data/roadmap.ts';
const commits = git('log', '--reverse', '--format=%H %cI', '--', path).trim().split('\n').filter(Boolean).map((l) => {
  const [hash, date] = l.split(' ');
  return { hash, date: date.slice(0, 10) };
});

const tmp = mkdtempSync(join(tmpdir(), 'roadmap-history-'));
const snapshots = [], changes = [];
let previous = null;
try {
  for (const c of commits) {
    const file = join(tmp, `${c.hash}.ts`);
    writeFileSync(file, git('show', `${c.hash}:${path}`));
    const data = await import(pathToFileURL(file).href);
    const rows = new Map(data.categories.flatMap((cat) => cat.rows).map((r) => [r.id, r]));
    const counts = Object.fromEntries(data.order.map((s) => [s, 0]));
    for (const r of rows.values()) counts[r.status] = (counts[r.status] ?? 0) + 1;
    if (snapshots.at(-1)?.date === c.date) snapshots.pop();
    snapshots.push({ date: c.date, commit: c.hash.slice(0, 8), counts });
    if (previous) {
      for (const [id, r] of rows) {
        const was = previous.get(id)?.status ?? null;
        if (was !== r.status) changes.push({ date: c.date, commit: c.hash.slice(0, 8), id, system: r.system, from: was, to: r.status });
      }
      for (const [id, r] of previous) if (!rows.has(id)) changes.push({ date: c.date, commit: c.hash.slice(0, 8), id, system: r.system, from: r.status, to: null });
    }
    previous = rows;
  }
} finally {
  rmSync(tmp, { recursive: true, force: true });
}
changes.reverse();
writeFileSync(join(root, 'site/src/data/history.json'), JSON.stringify({ snapshots, changes }, null, 1) + '\n');
console.log(`history.json: ${snapshots.length} snapshots, ${changes.length} changes`);
