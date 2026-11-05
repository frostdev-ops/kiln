// What the admin pages remember per server, on the launcher's side
// (kiln-players.json, deaths.json, graves.json, waypoints.json, enemy-types.json): Kiln.Admin keeps only the last 50
// deaths in memory and has no waypoints. Kept in localStorage per server id, fed by every `state` push of every server
// the launcher is connected to (onAdminState), so it records while any admin page is open.
import { create } from "zustand";
import type { AdminCombatDeath, AdminPos, AdminState } from "../api/admin";
import { onAdminState } from "../api/admin";
import type { ServerId } from "../api/commands";

export interface SeenPlayer {
  steamId: string;
  name: string;
  /** unix seconds */
  firstSeen: number;
  lastSeen: number;
}
export interface DeathRecord extends AdminCombatDeath {
  /** key: entity + time */
  key: string;
}
export interface Grave {
  key: string;
  entity: number;
  name: string | null;
  pos: AdminPos;
  /** unix seconds */
  at: number;
}
export interface Waypoint {
  name: string;
  pos: AdminPos;
  at: number;
}

export interface ServerHistory {
  players: Record<string, SeenPlayer>;
  deaths: DeathRecord[];
  graves: Grave[];
  waypoints: Waypoint[];
  /** enemy template guid -> the admin's label for it */
  enemyLabels: Record<string, string>;
}

const empty = (): ServerHistory => ({ players: {}, deaths: [], graves: [], waypoints: [], enemyLabels: {} });
const KEY = (id: ServerId) => `kiln.admin.history.v1.${id}`;

function load(id: ServerId): ServerHistory {
  try {
    const raw = localStorage.getItem(KEY(id));
    return raw ? { ...empty(), ...(JSON.parse(raw) as Partial<ServerHistory>) } : empty();
  } catch {
    return empty();
  }
}

const timers = new Map<ServerId, ReturnType<typeof setTimeout>>();
function persist(id: ServerId, h: ServerHistory) {
  clearTimeout(timers.get(id));
  timers.set(id, setTimeout(() => {
    try {
      localStorage.setItem(KEY(id), JSON.stringify(h));
    } catch {
      /* quota: keep it in memory */
    }
  }, 400));
}

export const useHistoryStore = create<{ by: Record<ServerId, ServerHistory> }>(() => ({ by: {} }));

export function historyOf(id: ServerId): ServerHistory {
  const s = useHistoryStore.getState().by[id];
  if (s) return s;
  const h = load(id);
  useHistoryStore.setState((st) => ({ by: { ...st.by, [id]: h } }));
  return h;
}

function update(id: ServerId, f: (h: ServerHistory) => ServerHistory | null) {
  const cur = historyOf(id);
  const next = f(cur);
  if (!next || next === cur) return;
  useHistoryStore.setState((st) => ({ by: { ...st.by, [id]: next } }));
  persist(id, next);
}

/** The history of one server (re-renders on change). */
export function useHistory(id: ServerId): ServerHistory {
  historyOf(id);
  return useHistoryStore((s) => s.by[id] ?? empty());
}

// ---------------------------------------------------------------- recording

const lastSig = new Map<ServerId, string>();

export function record(id: ServerId, st: AdminState) {
  update(id, (h) => {
    const now = Date.now() / 1000;
    let changed = false;
    const players = { ...h.players };
    for (const p of st.players ?? []) {
      const sid = String(p.steamId ?? "");
      if (!/^\d{17}$/.test(sid) || !p.name) continue;
      const k = players[sid];
      if (!k || k.name !== p.name || now - k.lastSeen > 300) {
        players[sid] = { steamId: sid, name: p.name, firstSeen: k?.firstSeen ?? now, lastSeen: now };
        changed = true;
      }
    }
    let deaths = h.deaths;
    const cd = st.combatDeaths ?? [];
    const sig = cd.length ? `${cd.length}|${JSON.stringify(cd[cd.length - 1])}` : "";
    if (sig && lastSig.get(id) !== sig) {
      lastSig.set(id, sig);
      const known = new Set(h.deaths.map((d) => d.key));
      const names = new Map((st.players ?? []).map((p) => [p.entity, p.name]));
      const add: DeathRecord[] = [];
      for (const d of cd) {
        const key = `${d.entity}|${d.time}`;
        if (known.has(key)) continue;
        known.add(key);
        let killerName = d.killerName;
        let killerKind = d.killerKind;
        if (!killerName && names.has(d.killer)) {
          killerName = names.get(d.killer);
          killerKind = "player";
        }
        add.push({ ...d, key, victimName: d.victimName ?? names.get(d.entity), killerName, killerKind: killerKind ?? (d.killer ? undefined : "none") });
      }
      if (add.length) {
        deaths = [...h.deaths, ...add].slice(-300);
        changed = true;
      }
    }
    // graves from the health-polling deaths (older Kiln.Admin without kiln.combat): clock is server seconds
    let graves = h.graves;
    for (const d of st.deaths ?? []) {
      const key = `${d.entity}|${Math.round(d.clock * 10)}`;
      if (graves.some((g) => g.key === key) || !d.pos) continue;
      const name = (st.players ?? []).find((p) => p.entity === d.entity)?.name ?? null;
      graves = [...graves, { key, entity: d.entity, name, pos: d.pos, at: now }].slice(-200);
      changed = true;
    }
    return changed ? { ...h, players, deaths, graves } : null;
  });
}

let wired = false;
/** Starts recording every server's pushes (AdminProvider calls this). */
export function startHistory() {
  if (wired) return;
  wired = true;
  onAdminState(record);
}

// ---------------------------------------------------------------- edits (validated)

export function saveWaypoint(id: ServerId, name: string, pos: AdminPos): string | null {
  const n = name.trim();
  if (n.length < 1 || n.length > 40) return "Waypoint names: 1 to 40 characters.";
  if (![pos.x, pos.y, pos.z].every((v) => typeof v === "number" && Number.isFinite(v))) return "No position for that waypoint.";
  update(id, (h) => ({ ...h, waypoints: [...h.waypoints.filter((w) => w.name.toLowerCase() !== n.toLowerCase()), { name: n, pos: { x: pos.x, y: pos.y, z: pos.z }, at: Date.now() / 1000 }] }));
  return null;
}

export function deleteWaypoint(id: ServerId, name: string) {
  update(id, (h) => ({ ...h, waypoints: h.waypoints.filter((w) => w.name !== name) }));
}

export function labelEnemy(id: ServerId, guid: string, label: string) {
  update(id, (h) => ({ ...h, enemyLabels: { ...h.enemyLabels, [guid]: label.slice(0, 60) } }));
}

/** Players known by SteamID: seen online while the launcher watched, plus the permission file's notes. */
export function knownPlayers(h: ServerHistory, extra: Record<string, { name?: string | null }> = {}): SeenPlayer[] {
  const out = new Map(Object.entries(h.players));
  for (const [sid, p] of Object.entries(extra)) if (!out.has(sid) && /^\d{17}$/.test(sid)) out.set(sid, { steamId: sid, name: p.name ?? "", firstSeen: 0, lastSeen: 0 });
  return [...out.values()].sort((a, b) => b.lastSeen - a.lastSeen || a.name.localeCompare(b.name));
}
