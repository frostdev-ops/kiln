// Browser-only mock of the Tauri backend. commands.ts uses it automatically when
// window.__TAURI_INTERNALS__ is absent (vite dev in a plain browser, Playwright smoke tests in mock mode), and for
// read-only not_implemented commands in dev builds (./fallback.ts). State lives in memory: profiles, library, tasks,
// a fake game that "runs" after Play. Fixtures are built on schema/examples (the contract examples).
// Commands without a mock reject with not_implemented, like the wave-0 Rust stubs.
import type {
  Activation, AddReport, AddedMod, AdminEndpoint, AppInfo, BootJson, BootState, ConfigDocument, ConfigFileInfo, ConfigTarget, CrashReport, Detected, Distribution,
  EditorBackup, GameInstall, GameStatus, ImportReport, InstallReceipt, InstallResult, InstallState, Json, KilnError, LastServer, LaunchRequest, NewerKilnMod,
  LaunchResult, LauncherSettings, LibraryEntry, LibraryRef, LogFile, LogTarget, ModKind, ModpackInstallRequest, ModpackInstallResult, ModsJson,
  NativeJson, NativePlanEntry, NativeUserState, Profile, ProfileChange, ProfileMod, ProfileSummary, ProxyClass, Rejection, ResolvePreview, RuntimeBundle, SaveBackup, SaveSlot,
  SchemaSnapshot, ServerInstance, ServerStatus, Task, TaskState,
} from "./commands";
import { adminMockHandlers } from "../admin/mock";
import { serverMockHandlers } from "../pages/servers/mockServers";
import { updateMockHandlers } from "./mockUpdate";
import { deckMockHandlers } from "../deck/mockDeck";
import { nexusMockHandlers } from "./mockNexus";
import bootExample from "../../../schema/examples/state-boot.json";
import modsExample from "../../../schema/examples/state-mods.json";
import nativeExample from "../../../schema/examples/state-native.json";
import lastServerExample from "../../../schema/examples/state-last-server.json";
import receiptExample from "../../../schema/examples/install-receipt.json";
import endpointExample from "../../../schema/examples/admin-endpoint.json";
import bigStacksSchema from "../../../schema/examples/config-schema.json";
import beastmasterSchema from "../../../schema/examples/config-schema-emf.json";
import kilnJsonSnapshot from "../data/kiln-json.snapshot.json";

const KINDS: ModKind[] = ["kiln", "native", "plugin", "emf"];
const now = () => new Date().toISOString();
const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v));
const hex = (seed: string) => {
  let h = 0x811c9dc5;
  let out = "";
  for (let r = 0; out.length < 64; r++) {
    for (const c of seed + r) h = Math.imul(h ^ c.charCodeAt(0), 16777619) >>> 0;
    out += h.toString(16).padStart(8, "0");
  }
  return out.slice(0, 64);
};
const kdh = (seed: string) => "kdh1:" + hex(seed);

function reject(code: KilnError["code"], message: string, detail?: unknown): never {
  throw { code, message, ...(detail !== undefined ? { detail } : {}) } satisfies KilnError;
}

// ---------------------------------------------------------------- first run

/** `?mock=fresh`: a first start: no game known yet (Detect finds the Steam one), no Kiln in it, no profiles, an empty
 *  library. `?mock=empty`: the same, and Detect finds nothing. Anything else: the seeded launcher below. */
const MODE = typeof location !== "undefined" ? new URLSearchParams(location.search).get("mock") : null;
const FRESH = MODE === "fresh" || MODE === "empty";
/** `?dist=nexus`: the Nexus Mods build (app_info.distribution "nexus": no self-update; mockUpdate.ts answers
 *  "disabled"). Anything else: a direct build. */
export const MOCK_DISTRIBUTION: Distribution = typeof location !== "undefined" && new URLSearchParams(location.search).get("dist") === "nexus" ? "nexus" : "direct";
/** Every URL app_open_url was asked to open (tests: `__kilnMock.opened()`). */
const openedUrls: string[] = [];
/** Deterministic IPC delays/failures for browser regressions; each plan is consumed by one call. */
interface CommandPlan { delay?: number; error?: KilnError }
const commandPlans: Record<string, CommandPlan[]> = typeof window === "undefined" ? {} :
  (window as unknown as { __kilnMockPlans?: Record<string, CommandPlan[]> }).__kilnMockPlans ?? {};
const commandCalls: string[] = [];

// ---------------------------------------------------------------- games

const STEAM = "/home/player/.local/share/Steam";
const steamGame: GameInstall = {
  id: "client-steam", kind: "client", path: `${STEAM}/steamapps/common/Enshrouded`, exe: "enshrouded.exe", appId: 1203620,
  library: STEAM, platform: "proton", prefix: `${STEAM}/steamapps/compatdata/1203620/pfx`, buildId: "26404966",
  exeSha256: "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618", steamFlavor: "native", pathMode: "z", source: "steam",
};
const games: GameInstall[] = FRESH ? [] : [steamGame];
const baseReceipt = receiptExample as unknown as InstallReceipt;
const kilnProxy: ProxyClass = { kind: "kiln", version: "0.2.0-dev", abi: 2, sha256: hex("proxy"), size: 98304, detail: "export KilnProxyInfo" };
const runtime: Record<string, InstallState> = FRESH ? {} : {
  "client-steam": { installed: true, receipt: baseReceipt, proxy: kilnProxy, modified: [], upgradeAvailable: null },
};
const running: Record<string, { pid: number; mode: LaunchRequest["mode"]; nonce: string | null; at: number; profile: string | null } | null> = {};
let pidSeq = 4100;
/** How long profile_activate takes to answer. */
const ACTIVATE_MS = 1500;
/** The next runtime install fails with this message (tests: `__kilnMock.failNextInstall`). */
let failNextInstall: string | null = null;
/** Steam does not load Kiln's proxy (tests: `__kilnMock.setNoProxy`): starts keep play-once.ini, write no boot.json, the
 *  launch option is not set, and the process "started" 31 s before the launch so the launcher's grace has passed. */
let noProxy = false;
/** GameStatus.newerMods per game (tests: `__kilnMock.setNewerMods`). */
const newerMods: Record<string, NewerKilnMod[]> = {};

const bundles: RuntimeBundle[] = [
  { version: "0.2.0-dev+20261104.0f1ffa6", kiln: "0.2.0-dev", file: "runtime/kiln-runtime-0.2.0-dev+20261104.0f1ffa6.zip", sha256: hex("rt2"),
    created: "2026-11-04T10:10:00Z", sides: ["server", "client"], components: ["core", "admin", "emf", "samples"] },
  { version: "0.1.9+20261101.a41c2d0", kiln: "0.1.9", file: "runtime/kiln-runtime-0.1.9+20261101.a41c2d0.zip", sha256: hex("rt1"),
    created: "2026-11-01T18:00:00Z", sides: ["server", "client"], components: ["core", "admin", "samples"] },
];

// ---------------------------------------------------------------- library

interface MockManifest { id: string; name: string; version: string; side: "client" | "server" | "both"; requires?: Record<string, string>;
  incompatible?: string[]; phase?: NativePlanEntry["phase"]; description?: string; clients?: "none" | "kiln" | "mod" }

const seedMods: Record<ModKind, MockManifest[]> = {
  kiln: [
    { id: "new-items", name: "New Items", version: "1.0.0", side: "both", description: "Cloned weapons and armor", clients: "kiln" },
    { id: "big-stacks", name: "Big Stacks", version: "0.3.1", side: "server", description: "Larger stack sizes", clients: "none" },
    { id: "more-hotbars", name: "More Hotbars", version: "2.1.0", side: "client", requires: { "ui-lib": "^1.0" }, description: "Two extra hotbars" },
    { id: "night-sky", name: "Night Sky", version: "0.9.0", side: "client", incompatible: ["weather-plus"], description: "Darker nights" },
    { id: "weather-plus", name: "Weather Plus", version: "1.4.2", side: "both", description: "More weather" },
  ],
  native: [
    { id: "creative-mode", name: "Creative Mode", version: "1.3.0", side: "client", phase: "early" },
    { id: "fps-unlock", name: "FPS Unlock", version: "2.0.0", side: "client", phase: "dllmain" },
    { id: "hud-tweaks", name: "HUD Tweaks", version: "0.4.0", side: "client", phase: "world" },
    { id: "free-cam", name: "Free Cam", version: "1.1.0", side: "client", phase: "world" },
  ],
  emf: [
    { id: "beastmaster", name: "Beastmaster", version: "0.3.3", side: "both" },
    { id: "emf-teleport", name: "Teleport", version: "0.2.0", side: "client" },
  ],
  plugin: [{ id: "my-plugin", name: "my-plugin.dll", version: "1.0.0", side: "client" }],
};

