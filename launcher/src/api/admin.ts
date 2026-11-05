// Server administration over KAP/1. Protocol: docs/kiln/launcher/admin-protocol.md; the pages
// and hooks: docs/kiln/launcher/admin-pages.md.
//
// One AdminHub session per server is shared by every admin page: <AdminProvider serverId> (or the first hook that
// names the server) acquires it, the last release closes it 30 s later. The hub connects with admin_connect,
// subscribes to the `state` topic (pushes arrive in `kiln://admin`), reconnects with backoff when the session closes,
// errors or goes quiet, and gates every op on the session's `ready.ops` ("needs a newer Kiln.Admin").
//
// Hooks (both forms work; the one without serverId uses the nearest <AdminProvider>):
//   useAdminState([serverId])          connection + the latest `state` push
//   useHasOp([serverId,] op)           true when the server's Kiln.Admin serves op
//   useAdminCaller([serverId])         (op, args) => Promise<data>  (rejects with KilnError)
//   useAdminCall(op, options?)         TanStack mutation; mutate(args)
//   useAdminQuery(op, args?, options?) TanStack query, refetched per session
import { useMutation, useQuery, useQueryClient, type QueryClient, type UseMutationOptions, type UseQueryOptions } from "@tanstack/react-query";
import { createContext, createElement, useContext, useEffect, useMemo, type ReactNode } from "react";
import { create } from "zustand";
import {
  adminCall, adminConnect, adminDisconnect, adminSubscribe, onKilnEvent, toKilnError,
  type AdminEvent, type AdminSession, type AdminTransport, type Json, type KilnError, type ServerId,
} from "./commands";

// ---------------------------------------------------------------- KAP/1 wire types (Rust: kiln_manage::admin::protocol)

export const KAP_PROTOCOL = "kap/1";

export type KapErrorCode =
  | "bad_frame" | "too_large" | "auth_required" | "auth_failed" | "version" | "unknown_op" | "op_failed" | "busy"
  | "unknown_topic" | "timeout" | "shutting_down" | "internal";

export interface KapLimits {
  maxFrame: number;
  maxClientFrame: number;
  inflight: number;
  pingS: number;
  idleS: number;
}

export type KapFrame =
  | { t: "hello"; proto: string; nonce: string; kiln?: string; admin?: string; server: { id?: string | null; name?: string | null }; auth: string[] }
  | { t: "auth"; proto: string; cnonce: string; mac: string; client?: string }
  | { t: "ready"; session: string; mac: string; ops: string[]; topics: string[]; limits: KapLimits }
  | { t: "req"; id: number; op: string; args: Record<string, Json> }
  | { t: "res"; id: number; ok: boolean; op: string; ms?: number; data?: Json; error?: string; code?: KapErrorCode }
  | { t: "sub"; id: number; topic: string; everyMs?: number }
  | { t: "unsub"; id: number; topic: string }
  | { t: "push"; topic: string; seq: number; data: Json }
  | { t: "ping"; id: number; ts?: number }
  | { t: "pong"; id: number; ts?: number }
  | { t: "error"; code: KapErrorCode; message: string; id?: number }
  | { t: "bye"; reason: string };

/** Topics Kiln.Admin offers (ready.topics lists the server's). */
export type AdminTopic = "state" | "log";

export interface AdminPos {
  x: number;
  y: number;
  z: number;
}

/** A player in the world (Ops.Players.cs PlayerList): name/steamId/admin/permissions, pos, health, god. */
export interface AdminPlayer {
  entity: number;
  name: string;
  steamId?: string;
  admin: boolean;
  permissions: string;
  pos?: AdminPos;
  health?: number;
  healthMax?: number;
  god?: boolean;
  [key: string]: unknown;
}

