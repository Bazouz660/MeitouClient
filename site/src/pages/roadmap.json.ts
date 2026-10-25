// /roadmap.json: the whole roadmap as data, for the reports service, the launcher and anyone building on it.
import { asOf, bugs, categories, fixedSeen, fixedUnseen, order, statuses } from '../data/roadmap';

export function GET() {
  const rows = categories.flatMap((c) => c.rows.map((r) => ({ ...r, category: c.id })));
  return new Response(JSON.stringify({
    asOf, statuses, order,
    categories: categories.map((c) => ({ id: c.id, name: c.name, blurb: c.blurb })),
    rows, bugs, fixedSeen, fixedUnseen,
  }), { headers: { 'Content-Type': 'application/json' } });
}