const library: LibraryEntry[] = FRESH ? [] : KINDS.flatMap((kind) => seedMods[kind].map((m) => entryFor(kind, m, "2026-11-0" + (2 + (m.id.length % 3)) + "T09:00:00Z")));

function entryFor(kind: ModKind, m: MockManifest, imported: string, from?: string): LibraryEntry {
  const hash = kdh(kind + m.id + m.version);
  return {
    kind, id: m.id, name: m.name,
    versions: [{
      version: m.version, hash, folder: `library/${kind}/${m.id}/${m.version}+${hash.slice(5, 13)}`, imported,
      side: m.side, clients: m.clients ?? (m.side === "server" ? "none" : "mod"), size: 40_000 + m.id.length * 3_100,
      source: { type: kind === "plugin" ? "file" : "folder", from: from ?? `${m.id}-${m.version}.zip` },
      manifest: { ...m } as unknown as Json,
    }],
  };
}

function manifestOf(kind: ModKind, id: string, version: string): MockManifest | null {
  const e = library.find((l) => l.kind === kind && l.id === id);
  const v = e?.versions.find((x) => x.version === version);
  return (v?.manifest as unknown as MockManifest) ?? null;
}

// ---------------------------------------------------------------- profiles

const pm = (kind: ModKind, id: string, enabled = true) => {
  const m = seedMods[kind].find((x) => x.id === id)!;
  return { id, version: m.version, hash: kdh(kind + m.id + m.version), enabled };
};

const profiles = new Map<string, Profile>(FRESH ? [] : [
  ["ember-run", {
    format: 1, id: "ember-run", name: "Ember run", created: "2026-11-01T10:00:00Z", updated: "2026-11-04T12:00:00Z", generation: 7, notes: "",
    mods: {
      kiln: [pm("kiln", "new-items"), pm("kiln", "big-stacks", false), pm("kiln", "more-hotbars"), pm("kiln", "night-sky"), pm("kiln", "weather-plus")],
      native: [pm("native", "fps-unlock"), pm("native", "creative-mode"), pm("native", "hud-tweaks"), pm("native", "free-cam", false)],
      plugin: [],
      emf: [pm("emf", "beastmaster")],
    },
    dropins: [], dataDir: "shared", server: null,
  }],
  ["vanilla-plus", {
    format: 1, id: "vanilla-plus", name: "Vanilla plus", created: "2026-11-02T08:00:00Z", updated: "2026-11-03T21:15:00Z", generation: 2, notes: "",
    mods: { kiln: [pm("kiln", "weather-plus")], native: [], plugin: [], emf: [] }, dropins: [], dataDir: "shared", server: null,
  }],
  ["server-ember-isles", {
    format: 1, id: "server-ember-isles", name: "Server: Ember Isles", created: "2026-11-03T20:00:00Z", updated: "2026-11-03T20:00:00Z", generation: 1, notes: "",
    mods: { kiln: [pm("kiln", "new-items")], native: [], plugin: [], emf: [] }, dropins: [], dataDir: "profile",
    server: { id: "srv-4b8e2c", name: "Ember Isles", address: "203.0.113.7:15636" },
  }],
]);
const activations: Record<string, Activation | null> = {};

function fingerprint(p: Profile): string {
  const parts = KINDS.flatMap((k) => p.mods[k].filter((m) => m.enabled).map((m) => `${k}\0${m.id}\0${m.version}\0${m.hash}`)).sort();
  return "kms1:" + hex(parts.join("\n"));
}

function activate(pid: string, gameId: string): Activation {
  const p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
  const g = gameOf(gameId);
  if (running[gameId]) reject("game_running", "Close the game first.");
  const prev = activations[gameId];
  const a: Activation = {
    gameId, profileId: pid, generation: (prev?.generation ?? 6) + 1, pathMode: g.pathMode, profileJson: `${g.path}/kiln/profile.json`,
    copied: 0, fingerprint: fingerprint(p), activated: now(),
  };
  activations[gameId] = a;
  return a;
}
if (!FRESH) activate("ember-run", "client-steam");

function summary(p: Profile): ProfileSummary {
  return {
    id: p.id, name: p.name, updated: p.updated, server: p.server,
    counts: Object.fromEntries(KINDS.map((k) => [k, p.mods[k].filter((m) => m.enabled).length])) as Record<ModKind, number>,
    active: Object.entries(activations).filter(([, a]) => a?.profileId === p.id).map(([g]) => g),
  };
}

function slug(name: string): string {
  const base = name.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40) || "profile";
  let id = base, n = 2;
  while (profiles.has(id)) id = `${base}-${n++}`;
  return id;
}

function resolve(p: Profile, side: "client" | "server"): ResolvePreview {
  const enabled = p.mods.kiln.filter((m) => m.enabled);
  const rejected: Rejection[] = [];
  const ok = new Map<string, MockManifest>();
  for (const m of enabled) {
    const man = manifestOf("kiln", m.id, m.version);
    if (!man) { rejected.push({ id: m.id, dir: m.id, state: "failed", reason: "kiln.mod.json: not found in the library" }); continue; }
    if (side === "client" && man.side === "server") { rejected.push({ id: m.id, dir: m.id, state: "skipped", reason: "side 'server': not loaded on a client" }); continue; }
    if (side === "server" && man.side === "client") { rejected.push({ id: m.id, dir: m.id, state: "skipped", reason: "side 'client': not loaded on a server" }); continue; }
    ok.set(m.id, man);
  }
  for (const [id, man] of [...ok]) {
    for (const [dep, range] of Object.entries(man.requires ?? {})) {
      if (!ok.has(dep)) { ok.delete(id); rejected.push({ id, dir: id, state: "failed", reason: `missing dependency ${dep} ${range}` }); }
    }
  }
  for (const [id, man] of [...ok]) {
    for (const bad of man.incompatible ?? []) {
      const other = ok.get(bad);
      if (other) { ok.delete(id); rejected.push({ id, dir: id, state: "failed", reason: `incompatible with ${bad} ${other.version} (declared in its incompatible list)` }); }
    }
  }
  const order = [...ok.keys()].sort();
  const native: NativePlanEntry[] = p.mods.native.map((m) => {
    const man = manifestOf("native", m.id, m.version);
    return { id: m.id, version: m.version, phase: man?.phase ?? "early", enabled: m.enabled, reason: m.enabled ? null : "disabled in the profile" };
  });
  const warnings: string[] = [];
  if (native.some((n) => n.enabled && n.phase === "dllmain")) warnings.push("dllmain mods load inside the proxy; a bad one can hang the start.");
  return { side, kiln: { order, rejected: rejected.sort((a, b) => a.dir.localeCompare(b.dir)) }, native, warnings };
}

// ---------------------------------------------------------------- tasks

const tasks = new Map<string, Task>();
const cancels = new Set<string>();
let taskSeq = 0;

function emitTask(t: Task) { tasks.set(t.id, t); mockEmit("kiln://task", clone(t)); }

/** A fake long task: `steps` progress messages, then `finish()` gives the result (or throws a KilnError). */
function startTask(kind: string, title: string, steps: string[], finish: () => unknown, stepMs = 450): string {
  const id = `t${++taskSeq}-${kind}`;
  const t: Task = { id, kind, title, state: "running", progress: 0, message: steps[0] ?? null, started: now(), finished: null };
  emitTask(t);
  let i = 0;
  const tick = () => {
    if (cancels.has(id)) { emitTask({ ...t, state: "canceled", message: "Canceled", finished: now(), error: { code: "canceled", message: "Canceled." } }); return; }
    i++;
    if (i < steps.length) { emitTask({ ...t, progress: i / steps.length, message: steps[i] }); setTimeout(tick, stepMs); return; }
    let state: TaskState = "done";
    let result: unknown = null;
    let error: KilnError | undefined;
    try { result = finish(); } catch (e) { state = "failed"; error = e as KilnError; }
    emitTask({ ...t, state, progress: 1, message: error ? error.message : "Done", finished: now(), result, error });
  };
  setTimeout(tick, stepMs);
  return id;
}

// ---------------------------------------------------------------- game state

function gameOf(id: unknown): GameInstall {
  return games.find((g) => g.id === id) ?? reject("not_found", `No game '${String(id)}'.`);
}