/** A player death from kiln.combat (state.combatDeaths): victim, killer, origin, where, unix seconds. */
export interface AdminCombatDeath {
  entity: number;
  killer: number;
  origin: string;
  pos: AdminPos;
  time: number;
  victimName?: string;
  killerKind?: "none" | "player" | "creature";
  killerName?: string;
  killerGuid?: string;
}

/** The `state` topic / state.json (AdminMod.State + Ops.AddState); fields grow with services. */
export interface AdminState {
  clock: number;
  kiln: string;
  ready: boolean;
  players?: AdminPlayer[];
  /** health-polling graves (health hit 0): clock = server seconds since boot */
  deaths?: { entity: number; pos: AdminPos; clock: number }[];
  combatDeaths?: AdminCombatDeath[];
  /** enemies spawned by Kiln.Admin that are still alive */
  spawned?: number;
  mods?: { loaded: number; failed: number; disabled: number; phase: string };
  [key: string]: unknown;
}

/** Ops every page can rely on (Ops*.cs); check others with useHasOp. */
export type KnownOp =
  | "ping" | "status" | "players" | "enemies" | "teleport" | "teleport_to_player" | "inventory" | "give_item" | "item_search"
  | "spawn" | "spawn_item" | "despawn_spawned" | "settings_get" | "settings_set" | "settings_save" | "broadcast" | "tell"
  | "command" | "command_run" | "commands" | "time_get" | "time_set" | "weather_reset" | "health" | "heal" | "god" | "kill"
  | "damage" | "save_now" | "save_status" | "backup_list" | "backup_create" | "backup_restore" | "backup_cancel_restore"
  | "gameplay_status" | "zones_list" | "zones_add" | "zones_remove" | "content_status" | "content_get" | "content_set"
  | "content_reset" | "content_fields" | "content_search" | "content_entry" | "content_recipe" | "content_definitions"
  | "content_report" | "content_dump" | "content_reload" | "sync_status" | "config_list" | "config_get" | "config_set"
  | "config_reload" | "permissions_get" | "permissions_check" | "permissions_player_node" | "permissions_player_group"
  | "permissions_group" | "permissions_group_remove" | "permissions_map_user_group" | "permissions_reload"
  | "commands_describe" | "command_complete" | "events_stats" | "mods" | "mod_load" | "mod_reload" | "mod_unload" | "mod_test"
  | "crash_reports"
  // Connection and session state
  | "admin_info" | "network_status" | "network_reload" | "native_status" | "log_tail" | "admin_token_rotate";

// ---------------------------------------------------------------- the hub (one session per server)

export type AdminStatus = "idle" | "connecting" | "ready" | "reconnecting" | "error";

/** Everything the hub knows about one server's admin connection. */
export interface AdminConn {
  serverId: ServerId;
  status: AdminStatus;
  session: AdminSession | null;
  /** the transport asked for (admin_connect); the session says which one it got */
  want: AdminTransport;
  error: KilnError | null;
  state: AdminState | null;
  stateSeq: number;
  /** Date.now() of the last state push */
  stateAt: number | null;
  connectedAt: number | null;
  /** failed connects in a row (backoff) */
  attempt: number;
  /** Date.now() of the next automatic try */
  retryAt: number | null;
  /** pushes that skipped a seq (slow client: Kiln.Admin dropped some) */
  gaps: number;
}

/** The contract's state shape (contracts.md section 13) plus the hub's details. */
export interface AdminHubState extends AdminConn {
  transport: AdminTransport | null;
  connecting: boolean;
  ready: boolean;
  ops: ReadonlySet<string>;
}

export type AdminCall = <T = Json>(op: KnownOp | string, args?: Record<string, Json>) => Promise<T>;

const blank = (serverId: ServerId): AdminConn => ({
  serverId, status: "idle", session: null, want: loadTransport(serverId), error: null, state: null, stateSeq: 0, stateAt: null,
  connectedAt: null, attempt: 0, retryAt: null, gaps: 0,
});

