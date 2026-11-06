// Shared admin pieces: op gating, player picker, item picker, coordinate inputs,
// validation helpers (so bad input never reaches the game).
import { useMemo, useState, type ReactNode } from "react";
import { useParams } from "react-router-dom";
import { AdminProvider, useAdminQuery, useAdminServerId, useAdminState, useHasOp, useInAdminProvider, type AdminPlayer, type AdminPos, type KnownOp } from "../api/admin";
import type { ServerId } from "../api/commands";
import type { ItemHit } from "./types";
import { Banner, Select, SuggestInput, TextInput, useDebounced, type Suggestion } from "./ui";

// ---------------------------------------------------------------- provider for pages mounted on their own

/** A page component that works inside <AdminRoutes> and on its own route (serverId from the URL). */
export function withAdmin<P extends object>(Page: (p: P) => ReactNode) {
  return function AdminPage(props: P) {
    const inside = useInAdminProvider();
    const { id } = useParams();
    if (inside || !id) return <Page {...props} />;
    return (
      <AdminProvider serverId={id}>
        <Page {...props} />
      </AdminProvider>
    );
  };
}

// ---------------------------------------------------------------- op gating

/** Children when the server serves every op in `ops`; else a "needs a newer Kiln.Admin" note (nothing while offline). */
export function NeedsOp({ ops, children, what }: { ops: KnownOp | (KnownOp | string)[]; children: ReactNode; what?: string }) {
  const st = useAdminState();
  const list = Array.isArray(ops) ? ops : [ops];
  const missing = list.filter((o) => !st.ops.has(o));
  if (!st.session) return null;
  if (missing.length)
    return (
      <Banner tone="ember" title={what ? `${what} needs a newer Kiln.Admin` : "Needs a newer Kiln.Admin"}>
        This server's Kiln.Admin has no {missing.map((m) => `'${m}'`).join(", ")} op yet. Update Kiln.Admin on the server (Servers, Mods) and restart it.
      </Banner>
    );
  return <>{children}</>;
}

/** The players in the world now (from the state push). */
export function usePlayers(): AdminPlayer[] {
  const st = useAdminState();
  return st.state?.players ?? [];
}

export const playerName = (p: AdminPlayer | undefined | null) => p?.name || (p?.entity != null ? `Player ${p.entity}` : "someone");

export function PlayerSelect({ value, onChange, players, placeholder = "Pick a player", exclude, id }: { value: number | null; onChange: (e: number | null) => void; players: AdminPlayer[]; placeholder?: string; exclude?: number; id?: string }) {
  const opts = players.filter((p) => p.entity !== exclude).map((p) => ({ value: String(p.entity), label: playerName(p) }));
  return <Select id={id} value={value == null ? "" : String(value)} onChange={(v) => onChange(v ? Number(v) : null)} options={[{ value: "", label: opts.length ? placeholder : "nobody online" }, ...opts]} />;
}

// ---------------------------------------------------------------- names of what mods add (kiln.content definitions)

/** A mod's item or recipe as content_definitions lists it. */
export interface DefinitionLike {
  kind: string;
  name: string;
  displayName?: string | null;
  definition?: unknown;
}

const textOf = (v: unknown): string | null => (typeof v === "string" && v.length > 0 ? v : null);

/** What players see: the definition's displayName, else its English (or every-language) name in texts; a recipe without
 * a name of its own is named after the first thing it makes (`items`: display names by debug name). Null when the mod
 * gave it no name. */
export function definitionName(d: DefinitionLike, items?: ReadonlyMap<string, string>): string | null {
  const body = (d.definition ?? {}) as { texts?: Record<string, { name?: unknown }>; outputs?: { item?: unknown }[] };
  const own = textOf(d.displayName) ?? textOf(body.texts?.en?.name) ?? textOf(body.texts?.["*"]?.name);
  if (own || d.kind !== "recipe") return own;
  const made = textOf(body.outputs?.[0]?.item);
  return made ? items?.get(made) ?? null : null;
}

/** Display names of the items mods add, by debug name (empty while loading or without content_definitions). */
export function useModItemNames(): ReadonlyMap<string, string> {
  const has = useHasOp("content_definitions");
  const defs = useAdminQuery<DefinitionLike[]>("content_definitions", {}, { enabled: has, staleTime: 30_000 });
  return useMemo(() => {
    const m = new Map<string, string>();
    for (const d of defs.data ?? []) {
      const n = d.kind === "item" ? definitionName(d) : null;
      if (n) m.set(d.name, n);
    }
    return m;
  }, [defs.data]);
}

/** An item_search hit's name as players see it, and its debug name when that differs. */
export function itemNames(i: ItemHit, mod: ReadonlyMap<string, string>): { name: string; debug: string | null } {
  const debug = i.debug ?? (mod.has(i.name) ? i.name : null);
  const name = (debug ? mod.get(debug) : undefined) ?? i.name;
  return { name, debug: debug && debug !== name ? debug : null };
}