/** Games started in this session (a first run has no boot.json before its first start). */
const launched = new Set<string>();

function bootFor(gameId: string): BootJson | null {
  const r = running[gameId];
  if (!r && FRESH && !launched.has(gameId)) return null;
  if (r && noProxy) return null;
  if (!r || Date.now() - r.at < 2500 || r.mode === "vanilla") return r ? null : clone(bootExample as unknown as BootJson);
  const b = clone(bootExample as unknown as BootJson);
  b.pid = r.pid;
  b.written = new Date(r.at + 2500).toISOString();
  b.playOnce = { mode: r.mode === "once" ? "profile" : r.mode === "no-mods" ? "no-mods" : "none", nonce: r.nonce };
  const p = r.profile ? profiles.get(r.profile) : null;
  b.profile = { ...b.profile, mode: r.mode === "no-mods" ? "no-mods" : r.mode === "once" ? "once" : "active", id: p?.id ?? null, name: p?.name ?? null,
    generation: activations[gameId]?.generation ?? 1 };
  return b;
}

function status(gameId: string): GameStatus {
  const g = gameOf(gameId);
  const r = running[gameId];
  return {
    id: g.id, running: !!r, pid: r?.pid ?? null, started: r ? new Date(r.at - (noProxy ? 31_000 : 0)).toISOString() : null, steamUpdating: false, runtime: runtime[gameId] ?? { installed: false, receipt: null, proxy: null, modified: [], upgradeAvailable: null },
    activeProfile: activations[gameId]?.profileId ?? null, playOncePending: !!r && r.mode !== "profile" && (noProxy || Date.now() - r.at < 2500),
    boot: bootFor(gameId), launchOptions: noProxy ? { value: null, hasDllOverride: false, advice: 'WINEDLLOVERRIDES="dinput8=n,b" %command%' }
      : { value: 'WINEDLLOVERRIDES="dinput8=n,b" %command%', hasDllOverride: true, advice: null },
    newerMods: newerMods[gameId] ?? [],
  };
}

function emitGame(gameId: string, kind: "started" | "stopped" | "boot" | "mods" | "native" | "status") {
  const s = status(gameId);
  mockEmit("kiln://game", { gameId, kind, pid: s.pid, status: s });
}

function launch(gameId: string, req: LaunchRequest): LaunchResult {
  const g = gameOf(gameId);
  if (running[gameId]) reject("game_running", "The game is already running.");
  if (req.mode === "once" && !req.profileId) reject("invalid_argument", "Pick a profile to play once.");
  const st = runtime[gameId];
  if (!st?.installed && req.mode !== "vanilla") reject("unsupported", "Kiln is not installed in this game.");
  const pid = ++pidSeq;
  const nonce = req.mode === "profile" ? null : hex(String(pid)).slice(0, 32);
  const profile = req.mode === "once" ? req.profileId ?? null : req.mode === "profile" ? activations[gameId]?.profileId ?? null : null;
  let backup: SaveBackup | null = null;
  if (!req.skipBackup && req.mode !== "vanilla") backup = makeBackup(gameId, "launch", null, profile);
  running[gameId] = { pid, mode: req.mode, nonce, at: Date.now(), profile };
  launched.add(gameId);
  setTimeout(() => emitGame(gameId, "started"), 300);
  setTimeout(() => emitGame(gameId, "boot"), 2600);
  setTimeout(() => emitGame(gameId, "mods"), 3200);
  return { gameId: g.id, mode: req.mode, nonce, backup, uri: "steam://rungameid/1203620", started: now() };
}

/** Dev/test helper: the fake game was started outside the launcher (Steam) `secondsAgo` seconds ago. */
export function mockStartOutside(secondsAgo: number, gameId = "client-steam") {
  if (running[gameId]) return;
  running[gameId] = { pid: ++pidSeq, mode: "profile", nonce: null, at: Date.now() - secondsAgo * 1000, profile: activations[gameId]?.profileId ?? null };
  emitGame(gameId, "started");
}

/** Dev/test helper: the fake game exits. */
export function mockStopGame(gameId = "client-steam") {
  if (!running[gameId]) return;
  running[gameId] = null;
  emitGame(gameId, "stopped");
}

// ---------------------------------------------------------------- last server (Play's refusal banner)

// <game>/kiln/state/last-server.json and dismissed.json, filtered like commands/modpack.rs (worth_showing).
let lastServer: LastServer | null = FRESH ? null : clone(lastServerExample as unknown as LastServer);
const dismissedOffers: Record<string, string> = {};
const dismissKey = (l: LastServer) => (l.modpack ? l.modpack.sha256 : `refused ${l.written}`);
function pendingLastServer(): LastServer | null {
  const l = lastServer;
  if (!l || dismissedOffers[l.server.id ?? ""] === dismissKey(l)) return null;
  return l.outcome === "rejected" || (l.outcome === "welcome" && !!l.modpack && l.missing.length > 0) ? l : null;
}
/** Dev/test helper: the game wrote a new last-server.json (the watcher's kiln://last-server follows). */
export function mockSetLastServer(l: LastServer | null, gameId = "client-steam") {
  lastServer = l;
  if (l) mockEmit("kiln://last-server", { gameId, lastServer: l });
}

// ---------------------------------------------------------------- saves

const saveSlots: SaveSlot[] = [
  { kind: "world", file: "3ad85aea", path: "userdata/12345678/1203620/remote/3ad85aea", modified: "2026-11-04T09:40:00Z", size: 2_884_112, steamId: "12345678" },
  { kind: "world", file: "3ad85aea-1", path: "userdata/12345678/1203620/remote/3ad85aea-1", modified: "2026-11-04T09:30:00Z", size: 2_880_000, steamId: "12345678" },
  { kind: "character", file: "characters", path: "userdata/12345678/1203620/remote/characters", modified: "2026-11-04T09:40:00Z", size: 96_220, steamId: "12345678" },
];
const backups: Record<string, SaveBackup[]> = FRESH ? {} : {
  "client-steam": [
    { id: "20261104T094000Z", created: "2026-11-04T09:40:00Z", reason: "launch", profileId: "ember-run", fingerprint: "kms1:" + hex("a"), note: null, files: 3, bytes: 5_860_332 },
    { id: "20261103T201500Z", created: "2026-11-03T20:15:00Z", reason: "modpack", profileId: "server-ember-isles", fingerprint: "kms1:" + hex("b"), note: null, files: 3, bytes: 5_812_004 },
    { id: "20261102T180000Z", created: "2026-11-02T18:00:00Z", reason: "manual", profileId: null, fingerprint: null, note: "before boss", files: 3, bytes: 5_700_120 },
  ],
};

function makeBackup(gameId: string, reason: SaveBackup["reason"], note: string | null, profileId: string | null = activations[gameId]?.profileId ?? null): SaveBackup {
  const d = new Date();
  const id = d.toISOString().replace(/[-:]/g, "").replace(/\.\d+/, "");
  const b: SaveBackup = { id, created: d.toISOString(), reason, profileId, fingerprint: profileId ? fingerprint(profiles.get(profileId)!) : null, note,
    files: saveSlots.length, bytes: saveSlots.reduce((s, x) => s + x.size, 0) };
  backups[gameId] = [b, ...(backups[gameId] ?? [])].slice(0, settings.savesKeep);
  return b;
}

// ---------------------------------------------------------------- configs

interface MockFile { text: string; values: Json; schema: SchemaSnapshot | null; mod: string | null; kind: ConfigFileInfo["kind"]; modified: string }
const newItemsSchema: SchemaSnapshot = {
  format: 1, source: "kiln", file: "new-items.json", valueFormat: "jsonc", id: "new-items", mod: "new-items", modVersion: "1.0.0", kind: "common",
  written: "2026-11-04T09:00:00Z", kiln: "0.2.0-dev",
  sections: [{ path: "loot", comment: "Where new items drop", synced: false }],
  entries: [
    { path: "enabled", type: "bool", comment: "Add the new items", default: true, requiresRestart: true, synced: true },
    { path: "rarityBoost", type: "float", comment: "Extra chance for rare drops", default: 1.0, requiresRestart: false, synced: false, min: 0.5, max: 4 },
    { path: "maxPerChest", type: "int", comment: null, default: 2, requiresRestart: false, synced: false, min: 0, max: 10 },
    { path: "loot.biomes", type: "list<string>", comment: "Biomes with new loot", default: ["springlands", "revelwood"], requiresRestart: false, synced: false },
    { path: "loot.respawn", type: "duration", comment: "Chest refill time", default: "30m", requiresRestart: false, synced: false },
    { path: "loot.tier", type: "enum", comment: null, default: "normal", requiresRestart: false, synced: false, options: ["low", "normal", "high"] },
  ],
};
const configs = new Map<string, Map<string, MockFile>>();
const targetKey = (t: ConfigTarget) => JSON.stringify(t);

