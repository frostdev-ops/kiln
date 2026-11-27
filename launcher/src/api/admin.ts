// Server administration over KAP/1. Protocol: docs/kiln/launcher/admin-protocol.md; the pages
// and hooks: docs/kiln/launcher/admin-pages.md.
//
// One session per server is shared by every admin page: <AdminProvider serverId> (or the first hook that names the
// server) acquires it, the last release closes it 30 s later. The hub connects through its AdminTransport (below),
// subscribes to the `state` topic, follows the transport's events (closed {retry}, ready, pushes), reconnects with
// backoff when a connect fails, and gates every op on the session's `ready.ops` ("needs a newer Kiln.Admin").
//
// The transport is the seam between the pages and what carries KAP/1 (configureAdmin):
//   - the launcher: ./adminTauri.ts, the Tauri commands admin_* and the `kiln://admin` event (the Rust AdminHub);
//     src/main.tsx installs it;
//   - a web page (Kilnwarden, through @kiln/admin-ui): ./adminWs.ts, KAP/1 frames as JSON over an
//     already-authenticated WebSocket.
// This module and everything the admin pages import is Tauri-free (unit-tests/admin-ui-package.test.ts).
//
// Hooks (both forms work; the one without serverId uses the nearest <AdminProvider>):
//   useAdminState([serverId])          connection + the latest `state` push
//   useHasOp([serverId,] op)           true when the server's Kiln.Admin serves op
//   useAdminCaller([serverId])         (op, args) => Promise<data>  (rejects with KilnError)
//   useAdminCall(op, options?)         TanStack mutation; mutate(args)
//   useAdminQuery(op, args?, options?) TanStack query, refetched per session
//   useKap([serverId])                 typed calls of the newer ops (kapOps)
import { useMutation, useQuery, useQueryClient, type QueryClient, type UseMutationOptions, type UseQueryOptions } from "@tanstack/react-query";
import { createContext, createElement, useContext, useEffect, useMemo, type ReactNode } from "react";
import { create } from "zustand";
import { t } from "../i18n";
import { toKilnError, type Json, type KilnError, type ServerId } from "./shared";
import type {
  AdminInfo, LoginMintResult, LoginRedeemResult, RestartRequest, ServerRestartArgs, UpdateCancelResult, UpdateList,
  UpdateRollbackResult, UpdateStageArgs, UpdateStageResult,
} from "../admin/types";

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
  | { t: "ready"; session: string; mac: string; ops: string[]; topics: string[]; limits: KapLimits; restart?: KapRestart }
  | { t: "req"; id: number; op: string; args: Record<string, Json> }
  | { t: "res"; id: number; ok: boolean; op: string; ms?: number; data?: Json; error?: string; code?: KapErrorCode }
  | { t: "sub"; id: number; topic: string; everyMs?: number }
  | { t: "unsub"; id: number; topic: string }
  | { t: "push"; topic: string; seq: number; data: Json }
  | { t: "ping"; id: number; ts?: number }
  | { t: "pong"; id: number; ts?: number }
  | { t: "error"; code: KapErrorCode; message: string; id?: number }
  | { t: "bye"; reason: string };

/** ready.restart / admin_info.restart (0.2.0-dev.30.9): does a stop asked for with server_restart come back? */
export interface KapRestart {
  supported: "supported" | "unknown";
  /** "kilnctl" (its systemd unit restarts the server) or null */
  via: string | null;
  /** kilnctl run's exit code after such a stop (75), or null */
  code: number | null;
}

/** Topics Kiln.Admin offers (ready.topics lists the server's). "metrics": AdminMetrics every 2 s (since 30.9). */
export type AdminTopic = "state" | "log" | "metrics";

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
  | "admin_info" | "network_status" | "network_reload" | "native_status" | "log_tail" | "admin_token_rotate"
  // Restart and one-time login (0.2.0-dev.30.9; check with useHasOp)
  | "server_restart" | "server_restart_cancel" | "login_mint" | "login_redeem"
  // Staged mod and Kiln updates (0.2.0-dev.30.9, Ops.Updates.cs; check with useHasOp)
  | "update_list" | "update_stage" | "update_cancel" | "update_rollback";