interface HubStore {
  conns: Record<ServerId, AdminConn>;
}
/** The hub's store (zustand). Pages read it through the hooks below. */
export const useAdminHub = create<HubStore>(() => ({ conns: {} }));

const get = (id: ServerId): AdminConn => useAdminHub.getState().conns[id] ?? blank(id);
const patch = (id: ServerId, p: Partial<AdminConn>) =>
  useAdminHub.setState((s) => ({ conns: { ...s.conns, [id]: { ...(s.conns[id] ?? blank(id)), ...p } } }));

const BACKOFF_S = [1, 2, 4, 8, 15, 30];
const RELEASE_MS = 30_000;
/** no state push for this long: the connection bar says the state is stale */
export const ADMIN_STALE_MS = 6_000;

interface Runtime {
  refs: number;
  gen: number;  // bumps on every (re)connect, so late answers of an old attempt are dropped
  retry: ReturnType<typeof setTimeout> | null;
  release: ReturnType<typeof setTimeout> | null;
}
const rt = new Map<ServerId, Runtime>();
const runtime = (id: ServerId): Runtime => {
  let r = rt.get(id);
  if (!r) rt.set(id, (r = { refs: 0, gen: 0, retry: null, release: null }));
  return r;
};

function loadTransport(id: ServerId): AdminTransport {
  try {
    const v = localStorage.getItem(`kiln.admin.transport.${id}`);
    return v === "tcp" || v === "file" || v === "ssh" ? v : "auto";
  } catch {
    return "auto";
  }
}

// one listener for every session's events (registered on first use)
let listening: Promise<() => void> | null = null;
function listen() {
  listening ??= onKilnEvent("kiln://admin", ({ session, serverId, event }) => {
    const c = useAdminHub.getState().conns[serverId];
    if (!c?.session || c.session.session !== session) return;
    if (event.kind === "push") {
      if (event.topic === "state" && event.data && typeof event.data === "object" && !Array.isArray(event.data)) {
        const st = event.data as unknown as AdminState;
        const gap = c.stateSeq > 0 && event.seq > c.stateSeq + 1 ? 1 : 0;
        patch(serverId, { state: st, stateSeq: event.seq, stateAt: Date.now(), gaps: c.gaps + gap });
        for (const l of stateListeners) {
          try {
            l(serverId, st);
          } catch (e) {
            console.error("admin state listener", e);
          }
        }
      } else for (const l of topicListeners) l(serverId, event.topic, event.data);
    } else if (event.kind === "closed") {
      closed(serverId, event.reason, event.retry);
    } else if (event.kind === "error") {
      patch(serverId, { error: { code: "internal", message: event.message } });
    } else if (event.kind === "ready") {
      readyAgain(serverId, event);
    }
  });
  return listening;
}

/** The AdminEvent after every (re)connect of the Rust hub's session (it re-subscribes by itself). */
type ReadyEvent = Extract<AdminEvent, { kind: "ready" }>;
/** Without `retry` (an older backend) a closed session is retried by this hub after this grace time. */
const CLOSED_GRACE_MS = 10_000;

/** closed {retry: true}: the Rust hub reconnects (backoff 1..30 s) and sends `ready`; {retry: false}: final, the user
 * reconnects (the Reconnect button). */
function closed(id: ServerId, reason: string, retry: boolean | undefined) {
  const r = runtime(id);
  const c = get(id);
  if (!c.session) return;
  if (r.retry) clearTimeout(r.retry);
  r.retry = null;
  if (retry === false) {
    r.gen++;
    patch(id, { status: "error", session: null, retryAt: null, error: { code: reason === "auth_failed" || reason === "token" ? "auth_failed" : "admin_unavailable", message: `The admin session closed (${reason}).` } });
    return;
  }
  patch(id, { status: "reconnecting", error: { code: "admin_unavailable", message: `The admin session closed (${reason}); reconnecting…` } });
  if (retry === undefined) {
    const gen = r.gen;
    r.retry = setTimeout(() => {
      r.retry = null;
      if (gen === r.gen && get(id).status === "reconnecting") lost(id, { code: "admin_unavailable", message: `The admin session closed (${reason}).` });
    }, CLOSED_GRACE_MS);
  }
}