function configFolder(t: ConfigTarget): Map<string, MockFile> {
  const k = targetKey(t);
  let f = configs.get(k);
  if (!f) {
    f = new Map();
    const files: [string, SchemaSnapshot | null, Json, ConfigFileInfo["kind"]][] = [
      ["new-items.json", newItemsSchema, { enabled: true, rarityBoost: 1.5, loot: { biomes: ["springlands"] } }, "kiln"],
      ["big-stacks.json", bigStacksSchema as unknown as SchemaSnapshot, { limits: { multiplier: 4 } }, "kiln"],
      ["emf/beastmaster.ini", beastmasterSchema as unknown as SchemaSnapshot, { general: { max_pets: 3, enabled: true } }, "emf"],
    ];
    for (const [file, schema, values, kind] of files)
      f.set(file, { text: JSON.stringify(values, null, 2), values, schema, mod: schema?.mod ?? null, kind, modified: "2026-11-04T09:00:00Z" });
    configs.set(k, f);
  }
  return f;
}

function setPath(obj: Json, path: string, value: Json | undefined): Json {
  const root = (obj && typeof obj === "object" && !Array.isArray(obj) ? clone(obj) : {}) as Record<string, Json>;
  const parts = path.split(".");
  let cur = root;
  for (const p of parts.slice(0, -1)) {
    const next = cur[p];
    cur[p] = next && typeof next === "object" && !Array.isArray(next) ? next : {};
    cur = cur[p] as Record<string, Json>;
  }
  const last = parts[parts.length - 1];
  if (value === undefined) delete cur[last];
  else cur[last] = value;
  return root;
}

function validate(schema: SchemaSnapshot | null, values: Json) {
  if (!schema) return [];
  const errors: { path: string; message: string }[] = [];
  const get = (p: string) => p.split(".").reduce<Json | undefined>((o, k) => (o && typeof o === "object" && !Array.isArray(o) ? (o as Record<string, Json>)[k] : undefined), values);
  for (const e of schema.entries) {
    const v = get(e.path);
    if (v === undefined) continue;
    if ((e.type === "int" || e.type === "float") && typeof v === "number") {
      if (e.min !== undefined && v < e.min) errors.push({ path: e.path, message: `below the minimum ${e.min}` });
      if (e.max !== undefined && v > e.max) errors.push({ path: e.path, message: `above the maximum ${e.max}` });
    }
    if (e.type === "enum" && e.options && !e.options.includes(String(v))) errors.push({ path: e.path, message: `not one of ${e.options.join(", ")}` });
  }
  return errors;
}

function doc(file: string, f: MockFile): ConfigDocument {
  return { file, text: f.text, values: f.values, schema: f.schema, errors: validate(f.schema, f.values) };
}

const kilnJson: Record<string, MockFile> = {};
function kilnFile(gameId: string): MockFile {
  gameOf(gameId);
  return (kilnJson[gameId] ??= {
    text: '{\n  "policy": "verified-only",\n  "clr": true,\n  "plugins": true,\n  "log_level": "info"\n}\n',
    values: { policy: "verified-only", clr: true, plugins: true, log_level: "info" }, schema: kilnJsonSnapshot as unknown as SchemaSnapshot,
    mod: "kiln", kind: "other", modified: "2026-11-03T23:00:00Z",
  });
}

const nativeStates = new Map<string, NativeUserState>();

// ---------------------------------------------------------------- logs

const KILN_LOG = [
  "[I] [kiln] Kiln 0.2.0-dev starting (client, proxy abi 2)",
  "[I] [kiln] config: policy=verified-only clr=1 plugins=1 native.policy=always",
  "[I] [kiln] build ce6fdba6…: verified (svn 1285611)",
  "[I] [kiln.profile] profile ember-run gen 7: 4 kiln, 3 native, 1 emf",
  "[I] [native] early: creative-mode 1.3.0 loaded (3.1 ms)",
  "[I] [kiln] sigdb: 412/412 symbols resolved",
  "[I] [kiln] services: kiln.example@1 kiln.profile@1 kiln.content@2 kiln.net@1",
  "[I] [plugins] kiln-emf 0.2.0-dev loaded",
  "[I] [clr] .NET 10.0.0 started in 412 ms",
  "[I] [host] resolve: 3 to load, 2 rejected",
  "[W] [host] more-hotbars: missing dependency ui-lib ^1.0",
  "[W] [host] night-sky: incompatible with weather-plus 1.4.2 (declared in its incompatible list)",
  "[I] [host] new-items 1.0.0 loaded (18 ms)",
  "[I] [host] weather-plus 1.4.2 loaded (6 ms)",
  "[I] [kiln] boot complete in 96 ms; handing control to the game",
  "[I] [world] client world started",
  "[I] [native] world: hud-tweaks 0.4.0 loaded (1.4 ms)",
];
const GAME_LOG = ["Enshrouded 0.8.1.0 (svn 1285611)", "Init renderer: Vulkan", "Loading world 'Test World'", "Player Alder joined", "Autosave done (2.8 MB)"];
const PROXY_LOG = ["proxy 0.2.0-dev abi 2", "play-once: none", "chain: kiln\\chain\\emf-dinput8.dll loaded", "profile: fps-unlock.dll loaded", "core: kiln-core.dll loaded"];
const tickStats = Array.from({ length: 40 }, (_, i) => JSON.stringify({
  time: new Date(Date.now() - (40 - i) * 60_000).toISOString().slice(0, 19), steps: 3580 + (i % 5) * 4, rate: 59.6 + Math.sin(i / 3) * 0.3, clock_rate: 60,
  step_ms: { p50: 1.1 + Math.sin(i / 4) * 0.2, p99: 4.2 + Math.abs(Math.sin(i / 2.5)) * 3, max: 12 + (i % 7) },
  pre_ms: { p50: 0.2, p99: 2.1, max: 6 }, post_ms: { p50: 0.02, p99: 0.4, max: 1.2 }, interval_ms: { p50: 16.7, p99: 18.2, max: 25 }, subs: { pre: 14, post: 1 },
}));
const logFiles: Record<string, string[]> = {
  "kiln/logs/kiln.log": KILN_LOG, "kiln/logs/proxy.log": PROXY_LOG, "enshrouded.log": GAME_LOG, "kiln/logs/tick-stats.jsonl": tickStats,
  "kiln/logs/mods/new-items.log": ["[I] new-items: 14 items registered", "[I] new-items: loot tables patched"],
};
const kindOf = (n: string): LogFile["kind"] => (n.endsWith("proxy.log") ? "proxy" : n.includes("/mods/") ? "mod" : n.startsWith("kiln/") ? "kiln" : "game");
const follows = new Map<string, ReturnType<typeof setInterval>>();
let subSeq = 0;
const crashes: CrashReport[] = [{ name: "2026-11-03T22-10-04Z-night-sky.txt", created: "2026-11-03T22:10:04Z", size: 4_211 }];

// ---------------------------------------------------------------- settings, servers

const assets = { root: "/home/player/.local/share/kiln", editor: { present: true, bytes: 0 }, admin: { map: false, catalog: false, bytes: 0 } };
let settings: LauncherSettings = { steamRoots: [], defaultGameId: FRESH ? null : "client-steam", allowHttpModpacks: false, savesKeep: 10, updateCheck: true, animatedFire: true,
  addToActiveProfile: true };

const server: ServerInstance = {
  id: "lab", name: "Lab server", kind: "local", dir: "/home/player/srv/enshrouded/lab", port: 26090, queryPort: 26091,
  wine: "ge:GE-Proton10-3", policy: "any", source: "kilnctl", remote: null,
};
const runningServer: ServerStatus = {
  id: server.id, state: "running", pid: 5120, uptimeS: 3600, players: 0, kiln: "0.2.0-dev", installed: "0.2.0-dev+20261104.0f1ffa6",
  boot: { ...(bootExample as unknown as BootJson), side: "server" }, endpoint: endpointExample as unknown as AdminEndpoint,
};
let serverStatus: ServerStatus = runningServer;
/** Tests (`__kilnMock.setServer`): the lab server stopped and with or without Kiln in its folder, or back to running. */
function mockSetServer(o: { running?: boolean; installed?: string | null }) {
  const installed = o.installed === undefined ? runningServer.installed : o.installed;
  serverStatus = !(o.running ?? true)
    ? { ...runningServer, state: "stopped", pid: null, uptimeS: null, players: null, kiln: null, boot: null, endpoint: null, installed }
    : installed ? { ...runningServer, installed } : { ...runningServer, installed: null, kiln: null, boot: null, endpoint: null };
  mockEmit("kiln://server", { serverId: server.id, status: serverStatus });
}