// ---------------------------------------------------------------- the transport seam

/** What the launcher may ask its backend for (Rust `Transport`): automatic, Kiln.Admin's TCP listener, the file bridge,
 * an SSH tunnel to a remote server. A transport lists the ones it offers in `kinds`. */
export type AdminTransportKind = "auto" | "tcp" | "file" | "ssh";

/** One server's admin session as the transport hands it out (Rust kiln_manage::admin::client::AdminSession). `session`
 * is the transport's id for it, stable across its own reconnects; `transport` says what carries it ("ws": a
 * WebSocket). */
export interface AdminSession {
  session: string;
  serverId: ServerId;
  transport: AdminTransportKind | "ws";
  ops: string[];
  topics: string[];
  kiln: string | null;
  server: { id?: string | null; name?: string | null };
  /** ready.restart (0.2.0-dev.30.9) when the transport has it */
  restart?: KapRestart;
}

/** A session's events (Rust AdminEvent): pushes; `closed` (retry: the transport reconnects by itself and sends `ready`
 * again; false: final; absent: an older backend, the hub retries); `error` (a non-fatal KAP error frame); `ready`
 * after every reconnect. */
export type AdminEvent =
  | { kind: "push"; topic: string; seq: number; data: Json }
  | { kind: "closed"; reason: string; retry?: boolean }
  | { kind: "error"; message: string }
  | { kind: "ready"; transport: AdminSession["transport"]; ops: string[]; topics: string[]; restart?: KapRestart };

/** An event with its session (the launcher's `kiln://admin` payload). */
export interface AdminHubEvent {
  session: string;
  serverId: ServerId;
  event: AdminEvent;
}

/** Carries KAP/1 for the hub. Every method rejects with a KilnError: `not_found` for a session it does not know,
 * `admin_unavailable` while it reconnects, the op's error as `op_failed` (busy, timeout for those KAP codes). It keeps
 * one session per server (connect again returns it), re-subscribes after its own reconnects, and reports them as
 * `closed {retry: true}` then `ready`. */
export interface AdminTransport {
  /** For diagnostics: "tauri", "websocket", ... */
  readonly name: string;
  /** The kinds a user may pick (the Reconnect menu); empty: nothing to pick. */
  readonly kinds: readonly AdminTransportKind[];
  connect(serverId: ServerId, want: AdminTransportKind): Promise<AdminSession>;
  disconnect(session: string): Promise<void>;
  /** Runs an op; resolves to its `data`. */
  call<T = Json>(session: string, op: string, args: Record<string, Json>): Promise<T>;
  subscribe(session: string, topic: string): Promise<void>;
  unsubscribe(session: string, topic: string): Promise<void>;
  /** Every session's events; resolves to the unsubscribe function. */
  listen(handler: (e: AdminHubEvent) => void): Promise<() => void>;
}

/** The map art's state where the host makes it from the game (the launcher's Settings, Game art). */
export interface AdminArtStatus {
  map?: boolean;
  extracted?: string | null;
  canExtract?: boolean;
}

/** What the Map page says while it has no map art, in the host's words (AdminHost.useMapNoArt). The launcher's own
 *  (admin.map.artPreparing / artNeedsClient over the map, admin.map.noMapData in the Markers tab) speak of its Game art;
 *  a host that gets the art another way (Kilnwarden: copied from the launcher) says how instead. */
export interface AdminMapNoArt {
  /** The banner over the map: its title; null: no banner (the host says it elsewhere, such as its own notice). */
  title: string | null;
  /** Under the banner's title: a sentence, or the host's own elements (steps, a fix with its progress); taller than the
   *  map, the banner scrolls inside it. */
  body?: ReactNode;
  /** One button in the banner. */
  action?: { label: string; onClick: () => void; busy?: boolean };
  /** The Markers tab's line (in a paragraph: inline content); default: the banner's title, else the launcher's line. */
  markers?: ReactNode;
}

/** A crash report file the host found itself (logs_crash_reports). */
export interface AdminCrashFile {
  name: string;
  created: string;
  size: number;
}

