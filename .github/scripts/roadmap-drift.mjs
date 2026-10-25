// Compares the roadmap (site/src/data/roadmap.ts) with GitHub and keeps one open "Roadmap out of date" issue (label roadmap-drift)
// listing what to look at; closes it when nothing is left. It only reports: a person changes the roadmap.
// DRY_RUN=1 prints the findings without touching issues.
import { all, api, repo } from './github.mjs';
import { loadRoadmap, stale } from '../../site/scripts/issue-forms.mjs';

const MAX_BEHIND = Number(process.env.DRIFT_MAX_BEHIND || 100);
const data = await loadRoadmap();
const rows = data.categories.flatMap((c) => c.rows);
const named = new Set(JSON.stringify(data).match(/#\d+/g) ?? []);   // every #N the roadmap names anywhere, notes included
const issues = new Map((await all('/repos/{repo}/issues?state=all')).map((i) => [`#${i.number}`, i]));
const link = (id) => `[${id}](https://github.com/${repo}/issues/${id.slice(1)})`;
const sections = [];
const add = (title, lines) => { if (lines.length) sections.push(`### ${title}\n\n${lines.map((l) => `- ${l}`).join('\n')}`); };

// Known bugs whose issue is closed (a Known bugs entry whose state already says closed is left alone).
const openBugIds = new Set([...rows.flatMap((r) => r.bugs), ...data.bugs.map((b) => b.id)]);
for (const b of data.bugs) if (/closed/i.test(b.state)) openBugIds.delete(b.id);
add('Named as a bug, but the issue is closed', [...openBugIds].filter((id) => issues.get(id)?.state === 'closed').map((id) => {
  const i = issues.get(id);
  return `${link(id)} ${i.title}: ${i.state_reason === 'not_planned' ? 'closed as not planned, decide what the row says' : 'completed, move it to the fixes'}`;
}));

// Open bug issues the roadmap never names.
add('Open bugs the roadmap does not name', [...issues.values()]
  .filter((i) => !i.pull_request && i.state === 'open' && i.labels.some((l) => l.name === 'bug') && !named.has(`#${i.number}`))
  .map((i) => `${link(`#${i.number}`)} ${i.title}`));

// Row bug ids and the Known bugs list must name each other.
const listed = new Set(data.bugs.map((b) => b.id));
const onRows = new Set(rows.flatMap((r) => r.bugs));
add('Row bug ids with no Known bugs entry', rows.flatMap((r) => r.bugs.filter((b) => !listed.has(b)).map((b) => `${b} on \`${r.id}\``)));
add('Known bugs no row names', data.bugs.filter((b) => !onRows.has(b.id)).map((b) => `${b.id}: ${b.seen}`));

// How far master has moved since the roadmap's as-of commit.
try {
  const cmp = await api(`/repos/{repo}/compare/${data.asOf.master}...master`);
  if (cmp?.ahead_by > MAX_BEHIND) add('Old as-of commit', [`\`asOf.master\` ${data.asOf.master} (${data.asOf.date}) is ${cmp.ahead_by} commits behind master`]);
} catch (e) {
  add('Old as-of commit', [`could not compare ${data.asOf.master} with master: ${e.message}`]);
}

// Issue forms and labels generated from the roadmap.
add('Issue forms out of date', (await stale()).map((p) => `\`${p}\`: run \`node site/scripts/issue-forms.mjs\``));

// What the play reports suggest, when the reports service is set (repository variable REPORTS_URL).
if (process.env.REPORTS_URL) {
  try {
    const proposals = await (await fetch(`${process.env.REPORTS_URL.replace(/\/$/, '')}/proposals`)).json();
    add('Play reports suggest', proposals.map((p) => `\`${p.row}\` ${p.system}: ${p.to === 'look' ? 'take a look' : `${p.from} to ${p.to}`} (${p.why})`));
  } catch (e) {
    console.log(`reports not read: ${e.message}`);
  }
}

const body = `The roadmap (\`site/src/data/roadmap.ts\`, as of ${data.asOf.date}) no longer matches GitHub. The Roadmap drift workflow keeps this issue up to date and closes it when nothing is left.\n\n${sections.join('\n\n')}\n`;
console.log(sections.length ? body : 'roadmap matches GitHub');
if (process.env.DRY_RUN) process.exit(0);

await api('/repos/{repo}/labels', { method: 'POST', body: { name: 'roadmap-drift', color: 'fbca04', description: 'The roadmap no longer matches GitHub' } });   // 422 when it exists
const [open] = await api('/repos/{repo}/issues?state=open&labels=roadmap-drift');
if (sections.length === 0) {
  if (open) await api(`/repos/{repo}/issues/${open.number}`, { method: 'PATCH', body: { state: 'closed', state_reason: 'completed' } });
} else if (open) {
  if (open.body !== body) await api(`/repos/{repo}/issues/${open.number}`, { method: 'PATCH', body: { body } });
  console.log(`updated #${open.number}`);
} else {
  const created = await api('/repos/{repo}/issues', { method: 'POST', body: { title: 'Roadmap out of date', body, labels: ['roadmap-drift'] } });
  console.log(`opened #${created.number}`);
}