// ---------------------------------------------------------------- handlers

type Handler = (args: Record<string, unknown>) => unknown;
const S = (v: unknown) => String(v ?? "");

const handlers: Record<string, Handler> = {
  // app
  app_info: (): AppInfo => ({ version: "0.2.0-dev", kiln: "0.2.0-dev", platform: "linux", arch: "x86_64", dataDir: "/home/player/.local/share/kiln", commit: "0f1ffa6", mock: true, noWebgl: false,
    distribution: MOCK_DISTRIBUTION }),
  app_settings_get: () => settings,
  app_settings_set: (a) => (settings = { ...(a.settings as LauncherSettings) }),
  app_open_path: () => null,
  app_open_url: (a) => { if (!S(a.url).startsWith("https://")) reject("refused", "Only https links open."); openedUrls.push(S(a.url)); return null; },
  task_list: () => [...tasks.values()],
  task_get: (a) => tasks.get(S(a.taskId)) ?? reject("not_found", "No such task."),
  task_cancel: (a) => { cancels.add(S(a.taskId)); return null; },
  app_assets_status: () => assets,
  app_assets_import: (a) => startTask("app_assets_import", a.kind === "admin" ? "Import map assets" : "Import editor art", ["Copying fonts", "Copying icons", "Copying textures"],
    () => { if (a.kind === "admin") assets.admin = { map: true, catalog: true, bytes: 55_000_000 }; else assets.editor = { present: true, bytes: 24_000_000 }; return assets; }),

  // game
  game_list: () => games,
  // a first start: Detect takes a moment, then finds the Steam install (or nothing, `?mock=empty`)
  game_detect: async () => {
    if (!FRESH) return games;
    await new Promise((ok) => setTimeout(ok, 1600));
    if (MODE === "fresh" && !games.some((g) => g.id === steamGame.id)) games.push(steamGame);
    return games;
  },
  game_add: (a) => {
    const path = S(a.path).replace(/\/+$/, "");
    if (!path) reject("invalid_argument", "Give a game folder.");
    if (games.some((g) => g.path === path)) reject("already_exists", "That folder is already listed.");
    const g: GameInstall = { ...(games[0] ?? steamGame), id: `client-${hex(path).slice(0, 8)}`, path, library: null, source: "manual", buildId: null, steamFlavor: null };
    games.push(g);
    return g;
  },
  game_remove: (a) => { const i = games.findIndex((g) => g.id === a.gameId); if (i < 0) reject("not_found", "No such game."); games.splice(i, 1); return null; },
  game_status: (a) => status(S(a.gameId)),

  // runtime
  runtime_list: () => bundles,
  runtime_import: (a) => {
    const name = S(a.path).split(/[\\/]/).pop() ?? "";
    const m = /^kiln-runtime-(.+)\.zip$/.exec(name);
    if (!m) reject("parse", "Not a Kiln runtime bundle.", { path: a.path });
    const b: RuntimeBundle = { ...bundles[0], version: m![1], file: `runtime/${name}`, sha256: hex(name), created: now() };
    if (!bundles.some((x) => x.version === b.version)) bundles.push(b);
    // like bundle::list: newest first by version, build date, then when built (never the git id)
    const key = (v: string) => { const [base, build = ""] = v.split("+"); return [...base.split("-")[0].split(".").map((n) => n.padStart(6, "0")), build.split(".")[0]].join("."); };
    bundles.sort((x, y) => key(y.version).localeCompare(key(x.version)) || (y.created ?? "").localeCompare(x.created ?? ""));
    return b;
  },
  runtime_status: (a) => status(S(a.gameId)).runtime,
  runtime_classify: (a) => {
    const p = S(a.path).toLowerCase();
    const kind: ProxyClass["kind"] = p.includes("shroudforge") ? "shroudforge" : p.includes("emf") ? "emf" : p.includes("kiln") ? "kiln" : p.endsWith(".dll") ? "unknown" : "notdll";
    return { kind, version: null, abi: null, sha256: hex(p), size: 81920, detail: kind === "emf" ? "export emf_loader_identity" : null } satisfies ProxyClass;
  },
  runtime_install: (a) => {
    const gameId = S(a.gameId), version = S(a.version);
    const g = gameOf(gameId);
    if (running[gameId]) reject("game_running", "Close the game first.");
    const b = bundles.find((x) => x.version === version) ?? reject("not_found", `No bundle ${version}.`);
    const opts = (a.options ?? {}) as { switchFromShroudforge?: boolean; components?: string[] | null };
    // like the backend: a part this side cannot take is refused before the task starts
    const wrong = opts.components?.find((c) => ["admin", "samples", "sigdb-server"].includes(c));
    if (wrong) reject("invalid_argument", `component ${wrong} is not for the client side`);
    const fail = failNextInstall;
    failNextInstall = null;
    return startTask("runtime_install", `Install Kiln ${b.kiln}`, ["Staging", "Verifying hashes", "Classifying dinput8.dll", "Backing up", "Placing files", "Writing receipt"], () => {
      const st = runtime[gameId];
      if (fail) reject("io", fail);
      if (st?.proxy?.kind === "shroudforge" && !opts.switchFromShroudforge) reject("refused", "ShroudForge is installed. Switch from ShroudForge to replace it.");
      const receipt: InstallReceipt = { ...baseReceipt, installed: st?.receipt?.installed ?? now(), updated: now(), runtime: { version: b.version, sha256: b.sha256, components: opts.components ?? b.components } };
      // like the backend: Kiln files changed by hand are replaced, their copies kept in the install's backup
      const changed = st?.modified ?? [];
      runtime[gameId] = { installed: true, receipt, proxy: kilnProxy, modified: [], upgradeAvailable: null };
      emitGame(g.id, "status");
      const backup = changed.length ? `/home/player/.local/share/kiln/backups/install/${receipt.installId}/install-${now().replace(/[-:]/g, "").slice(0, 15)}Z` : null;
      return { ...receipt, changedBackedUp: changed, backup } satisfies InstallResult;
    });
  },
  runtime_uninstall: (a) => {
    const gameId = S(a.gameId);
    if (running[gameId]) reject("game_running", "Close the game first.");
    return startTask("runtime_uninstall", "Uninstall Kiln", ["Restoring dinput8.dll", "Removing files", "Cleaning chain.ini"], () => {
      const prev = runtime[gameId];
      const emf = prev?.receipt?.replaced?.some((r) => r.class === "emf");
      runtime[gameId] = { installed: false, receipt: null, proxy: emf ? { kind: "emf", version: "0.3.3", abi: null, sha256: hex("emf"), size: 70_000, detail: "export emf_loader_identity" } : null, modified: [], upgradeAvailable: null };
      emitGame(gameId, "status");
      return null;
    });
  },
  runtime_rollback: (a) => handlers.runtime_install({ ...a, options: {} }),

  // profiles
  profile_list: () => [...profiles.values()].map(summary).sort((a, b) => a.name.localeCompare(b.name)),
  profile_get: (a) => clone(profiles.get(S(a.profileId)) ?? reject("not_found", `No profile '${S(a.profileId)}'.`)),
  profile_create: (a) => {
    const name = S(a.name).trim();
    if (!name) reject("invalid_argument", "Give the profile a name.");
    const from = a.copyFrom ? profiles.get(S(a.copyFrom)) ?? reject("not_found", "No such profile to copy.") : null;
    const p: Profile = { format: 1, id: slug(name), name, created: now(), updated: now(), generation: 1, notes: "",
      mods: from ? clone(from.mods) : { kiln: [], native: [], plugin: [], emf: [] }, dropins: from ? [...from.dropins] : [], dataDir: from?.dataDir ?? "shared", server: null };
    profiles.set(p.id, p);
    return clone(p);
  },
  profile_save: (a) => {
    const p = a.profile as Profile;
    const cur = profiles.get(p.id) ?? reject("not_found", `No profile '${p.id}'.`);
    if (cur.generation !== p.generation) reject("conflict", "The profile changed elsewhere. Reload it.");
    const next = { ...clone(p), generation: p.generation + 1, updated: now() };
    profiles.set(p.id, next);
    return clone(next);
  },
  profile_delete: (a) => {
    const id = S(a.profileId);
    if (!profiles.has(id)) reject("not_found", `No profile '${id}'.`);
    if (Object.values(activations).some((x) => x?.profileId === id)) reject("conflict", "Active profiles can't be deleted. Activate another first.");
    profiles.delete(id);
    return null;
  },
  profile_resolve: (a) => resolve(profiles.get(S(a.profileId)) ?? reject("not_found", "No such profile."), (a.side as "client" | "server") ?? "client"),
  // the real activation answers after it has resolved and written the profile (seconds with big mods): the UI must
  // not wait for the answer to show the new name
  profile_activate: async (a) => {
    const r = activate(S(a.profileId), S(a.gameId));
    await new Promise((ok) => setTimeout(ok, ACTIVATE_MS));
    emitGame(S(a.gameId), "status");
    return r;
  },
  profile_deactivate: (a) => { if (running[S(a.gameId)]) reject("game_running", "Close the game first."); activations[S(a.gameId)] = null; emitGame(S(a.gameId), "status"); return null; },
  profile_active: (a) => activations[S(a.gameId)] ?? null,
  profile_revert: (a) => {
    const id = S(a.profileId);
    const p = clone(profiles.get(id) ?? reject("not_found", `No profile '${id}'.`));
    let changed = false;
    for (const c of [...((a.changes as ProfileChange[]) ?? [])].reverse()) {
      const list = p.mods[c.kind];
      const i = list.findIndex((m) => m.id === c.after.id);
      if (i < 0 || list[i].version !== c.after.version || list[i].hash !== c.after.hash || list[i].enabled !== c.after.enabled) continue;
      if (c.before) list[i] = c.before; else list.splice(i, 1);
      changed = true;
    }
    if (!changed) return p;
    const next = { ...p, generation: p.generation + 1, updated: now() };
    profiles.set(id, next);
    reapplyWhereActive(id);
    return clone(next);
  },
  profile_ensure_default: (a) => {
    const gameId = S(a.gameId);
    gameOf(gameId);
    if (!runtime[gameId]?.installed) reject("unsupported", "Kiln is not in this game yet: install it first.");
    if (activations[gameId]) return activations[gameId];
    if (profiles.size) return null;
    if (running[gameId]) reject("game_running", "Close the game first: the new profile is activated in the game folder.");
    makeDefault();
    const r = activate("my-mods", gameId);
    emitGame(gameId, "status");
    return r;
  },

  // library
  library_list: (a) => clone(a.kind ? library.filter((l) => l.kind === a.kind) : library),
  library_detect: (a) => detect(S(a.path)),
  library_import: (a) => {
    const paths = (a.paths as string[]) ?? [];
    if (!paths.length) reject("invalid_argument", "Nothing to import.");
    return startTask("library_import", paths.length === 1 ? `Import ${paths[0].split(/[\\/]/).pop()}` : `Import ${paths.length} items`,
      ["Reading", "Hashing", "Detecting", "Copying"], () => importPaths(paths), 300);
  },
  library_add: (a) => {
    const gameId = S(a.gameId);
    const paths = (a.paths as string[]) ?? [];
    if (!paths.length) reject("invalid_argument", "Nothing to add.");
    gameOf(gameId);
    if (!runtime[gameId]?.installed) reject("unsupported", "Kiln is not in this game yet: install it first.");
    const makeIt = !activations[gameId] && profiles.size === 0;
    if (makeIt && running[gameId]) reject("game_running", "Close the game first: the new profile is activated in the game folder.");
    return startTask("library_add", paths.length === 1 ? `Add ${paths[0].split(/[\\/]/).pop()}` : `Add ${paths.length} items`,
      ["Reading", "Hashing", "Adding to the profile", "Writing profile.json"], () => quickAdd(gameId, paths, makeIt), 300);
  },
  library_lint: () => ({ format: 1, tool: "kiln-lint", version: "mock", ok: true, checked: [], summary: { errors: 0, warnings: 0, infos: 0, hints: 0 }, catalog: null, diagnostics: [] }),
  library_remove: (a) => {
    const r = a.entry as LibraryRef;
    const used = [...profiles.values()].filter((p) => p.mods[r.kind].some((m) => m.id === r.id && m.version === r.version));
    if (used.length && !a.force) reject("conflict", `Used by ${used.map((p) => p.name).join(", ")}.`, { profiles: used.map((p) => p.id) });
    const e = library.find((l) => l.kind === r.kind && l.id === r.id);
    if (!e) reject("not_found", "Not in the library.");
    e!.versions = e!.versions.filter((v) => v.version !== r.version);
    if (!e!.versions.length) library.splice(library.indexOf(e!), 1);
    return null;
  },

  // configs
  config_list: (a) => [...configFolder(a.target as ConfigTarget).entries()].map(([file, f]): ConfigFileInfo =>
    ({ file, mod: f.mod, kind: f.kind, hasSchema: !!f.schema, modVersion: f.schema?.modVersion ?? null, modified: f.modified })),
  config_read: (a) => { const f = configFolder(a.target as ConfigTarget).get(S(a.file)) ?? reject("not_found", `No config ${S(a.file)}.`); return doc(S(a.file), f); },
  config_set: (a) => {
    const f = configFolder(a.target as ConfigTarget).get(S(a.file)) ?? reject("not_found", `No config ${S(a.file)}.`);
    const values = setPath(f.values, S(a.path), a.value as Json);
    const errs = validate(f.schema, values).filter((e) => e.path === a.path);
    if (errs.length) reject("schema", `${S(a.path)}: ${errs[0].message}.`, errs);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2), modified: now() });
    return doc(S(a.file), f);
  },
  config_write: (a) => {
    const f = configFolder(a.target as ConfigTarget).get(S(a.file)) ?? reject("not_found", `No config ${S(a.file)}.`);
    let values: Json;
    try { values = JSON.parse(S(a.text)); } catch { reject("parse", "Not valid JSON."); }
    Object.assign(f, { values: values!, text: S(a.text), modified: now() });
    return doc(S(a.file), f);
  },
  config_reset: (a) => {
    const f = configFolder(a.target as ConfigTarget).get(S(a.file)) ?? reject("not_found", `No config ${S(a.file)}.`);
    const values = a.path ? setPath(f.values, S(a.path), undefined) : {};
    Object.assign(f, { values, text: JSON.stringify(values, null, 2), modified: now() });
    return doc(S(a.file), f);
  },
  config_native_state: (a) => nativeStates.get(targetKey(a.target as ConfigTarget)) ?? { format: 1, mods: {} },
  config_native_set: (a) => {
    const k = targetKey(a.target as ConfigTarget);
    const st = clone(nativeStates.get(k) ?? { format: 1, mods: {} });
    if (a.enabled === null || a.enabled === undefined) delete st.mods[S(a.modId)];
    else st.mods[S(a.modId)] = { enabled: !!a.enabled };
    nativeStates.set(k, st);
    return st;
  },
  config_kiln_read: (a) => doc("kiln.json", kilnFile(S(a.gameId))),
  config_kiln_set: (a) => {
    const f = kilnFile(S(a.gameId));
    const values = setPath(f.values, S(a.path), a.value as Json);
    const errs = validate(f.schema, values).filter((e) => e.path === a.path);
    if (errs.length) reject("schema", `${S(a.path)}: ${errs[0].message}.`, errs);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2) + "\n", modified: now() });
    return doc("kiln.json", f);
  },
  config_kiln_reset: (a) => {
    const f = kilnFile(S(a.gameId));
    const values = setPath(f.values, S(a.path), undefined);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2) + "\n", modified: now() });
    return doc("kiln.json", f);
  },

  // launch
  launch_play: (a) => launch(S(a.gameId), a.request as LaunchRequest),
  launch_state: (a): BootState => {
    const boot = bootFor(S(a.gameId));
    const r = running[S(a.gameId)];
    // the example's run logged two errors after its boot (a service tick at the menu); a run started here has none
    const log = !boot ? null : r ? { errors: 0, first: [] } : { errors: 2, first: [
      "[E 12:01:20.749] [kiln] kiln service tick 'kiln creature brains' threw (1x): System.InvalidOperationException: wrong thread",
      "[E 12:01:22.073] [kiln] kiln service tick 'kiln creature brains' threw (2x): System.InvalidOperationException: wrong thread",
    ] };
    return { boot, mods: boot ? (modsExample as unknown as ModsJson) : null, native: boot ? (nativeExample as unknown as NativeJson) : null, stale: !r && !!boot, log };
  },

  // logs
  logs_list: (a) => {
    const t = a.target as LogTarget;
    const base = "game" in t ? gameOf(t.game).path : server.dir;
    return Object.entries(logFiles).map(([name, lines]): LogFile => ({ name, path: `${base}/${name}`, size: lines.join("\n").length, modified: now(), kind: kindOf(name) }));
  },
  logs_read: (a) => {
    const lines = logFiles[S(a.file)] ?? reject("not_found", `No log ${S(a.file)}.`);
    const text = lines.join("\n") + "\n";
    return { file: S(a.file), offset: 0, next: text.length, size: text.length, text, truncated: false };
  },
  logs_follow: (a) => {
    const id = `log${++subSeq}`;
    const file = S(a.file);
    let n = 0;
    follows.set(id, setInterval(() => {
      const lines = file.endsWith("kiln.log") ? [`[I] [world] step ${++n * 600}: 14 subscribers, 1.1 ms p50`] : file.endsWith(".log") ? [`tick ${++n}`] : [];
      if (!lines.length) return;
      logFiles[file]?.push(...lines);
      mockEmit("kiln://log", { subscription: id, target: a.target, file, lines, next: 0 });
    }, 2000));
    return id;
  },
  logs_unfollow: (a) => { clearInterval(follows.get(S(a.subscription))); follows.delete(S(a.subscription)); return null; },
  logs_crash_reports: () => crashes,

  // saves
  saves_list: (a) => { gameOf(a.gameId); return saveSlots; },
  saves_backups: (a) => backups[S(a.gameId)] ?? [],
  saves_backup: (a) => makeBackup(S(a.gameId), "manual", (a.note as string | null) ?? null),
  saves_restore: (a) => {
    if (running[S(a.gameId)]) reject("game_running", "Close the game before restoring.");
    if (!(backups[S(a.gameId)] ?? []).some((b) => b.id === a.backupId)) reject("not_found", "No such backup.");
    makeBackup(S(a.gameId), "restore", `before restoring ${S(a.backupId)}`);
    return null;
  },
  saves_delete: (a) => { backups[S(a.gameId)] = (backups[S(a.gameId)] ?? []).filter((b) => b.id !== a.backupId); return null; },

  // editor: the browser has no save files (the editor shows its empty state) unless a local fixture is there
  // (launcher/assets-local/fixtures/editor/: copies of your own saves plus their zstd blocks, never committed)
  editor_list_saves: async () => (await editorFixture())?.saves ?? [],
  editor_file_info: () => ({ modified: Date.now() }),
  editor_read_file: async (a) => {
    const r = await fetch(EDITOR_FIXTURE + S(a.path).split("/").pop());
    if (!r.ok) reject("not_found", "No such save.");
    return r.arrayBuffer();
  },
  editor_zstd_decompress: async (a) => {
    const d = a.data as number[];
    const key = `${d.length}:${d.slice(0, 24).map((b) => b.toString(16).padStart(2, "0")).join("")}`;
    const b64 = (await editorFixture())?.blocks[key];
    if (!b64) reject("unsupported", "The browser mock can only read the fixture's own blocks.");
    return Uint8Array.from(atob(b64!), (c) => c.charCodeAt(0)).buffer;
  },
  editor_game_running: () => Object.values(running).some(Boolean),
  editor_list_backups: (): EditorBackup[] => [],
  editor_open_backups: () => null,

  // modpack (the refusal banner on Play); servers: the server mock handlers below override these
  modpack_pending: () => pendingLastServer(),
  server_list: () => [server],
  server_get: () => server,
  server_status: () => serverStatus,
  server_deploy: (a) => {
    const version = S(a.version);
    const b = bundles.find((x) => x.version === version) ?? reject("not_found", `No runtime bundle ${version}.`);
    if (serverStatus.pid) reject("conflict", "Stop the server before installing Kiln.");
    return startTask("server_deploy", `Installing Kiln ${version} on ${server.id}`, ["Verifying the bundle", "Staging", "Placing files", "Writing receipt"], () => {
      serverStatus = { ...serverStatus, installed: b.version };
      mockEmit("kiln://server", { serverId: server.id, status: serverStatus });
      const receipt: InstallReceipt = { ...baseReceipt, side: "server", installed: now(), runtime: { version: b.version, sha256: b.sha256, components: b.components } };
      return { ...receipt, changedBackedUp: [], backup: null } satisfies InstallResult;
    });
  },
  admin_sessions: () => [],
  ...serverMockHandlers,
  // the consent flow's install (Play's offer, or a modpack zip dropped on the window): a profile bound to the server
  modpack_install: (a) => {
    const gameId = S(a.gameId);
    const req = a.request as ModpackInstallRequest;
    if (!req?.consent) reject("consent_required", "Installing a modpack runs code from the server on your PC: confirm first.");
    if (running[gameId]) reject("game_running", "Close the game first.");
    return startTask("modpack_install", "path" in req.source ? `Installing modpack ${req.source.path.split(/[\\/]/).pop()}` : `Installing modpack from ${req.source.url}`,
      ["Checking the modpack", "Importing into the library", "Setting up the profile", "Backing up saves"], (): ModpackInstallResult => {
        const report = importPaths(["ember-lanterns-1.1.0.zip", "isle-weather-0.4.0.zip"]);
        const refs = [...report.imported, ...report.existing];
        const bound = [...profiles.values()].find((p) => p.server?.id === "srv-4b8e2c");
        const p: Profile = bound ? clone(bound) : { format: 1, id: slug("Server: Ember Isles"), name: "Server: Ember Isles", created: now(), updated: now(), generation: 1, notes: "",
          mods: { kiln: [], native: [], plugin: [], emf: [] }, dropins: [], dataDir: "profile", server: { id: "srv-4b8e2c", name: "Ember Isles" } };
        p.mods = { kiln: [], native: [], plugin: [], emf: [] };
        for (const r of refs) p.mods[r.kind].push({ id: r.id, version: r.version, hash: r.hash, enabled: true });
        p.generation += bound ? 1 : 0;
        profiles.set(p.id, p);
        const backup = req.activate === false ? null : makeBackup(gameId, "modpack", null, p.id);
        const activation = req.activate === false ? null : activate(p.id, gameId);
        emitGame(gameId, "status");
        return { profile: clone(p), imported: report.imported, backup, activation };
      });
  },
  // after the server mocks: Play's Dismiss has to stick
  modpack_dismiss: (a) => { if (lastServer && (lastServer.server.id ?? "") === S(a.serverId)) dismissedOffers[S(a.serverId)] = dismissKey(lastServer); return null; },
};
Object.assign(handlers, adminMockHandlers(mockEmit));
Object.assign(handlers, updateMockHandlers(mockEmit, MOCK_DISTRIBUTION));
Object.assign(handlers, deckMockHandlers(handlers));
Object.assign(handlers, nexusMockHandlers(handlers, mockEmit, startTask, quickAdd));