/** What the pages need from the app around them, beyond KAP. A launcher action here is never an op: a KAP op of a
 * similar name (server_restart) goes through the transport (kapOps), these through the host. */
export interface AdminHost {
  /** Base URL of the local game art (map/..., data/...); undefined: /assets-local/ (catalog.ts assetsRoot). */
  assetsRoot?: () => string | undefined;
  /** A hook: the map art's state, to probe again when the host made it. */
  useArtStatus: () => AdminArtStatus | undefined;
  /** A hook: what the Map page says while it has no map art, given the art's state (useArtStatus); undefined: the
   *  launcher's words. Called on every render of the map, art or not. */
  useMapNoArt: (art: AdminArtStatus | undefined) => AdminMapNoArt | undefined;
  /** The launcher reads a stopped server's crash reports from its folder. */
  crashReportsFromDisk?: (serverId: ServerId) => Promise<AdminCrashFile[]>;
  /** The launcher restarts a server it manages (its own server_restart command, not Kiln.Admin's op). */
  restartManagedServer?: (serverId: ServerId) => Promise<unknown>;
}

const noArtStatus = (): AdminArtStatus | undefined => undefined;
const launcherNoArtWords = (): AdminMapNoArt | undefined => undefined;
let transportNow: AdminTransport | null = null;
let hostNow: AdminHost = { useArtStatus: noArtStatus, useMapNoArt: launcherNoArtWords };

/** Sets what the hub talks through and the app around the pages. Sessions of a previous transport are dropped (their
 * timers stopped; nothing is sent to it). The launcher calls this once (./adminTauri.ts); a web page before it renders
 * the pages. */
export function configureAdmin(c: { transport: AdminTransport | null; host?: Partial<AdminHost> }) {
  if (c.transport !== transportNow) {
    for (const r of rt.values()) {
      if (r.retry) clearTimeout(r.retry);
      if (r.release) clearTimeout(r.release);
    }
    rt.clear();
    useAdminHub.setState({ conns: {} });
    const was = listening;
    listening = null;
    was?.then((un) => un()).catch(() => undefined);
    transportNow = c.transport;
  }
  hostNow = { ...c.host, useArtStatus: c.host?.useArtStatus ?? noArtStatus, useMapNoArt: c.host?.useMapNoArt ?? launcherNoArtWords };
}

/** The configured transport; rejects (admin_unavailable) when there is none. */
function transport(): AdminTransport {
  if (!transportNow) throw { code: "admin_unavailable", message: t("api.admin.noTransport") } satisfies KilnError;
  return transportNow;
}

/** The configured transport (null before configureAdmin). */
export const adminTransport = (): AdminTransport | null => transportNow;
/** The app around the pages (configureAdmin's host). */
export const adminHost = (): AdminHost => hostNow;

// ---------------------------------------------------------------- the hub (one session per server)

export type AdminStatus = "idle" | "connecting" | "ready" | "reconnecting" | "error";