function readyAgain(id: ServerId, ev: ReadyEvent) {
  const r = runtime(id);
  const c = get(id);
  if (!c.session) return;
  if (r.retry) clearTimeout(r.retry);
  r.retry = null;
  const session: AdminSession = {
    ...c.session,
    transport: ev.transport ?? c.session.transport,
    ops: ev.ops ?? c.session.ops,
    topics: ev.topics ?? c.session.topics,
  };
  patch(id, { status: "ready", session, error: null, attempt: 0, connectedAt: Date.now(), stateSeq: 0 });
}

type StateListener = (serverId: ServerId, state: AdminState) => void;
type TopicListener = (serverId: ServerId, topic: string, data: Json) => void;
const stateListeners = new Set<StateListener>();
const topicListeners = new Set<TopicListener>();
/** Called with every state push of every server (src/admin/history.ts keeps deaths and seen players). */
export function onAdminState(l: StateListener): () => void {
  stateListeners.add(l);
  return () => stateListeners.delete(l);
}
/** Pushes of other topics (e.g. `log`). */
export function onAdminTopic(l: TopicListener): () => void {
  topicListeners.add(l);
  return () => topicListeners.delete(l);
}

async function connect(id: ServerId) {
  const r = runtime(id);
  if (r.retry) clearTimeout(r.retry);
  r.retry = null;
  const gen = ++r.gen;
  const prev = get(id);
  patch(id, { status: prev.status === "ready" || prev.attempt > 0 || prev.session ? "reconnecting" : "connecting", retryAt: null });
  // admin_connect returns the server's existing hub session whatever the transport: close it first
  if (prev.session) await adminDisconnect(prev.session.session).catch(() => undefined);
  try {
    await listen();
    const session = await adminConnect(id, get(id).want);
    if (gen !== r.gen) {
      adminDisconnect(session.session).catch(() => undefined);
      return;
    }
    patch(id, { status: "ready", session, error: null, attempt: 0, connectedAt: Date.now(), stateSeq: 0, gaps: 0 });
    if (session.topics.includes("state")) {
      await adminSubscribe(session.session, "state").catch((e) => patch(id, { error: toKilnError(e) }));
    }
  } catch (e) {
    if (gen !== r.gen) return;
    lost(id, toKilnError(e));
  }
}

/** No session (admin_connect failed: server stopped, no endpoint, ...): show why and try again later while a page
 * still uses the server. A live session's reconnects are the Rust hub's (closed/ready events). */
function lost(id: ServerId, error: KilnError) {
  const r = runtime(id);
  const c = get(id);
  const attempt = c.attempt + 1;
  const wait = BACKOFF_S[Math.min(BACKOFF_S.length - 1, attempt - 1)] * 1000;
  r.gen++;
  if (c.session) adminDisconnect(c.session.session).catch(() => undefined);
  const retry = r.refs > 0 && error.code !== "auth_failed";
  patch(id, { status: "error", session: null, error, attempt, retryAt: retry ? Date.now() + wait : null });
  if (r.retry) clearTimeout(r.retry);
  r.retry = retry ? setTimeout(() => connect(id), wait) : null;
}

/** A page started using the server's admin session. */
export function acquireAdmin(id: ServerId) {
  const r = runtime(id);
  r.refs++;
  if (r.release) clearTimeout(r.release);
  r.release = null;
  const c = get(id);
  if (c.status === "idle" || (c.status === "error" && !r.retry && c.error?.code !== "auth_failed")) connect(id);
}