const EDITOR_FIXTURE = "/assets-local/fixtures/editor/";
let editorFix: Promise<{ saves: { path: string; file: string; modified: number }[]; blocks: Record<string, string> } | null> | null = null;
function editorFixture() {
  editorFix ??= fetch(EDITOR_FIXTURE + "index.json").then((r) => (r.ok ? r.json() : null)).catch(() => null);
  return editorFix;
}

/** "My mods": the profile a first-time player gets (every drop-in folder on, like kiln_manage::quick). */
function makeDefault(): Profile {
  const p: Profile = { format: 1, id: slug("My mods"), name: "My mods", created: now(), updated: now(), generation: 2, notes: "",
    mods: { kiln: [], native: [], plugin: [], emf: [] }, dropins: [...KINDS], dataDir: "shared", server: null };
  profiles.set(p.id, p);
  return p;
}

/** profile_save's follow-up: the games where the profile is active (and stopped) get profile.json again. */
function reapplyWhereActive(pid: string) {
  for (const [g, act] of Object.entries(activations)) {
    if (act?.profileId !== pid || running[g]) continue;
    activate(pid, g);
    emitGame(g, "status");
  }
}

/** library_add (kiln_manage::quick::add): import, add to the active profile ("My mods" when there is none at all), apply. */
function quickAdd(gameId: string, paths: string[], makeIt: boolean): AddReport {
  const r: AddReport = { import: { imported: [], existing: [], skipped: [] }, profileId: null, profileName: null, created: false, added: [], already: [],
    changes: [], applied: false, running: !!running[gameId], activation: null, missing: [], modpacks: [] };
  const files: string[] = [];
  for (const p of paths) {
    if (detect(p).kind !== "modpack") files.push(p);
    else if (/\.zip$/i.test(p)) r.modpacks.push(p);
    else r.import.skipped.push({ path: p, reason: "a modpack folder: drop its zip to install it" });
  }
  if (files.length) { const rep = importPaths(files); r.import = { ...rep, skipped: [...rep.skipped, ...r.import.skipped] }; }
  const refs = [...r.import.imported, ...r.import.existing];
  if (!refs.length) return r;
  const active = activations[gameId]?.profileId ?? null;
  let p: Profile;
  if (active) p = clone(profiles.get(active)!);
  else if (makeIt) {
    p = makeDefault();
    r.activation = activate(p.id, gameId);
    r.created = r.applied = true;
  } else return r;
  r.profileId = p.id;
  r.profileName = p.name;
  const named = (x: { kind: ModKind; id: string; version: string; hash: string }): AddedMod =>
    ({ kind: x.kind, id: x.id, version: x.version, hash: x.hash, name: library.find((l) => l.kind === x.kind && l.id === x.id)?.name ?? x.id });
  for (const ref of refs) {
    const after: ProfileMod = { id: ref.id, version: ref.version, hash: ref.hash, enabled: true };
    const list = p.mods[ref.kind];
    const i = list.findIndex((m) => m.id === ref.id);
    if (i >= 0 && JSON.stringify(list[i]) === JSON.stringify(after)) { r.already.push(named(ref)); continue; }
    r.changes.push({ kind: ref.kind, before: i >= 0 ? list[i] : null, after });
    if (i >= 0) list[i] = after; else list.push(after);
  }
  if (!r.changes.length) return r;
  profiles.set(p.id, { ...p, generation: p.generation + 1, updated: now() });
  if (!running[gameId]) { r.activation = activate(p.id, gameId); r.applied = true; emitGame(gameId, "status"); }
  const has = (id: string) => p.mods.kiln.some((m) => m.enabled && m.id === id) || p.mods.native.some((m) => m.enabled && m.id === id);
  for (const c of r.changes) {
    for (const [dep, range] of Object.entries(manifestOf(c.kind, c.after.id, c.after.version)?.requires ?? {}))
      if (!has(dep) && !r.missing.some((m) => m.id === dep)) r.missing.push({ id: dep, range, by: c.after.id });
  }
  r.added = r.changes.map((c) => named({ kind: c.kind, ...c.after }));
  return r;
}

