// Roadmap update proposal: after a nightly or release, Claude Haiku reads the roadmap, what changed since its as-of commit (commits,
// issues, the drift findings, play reports) and returns edits as structured output. This script applies them to
// site/src/data/roadmap.ts line by line (ids, names and categories never change) and writes a summary for the pull request.
// A person merges the PR: nothing here changes the live roadmap on its own. docs/play-reports.md, "Roadmap updates".
//   ANTHROPIC_API_KEY, GITHUB_TOKEN, REPORTS_URL (optional); writes $SUMMARY_FILE (default roadmap-update.md) when it changed the file.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import Anthropic from '@anthropic-ai/sdk';
import { zodOutputFormat } from '@anthropic-ai/sdk/helpers/zod';
import { z } from 'zod';
import { all } from './github.mjs';
import { loadRoadmap, root } from '../../site/scripts/issue-forms.mjs';

const MODEL = 'claude-haiku-5-5';
const file = new URL('site/src/data/roadmap.ts', root);
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 64 << 20 }).trim();
const data = await loadRoadmap();
const source = readFileSync(file, 'utf8');
const head = git('rev-parse', '--short=8', 'HEAD');

// ---- What the model reads ----
const commits = git('log', '--no-merges', '--format=%h %s', `${data.asOf.master}..HEAD`).split('\n').filter(Boolean);
const since = `${data.asOf.date}T00:00:00Z`;
const issues = (await all(`/repos/{repo}/issues?state=all&since=${since}`))
  .map((i) => `#${i.number} [${i.pull_request ? 'PR' : 'issue'}, ${i.state}${i.state_reason ? `, ${i.state_reason}` : ''}${i.labels.length ? `, ${i.labels.map((l) => l.name).join(' ')}` : ''}] ${i.title}`);
let drift = '';
try {
  drift = execFileSync('node', ['.github/scripts/roadmap-drift.mjs'], { cwd: root, encoding: 'utf8', env: { ...process.env, DRY_RUN: '1' } });
} catch (e) { drift = `(drift check failed: ${e.message})`; }
let reports = '(no reports service set)';
if (process.env.REPORTS_URL) {
  try {
    const base = process.env.REPORTS_URL.replace(/\/$/, '');
    const [summary, proposals] = await Promise.all([fetch(`${base}/summary`).then((r) => r.json()), fetch(`${base}/proposals`).then((r) => r.json())]);
    reports = JSON.stringify({ summary, proposals }, null, 1);
  } catch (e) { reports = `(reports not read: ${e.message})`; }
}

const statuses = ['works', 'untested', 'wip', 'bug', 'missing'];
const Edits = z.object({
  rows: z.array(z.object({
    id: z.string().describe('An existing row id; never a new one.'),
    status: z.enum(statuses).nullable().describe('New status, or null to keep it.'),
    note: z.string().nullable().describe('The whole new note, or null to keep it.'),
    bugs: z.array(z.string()).nullable().describe('The whole new list of open bug ids ("#N") on this row, or null to keep it.'),
    reason: z.string().describe('The evidence: commit hashes, issue numbers or report counts.'),
  })),
  removeBugs: z.array(z.string()).describe('Known bugs entries ("#N") to remove because their issue is closed as fixed.'),
  addBugs: z.array(z.object({ id: z.string(), seen: z.string(), original: z.string(), state: z.string() })).describe('New Known bugs entries for open bug issues.'),
  addFixedUnseen: z.array(z.object({ id: z.string(), text: z.string() })).describe('Fixes on master not yet seen in play, newest first.'),
  summary: z.string().describe('Two to six short bullet lines for the pull request, each naming the evidence.'),
});

const system = `You keep the Meitou parity roadmap (site/src/data/roadmap.ts) honest. Meitou reimplements the game Kenshi; each row is one of Kenshi's systems with one status:
works = seen working in play, or a reported bug whose fix is on master; untested = in the code, never seen; wip = in the code with known gaps; bug = does not work; missing = not in Meitou. A system that works but has known bugs stays works and lists the bug ids.

Propose only edits the evidence below supports, and cite it in each reason. Rules:
- A closed issue that a row or Known bugs names: if closed as completed, remove it from the row's bugs and from Known bugs, add it to addFixedUnseen, and say "fixed on master (#N)" in the note. If closed as not planned, leave it and say so in the summary.
- An open issue that reports a bug in a system: add it to that row's bugs and to addBugs, unless the roadmap already names it.
- Untested to works only on play reports (3 distinct people, or a trusted tester) or an issue or comment saying it was seen working. Never on commits alone.
- Commits can change a note (what is now in the code) and can move missing to untested or wip, never further.
- Keep notes short and factual, in the existing style: plain sentences, issue numbers in brackets, no hype. Keep what an existing note says unless evidence changes it.
- When nothing needs changing, return empty lists and the summary "Nothing to change."`;

