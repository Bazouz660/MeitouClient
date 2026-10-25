// /roadmap-changes.json: status changes of roadmap rows, newest first, and status counts over time.
import { history } from '../data/history';

export function GET() {
  return new Response(JSON.stringify(history), { headers: { 'Content-Type': 'application/json' } });
}