/** A page stopped using it; the last one closes the session after 30 s (page changes keep it). */
export function releaseAdmin(id: ServerId) {
  const r = runtime(id);
  r.refs = Math.max(0, r.refs - 1);
  if (r.refs > 0) return;
  if (r.release) clearTimeout(r.release);
  r.release = setTimeout(() => {
    if (r.refs > 0) return;
    r.gen++;
    if (r.retry) clearTimeout(r.retry);
    r.retry = null;
    const c = get(id);
    if (c.session) adminDisconnect(c.session.session).catch(() => undefined);
    patch(id, { ...blank(id), want: c.want });
  }, RELEASE_MS);
}

/** Reconnect now (the "Reconnect" button), optionally over another transport. */
export function reconnectAdmin(id: ServerId, transport?: AdminTransport) {
  if (transport) {
    patch(id, { want: transport });
    try {
      localStorage.setItem(`kiln.admin.transport.${id}`, transport);
    } catch {
      /* private mode */
    }
  }
  patch(id, { attempt: 0 });
  connect(id);
}

const waitReady = (id: ServerId, ms: number) =>
  new Promise<AdminConn>((resolve, reject) => {
    const c = get(id);
    if (c.status === "ready") return resolve(c);
    const t = setTimeout(() => {
      un();
      const now = get(id);
      reject(now.error ?? { code: "admin_unavailable", message: "Not connected to the server's Kiln.Admin." });
    }, ms);
    const un = useAdminHub.subscribe((s) => {
      const n = s.conns[id];
      if (n?.status === "ready") {
        clearTimeout(t);
        un();
        resolve(n);
      }
    });
  });

/** Error for an op this server's Kiln.Admin does not serve. */
export const needsNewer = (op: string): KilnError => ({
  code: "unsupported", message: `This needs a newer Kiln.Admin on the server (it has no '${op}' op yet).`,
});

/** Runs an op on the server's shared session; data on ok, KilnError (op_failed with the op's message) otherwise. */
export async function callAdmin<T = Json>(id: ServerId, op: string, args: Record<string, Json> = {}): Promise<T> {
  const c = get(id).status === "ready" ? get(id) : await waitReady(id, 8000);
  const s = c.session!;
  if (!s.ops.includes(op)) throw needsNewer(op);
  try {
    return await adminCall<T>(s.session, op, args);
  } catch (e) {
    const err = toKilnError(e);
    if (get(id).session?.session === s.session) {
      // not_found: the hub forgot the session (launcher restarted the backend): connect again;
      // admin_unavailable: the hub is reconnecting it: show that until its `ready`
      if (err.code === "not_found") lost(id, err);
      else if (err.code === "admin_unavailable" && get(id).status === "ready") patch(id, { status: "reconnecting", error: err });
    }
    throw err;
  }
}

// ---------------------------------------------------------------- React: provider and hooks

const ServerCtx = createContext<ServerId | null>(null);

/** Shares one admin session for `serverId` with everything below it. */
export function AdminProvider({ serverId, children }: { serverId: ServerId; children?: ReactNode }) {
  useEffect(() => {
    acquireAdmin(serverId);
    return () => releaseAdmin(serverId);
  }, [serverId]);
  return createElement(ServerCtx.Provider, { value: serverId }, children);
}

/** The provider's server id (or the given one). */
export function useAdminServerId(explicit?: ServerId): ServerId {
  const ctx = useContext(ServerCtx);
  const id = explicit ?? ctx;
  if (!id) throw new Error("admin hooks need a serverId or an <AdminProvider>");
  return id;
}

/** True inside an <AdminProvider>. */
export const useInAdminProvider = () => useContext(ServerCtx) !== null;

/** Acquires the session while the calling component is mounted, when it named a server outside a provider. */
function useAcquire(id: ServerId, explicit: boolean) {
  const inside = useContext(ServerCtx) === id;
  useEffect(() => {
    if (!explicit || inside) return;
    acquireAdmin(id);
    return () => releaseAdmin(id);
  }, [id, explicit, inside]);
}