function detect(path: string): Detected {
  const name = path.split(/[\\/]/).pop() ?? path;
  const lower = name.toLowerCase();
  const base = lower.replace(/\.(zip|dll)$/, "").replace(/[-_ ]?v?(\d+\.\d+(\.\d+)?)$/, "");
  const version = /(\d+\.\d+\.\d+)/.exec(lower)?.[1] ?? "1.0.0";
  if (lower.startsWith("kiln-runtime-")) return { kind: "runtime", path, id: null, version: lower.slice(13).replace(/\.zip$/, ""), name, reason: "kiln-runtime.json at the root", items: [] };
  if (lower.endsWith(".dll")) return { kind: lower.includes("plugin") ? "plugin" : "bare-dll", path, id: base, version: null, name,
    reason: lower.includes("plugin") ? "exports kiln_plugin_main" : "a DLL without kiln_plugin_main; imports as a native mod", items: [] };
  // a pack zip with the mod in a folder inside (`<name>-pack.zip` holds `<name>/kiln.mod.json`)
  if (lower.endsWith("-pack.zip")) {
    const inner = detect(`${path.replace(/-pack\.zip$/i, "")}.zip`);
    return { kind: "unknown", path, id: null, version: null, name, reason: "a folder with 1 importable item(s)", items: [{ ...inner, path: `${path}/${inner.id}` }] };
  }
  if (lower.includes("modpack")) return { kind: "modpack", path, id: null, version: null, name, reason: "kiln-modpack.json at the root", items: [] };
  if (lower.includes("emf")) return { kind: "emf", path, id: base, version, name, reason: "emf.json", items: [] };
  if (lower.includes("native")) return { kind: "native", path, id: base, version, name, reason: "native.json", items: [] };
  if (lower.endsWith(".zip") || !lower.includes(".")) return { kind: "kiln", path, id: base, version, name, reason: "kiln.mod.json", items: [] };
  return { kind: "unknown", path, id: null, version: null, name, reason: "No kiln.mod.json, native.json, emf.json or DLL", items: [] };
}