// ---------------------------------------------------------------- item picker (item_search over the server's items)

export function ItemPicker({ value, onPick, placeholder = "Search the server's items…", id }: { value: ItemHit | null; onPick: (i: ItemHit | null) => void; placeholder?: string; id?: string }) {
  const [text, setText] = useState(value?.name ?? "");
  const q = useDebounced(text.trim(), 180);
  const has = useHasOp("item_search");
  const search = useAdminQuery<ItemHit[]>("item_search", { q }, { enabled: has && q.length >= 2 && q !== value?.name, staleTime: 60_000 });
  const mod = useModItemNames();
  const sugg = useMemo<Suggestion<ItemHit>[]>(
    () => (search.data ?? []).map((i) => {
      const n = itemNames(i, mod);
      return { key: String(i.id), label: n.name, sub: <span className="k-mono">{n.debug ? `${n.debug} · ` : ""}#{i.id}{i.gear ? " · gear" : ""}</span>, value: { ...i, name: n.name } };
    }),
    [search.data, mod],
  );
  return (
    <SuggestInput
      id={id}
      value={text}
      placeholder={has ? placeholder : "item_search is not available"}
      onChange={(v) => {
        setText(v);
        const m = /^#?(\d{3,})$/.exec(v.trim());
        onPick(m ? { id: Number(m[1]), name: `#${m[1]}`, gear: false } : null);
      }}
      suggestions={q === value?.name ? [] : sugg}
      onPick={(s) => {
        setText(s.value.name);
        onPick(s.value);
      }}
    />
  );
}

// ---------------------------------------------------------------- coordinates

export interface CoordText {
  x: string;
  y: string;
  z: string;
}
export const coordText = (p?: AdminPos | null): CoordText => (p ? { x: String(round1(p.x)), y: String(round1(p.y)), z: String(round1(p.z)) } : { x: "", y: "", z: "" });
export const round1 = (n: number) => Math.round(n * 10) / 10;

/** Parses x/y/z text; null + message when one is missing or out of the world's range (±1e6). */
export function parseCoords(c: CoordText): { pos: AdminPos | null; error: string | null } {
  const v = { x: Number(c.x), y: Number(c.y), z: Number(c.z) };
  if ([c.x, c.y, c.z].some((s) => s.trim() === "") || ![v.x, v.y, v.z].every(Number.isFinite)) return { pos: null, error: "Enter x, y and z." };
  if (![v.x, v.y, v.z].every((n) => n > -1e6 && n < 1e6)) return { pos: null, error: "Coordinates out of range." };
  return { pos: v, error: null };
}

export function CoordInputs({ value, onChange, idPrefix }: { value: CoordText; onChange: (c: CoordText) => void; idPrefix?: string }) {
  return (
    <div className="ka-coords">
      {(["x", "y", "z"] as const).map((k) => (
        <TextInput key={k} id={idPrefix ? `${idPrefix}-${k}` : undefined} inputMode="decimal" placeholder={k === "y" ? "y (height)" : k} aria-label={k} value={value[k]}
          onChange={(e) => onChange({ ...value, [k]: e.target.value })} className="ka-coord" />
      ))}
    </div>
  );
}

// ---------------------------------------------------------------- validation (extensions/kiln.py)

export const STEAM_ID = /^\d{17}$/;
export const NODE = /^(\*|[a-z0-9_:-]+(\.[a-z0-9_:-]+)*(\.\*)?)$/;
export const GROUP = /^[A-Za-z0-9_:-]{1,40}$/;
export const BACKUP_NAME = /^(?!\.)[A-Za-z0-9._-]{1,64}$/;
export const FIELD = /^(\[\d{1,4}\]\.)?[A-Za-z_][A-Za-z0-9_]*(\[\d{1,4}\])?(\.[A-Za-z_][A-Za-z0-9_]*(\[\d{1,4}\])?){0,8}$/;
export const MOD_ID = /^[A-Za-z0-9._-]{1,80}$/;

/** A SteamID64 as text; `strict`: an individual account (zone owners). */
export function steamIdError(v: string, strict = false): string | null {
  const s = v.trim();
  if (!STEAM_ID.test(s) || !s.startsWith("765611") || (strict && BigInt(s) <= 76561197960265728n)) return "A SteamID64 has 17 digits (7656119…).";
  return null;
}

export function nodeError(v: string): string | null {
  const n = v.trim().toLowerCase();
  if (n.startsWith("-")) return "Give the node without '-'; choose Deny instead.";
  if (!n || n.length > 128 || !NODE.test(n)) return "Permission nodes are dot-separated words of a-z 0-9 _ - : (a final .* matches everything below).";
  return null;
}

/** The current server's id (inside an AdminProvider). */
export const useServerId = (): ServerId => useAdminServerId();