/** Everything the hub knows about one server's admin connection. */
export interface AdminConn {
  serverId: ServerId;
  status: AdminStatus;
  session: AdminSession | null;
  /** the transport kind asked for (connect); the session says which one it got */
  want: AdminTransportKind;
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
  transport: AdminSession["transport"] | null;
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

function loadTransport(id: ServerId): AdminTransportKind {
  try {
    const v = localStorage.getItem(`kiln.admin.transport.${id}`);
    return v === "tcp" || v === "file" || v === "ssh" ? v : "auto";
  } catch {
    return "auto";
  }
}

/** The lists of a state push. Kiln.Admin sends arrays; the Emberwatch Lua mod (a ShroudForge mod that answers the
 * same file bridge) writes an empty Lua table as `{}`, and one with gaps as an object keyed by index. */
const STATE_LISTS = ["players", "deaths", "combatDeaths"] as const;

/** A state push with its lists as arrays, whoever wrote it. The same object when nothing had to change. */
function normalizeState(raw: Record<string, unknown>): AdminState {
  let st = raw;
  for (const k of STATE_LISTS) {
    const v = raw[k];
    if (v === undefined || Array.isArray(v)) continue;
    if (st === raw) st = { ...raw };
    st[k] = v && typeof v === "object" ? Object.values(v) : [];
  }
  return st as unknown as AdminState;
}

// one listener for every session's events (registered on first use, again after configureAdmin)
let listening: Promise<() => void> | null = null;
function listen() {
  listening ??= transport().listen(({ session, serverId, event }) => {
    const c = useAdminHub.getState().conns[serverId];
    if (!c?.session || c.session.session !== session) return;
    if (event.kind === "push") {
      if (event.topic === "state" && event.data && typeof event.data === "object" && !Array.isArray(event.data)) {
        const st = normalizeState(event.data as Record<string, unknown>);
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
      // a session the hub ended itself (Reconnect while connected): its transport's `closed` is not the server leaving
      if (ending.delete(session)) return;
      closed(serverId, event.reason, event.retry);
    } else if (event.kind === "error") {
      patch(serverId, { error: { code: "internal", message: event.message } });
    } else if (event.kind === "ready") {
      readyAgain(serverId, event);
    }
  });
  return listening;
}

/** The AdminEvent after every (re)connect of the transport's session (it re-subscribes by itself). */
type ReadyEvent = Extract<AdminEvent, { kind: "ready" }>;
/** Without `retry` (an older backend) a closed session is retried by this hub after this grace time. */
const CLOSED_GRACE_MS = 10_000;

/** closed {retry: true}: the transport reconnects (backoff 1..30 s) and sends `ready`; {retry: false}: final, the user
 * reconnects (the Reconnect button). */
function closed(id: ServerId, reason: string, retry: boolean | undefined) {
  const r = runtime(id);
  const c = get(id);
  if (!c.session) return;
  if (r.retry) clearTimeout(r.retry);
  r.retry = null;
  if (retry === false) {
    r.gen++;
    patch(id, { status: "error", session: null, retryAt: null, error: { code: reason === "auth_failed" || reason === "token" ? "auth_failed" : "admin_unavailable", message: t("api.admin.sessionClosed", { reason }) } });
    return;
  }
  patch(id, { status: "reconnecting", error: { code: "admin_unavailable", message: t("api.admin.sessionClosedReconnecting", { reason }) } });
  if (retry === undefined) {
    const gen = r.gen;
    r.retry = setTimeout(() => {
      r.retry = null;
      if (gen === r.gen && get(id).status === "reconnecting") lost(id, { code: "admin_unavailable", message: t("api.admin.sessionClosed", { reason }) });
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
    ...(ev.restart ? { restart: ev.restart } : {}),
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

/** Sessions the hub is ending itself (their `closed` event is expected; forgotten after a minute). */
const ending = new Set<string>();

/** Ends a session; a transport that is gone or refuses is no reason to stop. */
function disconnectQuietly(session: string): Promise<void> {
  ending.add(session);
  setTimeout(() => ending.delete(session), 60_000);
  try {
    return transport().disconnect(session).catch(() => undefined);
  } catch {
    return Promise.resolve();
  }
}

async function connect(id: ServerId) {
  const r = runtime(id);
  if (r.retry) clearTimeout(r.retry);
  r.retry = null;
  const gen = ++r.gen;
  const prev = get(id);
  patch(id, { status: prev.status === "ready" || prev.attempt > 0 || prev.session ? "reconnecting" : "connecting", retryAt: null });
  // connect returns the server's existing session whatever the kind: close it first
  if (prev.session) await disconnectQuietly(prev.session.session);
  try {
    await listen();
    const session = await transport().connect(id, get(id).want);
    if (gen !== r.gen) {
      disconnectQuietly(session.session);
      return;
    }
    patch(id, { status: "ready", session, error: null, attempt: 0, connectedAt: Date.now(), stateSeq: 0, gaps: 0 });
    if (session.topics.includes("state")) {
      await transport().subscribe(session.session, "state").catch((e) => patch(id, { error: toKilnError(e) }));
    }
  } catch (e) {
    if (gen !== r.gen) return;
    lost(id, toKilnError(e));
  }
}

/** No session (connect failed: server stopped, no endpoint, ...): show why and try again later while a page still uses
 * the server. A live session's reconnects are the transport's (closed/ready events). */
function lost(id: ServerId, error: KilnError) {
  const r = runtime(id);
  const c = get(id);
  const attempt = c.attempt + 1;
  const wait = BACKOFF_S[Math.min(BACKOFF_S.length - 1, attempt - 1)] * 1000;
  r.gen++;
  if (c.session) disconnectQuietly(c.session.session);
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
    if (c.session) disconnectQuietly(c.session.session);
    patch(id, { ...blank(id), want: c.want });
  }, RELEASE_MS);
}

/** Reconnect now (the "Reconnect" button), optionally over another transport kind. */
export function reconnectAdmin(id: ServerId, kind?: AdminTransportKind) {
  if (kind) {
    patch(id, { want: kind });
    try {
      localStorage.setItem(`kiln.admin.transport.${id}`, kind);
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
    const timer = setTimeout(() => {
      un();
      const now = get(id);
      reject(now.error ?? { code: "admin_unavailable", message: t("api.admin.notConnected") });
    }, ms);
    const un = useAdminHub.subscribe((s) => {
      const n = s.conns[id];
      if (n?.status === "ready") {
        clearTimeout(timer);
        un();
        resolve(n);
      }
    });
  });

/** Error for an op this server's Kiln.Admin does not serve. */
export const needsNewer = (op: string): KilnError => ({
  code: "unsupported", message: t("api.admin.needsNewer", { op }),
});

/** Runs an op on the server's shared session; data on ok, KilnError (op_failed with the op's message) otherwise. */
export async function callAdmin<T = Json>(id: ServerId, op: string, args: Record<string, Json> = {}): Promise<T> {
  const c = get(id).status === "ready" ? get(id) : await waitReady(id, 8000);
  const s = c.session!;
  if (!s.ops.includes(op)) throw needsNewer(op);
  try {
    return await transport().call<T>(s.session, op, args);
  } catch (e) {
    const err = toKilnError(e);
    if (get(id).session?.session === s.session) {
      // not_found: the transport forgot the session (the launcher restarted its backend): connect again;
      // admin_unavailable: the transport is reconnecting it: show that until its `ready`
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

// ---------------------------------------------------------------- typed calls of the newer ops

/** Typed calls of Kiln.Admin's newer ops (0.2.0-dev.30.9; admin-protocol.md sections 8 and 11), each a KAP request on
 * the server's session through the hub's transport. They are ops, never launcher actions: `serverRestart` is Kiln.Admin
 * restarting the server it runs in (KAP `server_restart`), not the launcher's restart of a server it manages
 * (AdminHost.restartManagedServer). Check an op with useHasOp before offering it. */
export function kapOps(call: AdminCall) {
  return {
    adminInfo: () => call<AdminInfo>("admin_info"),
    serverRestart: (args: ServerRestartArgs = {}) => call<RestartRequest>("server_restart", { ...args }),
    serverRestartCancel: (args: { by?: string } = {}) => call<{ canceled: true }>("server_restart_cancel", { ...args }),
    loginMint: (args: { consumer: string; label?: string }) => call<LoginMintResult>("login_mint", { ...args }),
    loginRedeem: (args: { consumer: string; secret: string }) => call<LoginRedeemResult>("login_redeem", { ...args }),
    updateList: () => call<UpdateList>("update_list"),
    updateStage: (args: UpdateStageArgs) => call<UpdateStageResult>("update_stage", { ...args } as unknown as Record<string, Json>),
    updateCancel: (args: { unit: string }) => call<UpdateCancelResult>("update_cancel", { ...args }),
    updateRollback: (args: { unit: string }) => call<UpdateRollbackResult>("update_rollback", { ...args }),
  };
}
export type KapOps = ReturnType<typeof kapOps>;

/** kapOps on the server's shared session (the provider's, or the named server's). */
export function useKap(serverId?: ServerId): KapOps {
  const call = useAdminCaller(serverId);
  return useMemo(() => kapOps(call), [call]);
}