/** The shared session's state for a server: connection, transport, the latest `state` push. */
export function useAdminState(serverId?: ServerId): AdminHubState {
  const id = useAdminServerId(serverId);
  useAcquire(id, serverId !== undefined);
  const c = useAdminHub((s) => s.conns[id]);
  return useMemo(() => {
    const conn = c ?? blank(id);
    return {
      ...conn,
      transport: conn.session?.transport ?? null,
      connecting: conn.status === "connecting" || conn.status === "reconnecting",
      ready: conn.status === "ready",
      ops: new Set(conn.session?.ops ?? []),
    };
  }, [c, id]);
}

/** True when the server's Kiln.Admin serves `op` (ready.ops / status.ops). `useHasOp(op)` or `useHasOp(serverId, op)`. */
export function useHasOp(serverIdOrOp: ServerId | KnownOp | string, op?: KnownOp | string): boolean {
  const explicit = op !== undefined;
  const id = useAdminServerId(explicit ? serverIdOrOp : undefined);
  const name = explicit ? op! : serverIdOrOp;
  return useAdminHub((s) => !!s.conns[id]?.session?.ops.includes(name));
}

/** A function that runs an op on the server's shared session (rejects with KilnError, op_failed for ok=false). */
export function useAdminCaller(serverId?: ServerId): AdminCall {
  const id = useAdminServerId(serverId);
  useAcquire(id, serverId !== undefined);
  return useMemo<AdminCall>(() => (op, args) => callAdmin(id, op, args ?? {}), [id]);
}

export interface AdminMutationOptions<T, A> extends Omit<UseMutationOptions<T, KilnError, A>, "mutationFn"> {
  /** queries of these ops are refetched after a success */
  invalidates?: (KnownOp | string)[];
  serverId?: ServerId;
}

/** An op as a TanStack mutation: `const give = useAdminCall("give_item"); give.mutate({entity, item, count})`. */
export function useAdminCall<T = Json, A extends Record<string, Json> = Record<string, Json>>(op: KnownOp | string, options: AdminMutationOptions<T, A> = {}) {
  const { invalidates, serverId, onSuccess, ...rest } = options;
  const id = useAdminServerId(serverId);
  const qc = useQueryClient();
  return useMutation<T, KilnError, A>({
    mutationKey: ["admin", id, op],
    mutationFn: (args: A) => callAdmin<T>(id, op, args ?? {}),
    onSuccess: (...a) => {
      if (invalidates?.length) invalidateAdmin(qc, id, invalidates);
      return onSuccess?.(...a);
    },
    ...rest,
  });
}

export interface AdminQueryOptions<T> extends Omit<UseQueryOptions<T, KilnError>, "queryKey" | "queryFn"> {
  serverId?: ServerId;
}

/** An op as a TanStack query, keyed per session (a reconnect refetches). Disabled until the op is available. */
export function useAdminQuery<T = Json>(op: KnownOp | string, args: Record<string, Json> = {}, options: AdminQueryOptions<T> = {}) {
  const { serverId, enabled, ...rest } = options;
  const id = useAdminServerId(serverId);
  const session = useAdminHub((s) => (s.conns[id]?.status === "ready" ? s.conns[id]?.session : null));
  const has = !!session?.ops.includes(op);
  return useQuery<T, KilnError>({
    queryKey: ["admin", id, op, args, session?.session ?? null],
    queryFn: () => callAdmin<T>(id, op, args),
    enabled: has && (enabled ?? true),
    retry: false,
    ...rest,
  });
}

/** Refetches the queries of these ops on a server. */
export function invalidateAdmin(qc: QueryClient, serverId: ServerId, ops: (KnownOp | string)[]) {
  const set = new Set(ops);
  return qc.invalidateQueries({ predicate: (q) => q.queryKey[0] === "admin" && q.queryKey[1] === serverId && set.has(String(q.queryKey[2])) });
}
