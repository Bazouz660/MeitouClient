// The roadmap's history, from scripts/history.mjs (src/data/history.json, made at build time from git; empty when it was not run).
import type { Status } from './roadmap';

export interface Snapshot { date: string; commit: string; counts: Record<Status, number>; }
export interface Change { date: string; commit: string; id: string; system: string; from: Status | null; to: Status | null; }

const files = import.meta.glob<{ default: { snapshots: Snapshot[]; changes: Change[] } }>('./history.json', { eager: true });
export const history = Object.values(files)[0]?.default ?? { snapshots: [], changes: [] };
