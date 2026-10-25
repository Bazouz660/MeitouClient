// /roadmap-changes.xml: the roadmap's status changes as an RSS feed (newest 50).
import type { APIContext } from 'astro';
import { history } from '../data/history';
import { statuses } from '../data/roadmap';
import type { Status } from '../data/roadmap';

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const label = (s: Status | null) => (s ? statuses[s].label : 'removed');

export function GET({ site }: APIContext) {
  const base = site?.href.replace(/\/$/, '') ?? '';
  const items = history.changes.slice(0, 50).map((c) => {
    const title = c.from === null ? `${c.system}: added as ${label(c.to)}` : `${c.system}: ${label(c.from)} to ${label(c.to)}`;
    const link = `${base}/roadmap#${c.id}`;
    return `<item><title>${esc(title)}</title><link>${link}</link><guid isPermaLink="false">${c.commit}-${c.id}</guid><pubDate>${new Date(`${c.date}T12:00:00Z`).toUTCString()}</pubDate></item>`;
  });
  const xml = `<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0"><channel><title>Meitou roadmap changes</title><link>${base}/roadmap</link><description>Status changes of Kenshi's systems in Meitou.</description>
${items.join('\n')}
</channel></rss>
`;
  return new Response(xml, { headers: { 'Content-Type': 'application/rss+xml; charset=utf-8' } });
}