function importPaths(paths: string[]): ImportReport {
  const report: ImportReport = { imported: [], existing: [], skipped: [] };
  for (const p of paths.flatMap((x) => { const d = detect(x); return d.kind === "unknown" && d.items.length ? d.items.map((i) => i.path) : [x]; })) {
    const d = p.includes(".zip/") ? detect(p.split(".zip/")[0].replace(/-pack$/i, "") + ".zip") : detect(p);
    const kind: ModKind | null = d.kind === "bare-dll" ? "native" : d.kind === "kiln" || d.kind === "native" || d.kind === "emf" || d.kind === "plugin" ? d.kind : null;
    if (!kind || !d.id) { report.skipped.push({ path: p, reason: d.reason }); continue; }
    const version = d.version ?? "1.0.0";
    const ex = library.find((l) => l.kind === kind && l.id === d.id);
    const hash = kdh(kind + d.id + version);
    const ref: LibraryRef = { kind, id: d.id, version, hash };
    if (ex?.versions.some((v) => v.version === version)) { report.existing.push(ref); continue; }
    // a seed mod's file ("more-hotbars-2.1.0.zip") keeps the seed's manifest (its name, requires, side)
    const seed = seedMods[kind].find((x) => x.id === d.id);
    const m: MockManifest = seed ? { ...seed, version } : { id: d.id, name: d.id.replace(/-/g, " ").replace(/\b\w/g, (c) => c.toUpperCase()), version, side: "both", phase: kind === "native" ? "early" : undefined };
    const fresh = entryFor(kind, m, now(), p);
    if (ex) ex.versions.unshift(fresh.versions[0]);
    else library.push(fresh);
    report.imported.push(ref);
  }
  return report;
}

export async function mockInvoke<T>(cmd: string, args: Record<string, unknown>): Promise<T> {
  commandCalls.push(cmd);
  const plan = commandPlans[cmd]?.shift();
  if (plan?.delay) await new Promise((r) => setTimeout(r, plan.delay));
  if (plan?.error) throw plan.error;
  await new Promise((r) => setTimeout(r, 30)); // async like IPC
  const h = handlers[cmd];
  if (!h) reject("not_implemented", `${cmd} has no mock (and no backend in a plain browser)`);
  const r = (await h(args)) ?? null;
  return (r instanceof ArrayBuffer ? r.slice(0) : clone(r)) as T;
}

type Listener = (payload: unknown) => void;
const listeners = new Map<string, Set<Listener>>();

export function mockListen(name: string, handler: Listener): Promise<() => void> {
  const set = listeners.get(name) ?? new Set<Listener>();
  set.add(handler);
  listeners.set(name, set);
  return Promise.resolve(() => set.delete(handler));
}

/** Tests and dev tools: fire a fake backend event in mock mode. */
export function mockEmit(name: string, payload: unknown): void {
  listeners.get(name)?.forEach((h) => h(payload));
}

// Playwright and the dev console reach the fake game through this (mock mode only).
if (typeof window !== "undefined") (window as unknown as { __kilnMock?: unknown }).__kilnMock = { stopGame: mockStopGame, emit: mockEmit, setLastServer: mockSetLastServer,
  calls: () => [...commandCalls],
  planCommand: (cmd: string, plan: CommandPlan) => { (commandPlans[cmd] ??= []).push(plan); },
  failNextInstall: (message: string) => { failNextInstall = message; },
  setModified: (files: string[], gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], modified: files }; emitGame(gameId, "status"); },
  startOutside: mockStartOutside, setServer: mockSetServer, opened: () => [...openedUrls],
  setNoProxy: (on: boolean, gameId = "client-steam") => { noProxy = on; emitGame(gameId, "status"); },
  setNewerMods: (mods: NewerKilnMod[], gameId = "client-steam") => { newerMods[gameId] = mods; emitGame(gameId, "status"); },
  setUpgradeAvailable: (version: string | null, gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], upgradeAvailable: version }; emitGame(gameId, "status"); } };