const user = `Current roadmap file:
<roadmap>
${source}
</roadmap>

Drift check findings:
<drift>
${drift}
</drift>

Commits on master since the as-of commit ${data.asOf.master} (${commits.length}, newest first${commits.length > 400 ? ', first 400' : ''}):
<commits>
${commits.slice(0, 400).join('\n')}
</commits>

Issues and pull requests updated since ${data.asOf.date}:
<issues>
${issues.join('\n')}
</issues>

Play reports (counts per row since the cutoff, and the service's own suggestions):
<reports>
${reports}
</reports>`;

// EDITS_FILE: apply a saved answer instead of asking the model (to check the applying part offline).
let edits;
if (process.env.EDITS_FILE) {
  edits = Edits.parse(JSON.parse(readFileSync(process.env.EDITS_FILE, 'utf8')));
} else {
  const client = new Anthropic();
  const response = await client.messages.parse({
    model: MODEL,
    max_tokens: 16000,
    system,
    messages: [{ role: 'user', content: user }],
    output_config: { effort: 'medium', format: zodOutputFormat(Edits) },
  });
  if (response.stop_reason === 'refusal' || response.stop_reason === 'max_tokens' || !response.parsed_output) {
    console.error(`No usable answer (stop_reason ${response.stop_reason}).`);
    process.exit(1);
  }
  edits = response.parsed_output;
  console.log(`tokens: ${response.usage.input_tokens} in, ${response.usage.output_tokens} out`);
}
console.log(JSON.stringify(edits, null, 1));

// ---- Apply ----
const q = (s) => JSON.stringify(s);
const lines = source.split('\n');
const rows = new Map(data.categories.flatMap((c) => c.rows).map((r) => [r.id, r]));
const rowLine = (r) => `    { id: ${q(r.id)}, system: ${q(r.system)}, status: ${q(r.status)}, note: ${q(r.note)}, bugs: ${JSON.stringify(r.bugs)}`
  + `${r.platforms ? `, platforms: ${JSON.stringify(r.platforms)}` : ''}${r.seen ? `, seen: ${JSON.stringify(r.seen)}` : ''} },`;
const skipped = [];
let changed = false;

for (const e of edits.rows) {
  const r = rows.get(e.id);
  if (!r) { skipped.push(`unknown row ${e.id}`); continue; }
  const next = { ...r, status: e.status ?? r.status, note: e.note ?? r.note, bugs: e.bugs ?? r.bugs };
  if (next.bugs.some((b) => !/^#\d+$/.test(b))) { skipped.push(`${e.id}: bug ids must be #N`); continue; }
  const i = lines.findIndex((l) => l.startsWith(`    { id: ${q(e.id)}, system:`));
  if (i < 0) { skipped.push(`${e.id}: row line not found`); continue; }
  const text = rowLine(next);
  if (text !== lines[i]) { lines[i] = text; changed = true; }
}

const section = (name) => lines.findIndex((l) => l.startsWith(`export const ${name}:`));
for (const id of edits.removeBugs) {
  const i = lines.findIndex((l, n) => n > section('bugs') && l.startsWith(`  { id: ${q(id)}, seen:`));
  if (i >= 0) { lines.splice(i, 1); changed = true; } else skipped.push(`Known bugs has no ${id}`);
}
for (const b of edits.addBugs.toReversed()) {
  if (!/^#\d+$/.test(b.id) || lines.some((l) => l.startsWith(`  { id: ${q(b.id)}, seen:`))) { skipped.push(`bug ${b.id} not added`); continue; }
  lines.splice(section('bugs') + 1, 0, `  { id: ${q(b.id)}, seen: ${q(b.seen)}, original: ${q(b.original)}, state: ${q(b.state)} },`);
  changed = true;
}
for (const f of edits.addFixedUnseen.toReversed()) {
  if (f.id && lines.some((l) => l.startsWith(`  { id: ${q(f.id)}, text:`))) { skipped.push(`fix ${f.id} already listed`); continue; }
  lines.splice(section('fixedUnseen') + 1, 0, `  { id: ${q(f.id)}, text: ${q(f.text)} },`);
  changed = true;
}
if (!changed) { console.log('Nothing to change.'); process.exit(0); }

const asOf = lines.findIndex((l) => l.startsWith('export const asOf = '));
lines[asOf] = `export const asOf = { date: ${q(new Date().toISOString().slice(0, 10))}, master: ${q(head)} };`;
writeFileSync(file, lines.join('\n'));

// The result must still load, with the same rows and valid statuses; otherwise the original is restored.
try {
  const fresh = await import(`${file.href}?v=${Date.now()}`);
  const after = fresh.categories.flatMap((c) => c.rows);
  if (after.length !== rows.size || after.some((r) => !rows.has(r.id) || !statuses.includes(r.status))) throw new Error('rows changed shape');
} catch (e) {
  writeFileSync(file, source);
  console.error(`Edits not applied: ${e.message}`);
  process.exit(1);
}

const out = process.env.SUMMARY_FILE || 'roadmap-update.md';
writeFileSync(out, `Proposed by ${MODEL} after master ${head}, from commits since ${data.asOf.master}, issues, the drift check and play reports. Check each line against its evidence before merging.

${edits.summary.trim()}
${skipped.length ? `\nNot applied:\n${skipped.map((s) => `- ${s}`).join('\n')}\n` : ''}`);
console.log(`wrote ${out}`);
