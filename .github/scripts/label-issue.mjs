// Adds the area:<category> label to an issue opened from a form: from the bug form's Area, or from the Works in play form's row.
import { readFileSync } from 'node:fs';
import { api } from './github.mjs';
import { loadRoadmap } from '../../site/scripts/issue-forms.mjs';

const issue = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8')).issue;
const field = (label) => (issue.body ?? '').match(new RegExp(`### ${label}\\s*\\n\\s*\\n(.+)`))?.[1].trim();
const { categories } = await loadRoadmap();
let category = categories.find((c) => c.name === field('Area'));
const row = field('Roadmap system')?.match(/\[([a-z0-9-]+)\]$/)?.[1];
if (!category && row) category = categories.find((c) => c.rows.some((r) => r.id === row));
if (category) {
  await api(`/repos/{repo}/issues/${issue.number}/labels`, { method: 'POST', body: { labels: [`area:${category.id}`] } });
  console.log(`#${issue.number}: area:${category.id}`);
}
