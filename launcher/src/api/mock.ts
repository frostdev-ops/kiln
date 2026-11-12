// Browser-only mock of the Tauri backend. commands.ts uses it automatically when
// window.__TAURI_INTERNALS__ is absent (vite dev in a plain browser, Playwright smoke tests in mock mode), and for
// read-only not_implemented commands in dev builds (./fallback.ts). State lives in memory: profiles, library, tasks,
// a fake game that "runs" after Play. Fixtures are built on schema/examples (the contract examples).
// Commands without a mock reject with not_implemented, like the wave-0 Rust stubs.
import type {
  Activation, AddReport, BundlePart, RunSide, AddedMod, AdminEndpoint, AppInfo, BootJson, BootState, ConfigDocument, ConfigFileInfo, ConfigTarget, CrashReport, ReportFinding, ReportSaved, Detected, Distribution,
  EditorBackup, FoundMod, FoundReport, GameInstall, GameStatus, ImportReport, InstallReceipt, InstallResult, InstallState, Json, KilnError, LastServer, LaunchRequest, NewerKilnMod,
  LaunchResult, LauncherSettings, RenderInfo, LibraryEntry, LibraryRef, LogFile, LogTarget, ModKind, ModpackInstallRequest, ModpackInstallResult, ModsJson,
  NativeJson, NativePlanEntry, NativeUserState, PreStart, Profile, ProfileChange, ProfileMod, ProfileSummary, ProxyClass, Rejection, RemoveReport, ResolvePreview, RuntimeBundle, SaveBackup, SaveSlot,
  SchemaSnapshot, ServerInstance, ServerStatus, Task, TaskState,
} from "./commands";
import { adminMockHandlers } from "../admin/mock";
import { serverMockHandlers } from "../pages/servers/mockServers";
import { updateMockHandlers } from "./mockUpdate";
import { deckMockHandlers } from "../deck/mockDeck";
import { nexusMockHandlers, NEXUS_MOCK_ENABLED } from "./mockNexus";
import { chainMockHandlers } from "./mockChain";
import { dataFolderBlocks, dataFolderMockHandlers, mockDataDir, mockPickFolder } from "./mockDataFolder";
import bootExample from "../../../schema/examples/state-boot.json";
import modsExample from "../../../schema/examples/state-mods.json";
import nativeExample from "../../../schema/examples/state-native.json";
import lastServerExample from "../../../schema/examples/state-last-server.json";
import receiptExample from "../../../schema/examples/install-receipt.json";
import endpointExample from "../../../schema/examples/admin-endpoint.json";
import bigStacksSchema from "../../../schema/examples/config-schema.json";
import beastmasterSchema from "../../../schema/examples/config-schema-emf.json";
import kilnJsonSnapshot from "../data/kiln-json.snapshot.json";
import { MOD_KINDS, emptyMods } from "./kinds";
import { DROPIN_KINDS, dropinKinds } from "../pages/profiles/dropins";
import { deselectedFor, partRows, resolveParts, selectParts } from "../shell/parts";

/** Kiln's own four kinds (the drop-in folders); MOD_KINDS (./kinds.ts) has every kind. */
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
interface CommandPlan { delay?: number; error?: KilnError; result?: unknown }
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
/** What `launch_prestart` answers while the fake game runs (tests: `__kilnMock.setPreStart`); null: nothing before boot. */
let preStart: PreStart | null = null;
/** GameStatus.otherFolderProfile per game (tests: `__kilnMock.setOtherFolderProfile`): another data folder's profile is
 *  in the game; activeProfile is null then, as the backend says it. */
const otherFolder: Record<string, { id: string; root?: string | null } | null> = {};
/** GameStatus.newerMods per game (tests: `__kilnMock.setNewerMods`). */
const newerMods: Record<string, NewerKilnMod[]> = {};

function mockPart(name: string, sides: RunSide[] = ["server", "client"], isDefault = true): BundlePart {
  return { name, sides, default: isDefault, requires: name === "core" ? [] : ["core"] };
}

/** Like install::status: missing defaults and requirements that a reinstall adds. */
function withMissing(st: InstallState, side: RunSide): InstallState {
  const r = st.receipt;
  const b = r && bundles.find((x) => x.version === r.runtime.version);
  return { ...st, missingParts: r && b ? partRows(b, side, r).filter((p) => p.state === "missing").map((p) => p.name) : [] };
}

const bundles: RuntimeBundle[] = [
  { version: "0.2.0-dev+20261104.0f1ffa6", kiln: "0.2.0-dev", file: "runtime/kiln-runtime-0.2.0-dev+20261104.0f1ffa6.zip", sha256: hex("rt2"),
    created: "2026-11-04T10:10:00Z", sides: ["server", "client"], components: ["core", "admin", "emf", "eml", "samples"],
    parts: [mockPart("core"), mockPart("admin", ["server"]), mockPart("emf"), mockPart("eml"), mockPart("samples", ["server"], false)] },
  { version: "0.1.9+20261101.a41c2d0", kiln: "0.1.9", file: "runtime/kiln-runtime-0.1.9+20261101.a41c2d0.zip", sha256: hex("rt1"),
    created: "2026-11-01T18:00:00Z", sides: ["server", "client"], components: ["core", "admin", "emf", "samples"],
    parts: [mockPart("core"), mockPart("admin", ["server"]), mockPart("emf"), mockPart("samples", ["server"], false)] },
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
  // other engines' mods (engine_mods::import_engine_mod): one per imported kind
  // an XHL companion: Library id from its original folder name, which stays its own id (engine_mods::import_engine_mod)
  xhl: [{ id: "xhl-vein-mining", name: "XHL-Vein-Mining", version: "0.0.0", side: "both", clients: "mod" }],
  eml: [{ id: "loot-tables-x", name: "Loot Tables X", version: "1.2.0", side: "both", clients: "mod" }],
  shroudforge: [{ id: "better-chests", name: "Better Chests", version: "0.4.1", side: "both", clients: "mod" }],
  set: [{ id: "fov-tweak", name: "FOV Tweak", version: "1.0.0", side: "both", clients: "none" }],
  greeneye: [{ id: "zoom-out", name: "Zoom Out", version: "0.2.0", side: "client", clients: "none" }],
  flame: [{ id: "creative-build", name: "Creative Build", version: "3.1.0", side: "both", clients: "mod" }],
  shroudtopia: [{ id: "fly-mode", name: "Fly Mode", version: "1.0.3", side: "both", clients: "none" }],
  enshwick: [{ id: "you.lanterns", name: "Lanterns", version: "1.0.0", side: "both", clients: "mod" }],
  shroudlink: [{ id: "chestsort", name: "chestsort.dll", version: "0.0.0", side: "server", clients: "none" }],
  ember: [{ id: "private-chests", name: "private-chests", version: "0.0.0", side: "server", clients: "none" }],
};

const library: LibraryEntry[] = FRESH ? [] : MOD_KINDS.flatMap((kind) => [...seedMods[kind]].sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  .map((m) => entryFor(kind, m, "2026-11-0" + (2 + (m.id.length % 3)) + "T09:00:00Z")));

function entryFor(kind: ModKind, m: MockManifest, imported: string, from?: string): LibraryEntry {
  const hash = kdh(kind + m.id + m.version);
  return {
    kind, id: m.id, name: m.name,
    versions: [{
      version: m.version, hash, folder: `library/${kind}/${m.id}/${m.version}+${hash.slice(5, 13)}`, imported,
      side: m.side, clients: m.clients ?? (m.side === "server" ? "none" : "mod"), size: 40_000 + m.id.length * 3_100,
      source: { type: kind === "plugin" ? "file" : "folder", from: from ?? `${m.id}-${m.version}.zip` },
      manifestId: kind === "xhl" && m.name !== m.id ? m.name : null,
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
      xhl: [], eml: [pm("eml", "loot-tables-x", false)], shroudforge: [pm("shroudforge", "better-chests")], set: [], greeneye: [pm("greeneye", "zoom-out")],
      flame: [], shroudtopia: [pm("shroudtopia", "fly-mode")], enshwick: [], shroudlink: [], ember: [],
    },
    // made by a dev.22 launcher: the list names every kind that is on (none). "Vanilla plus" is from before dev.22.
    dropins: [], dropinsEngines: true, dataDir: "shared", server: null,
  }],
  ["vanilla-plus", {
    format: 1, id: "vanilla-plus", name: "Vanilla plus", created: "2026-11-02T08:00:00Z", updated: "2026-11-03T21:15:00Z", generation: 2, notes: "",
    mods: { ...emptyMods(), kiln: [pm("kiln", "weather-plus")] }, dropins: [], dataDir: "shared", server: null,
  }],
  ["server-ember-isles", {
    format: 1, id: "server-ember-isles", name: "Server: Ember Isles", created: "2026-11-03T20:00:00Z", updated: "2026-11-03T20:00:00Z", generation: 1, notes: "",
    mods: { ...emptyMods(), kiln: [pm("kiln", "new-items")] }, dropins: [], dataDir: "profile",
    server: { id: "srv-4b8e2c", name: "Ember Isles", address: "203.0.113.7:15636" },
  }],
]);
const activations: Record<string, Activation | null> = {};
/** The profile the launcher remembers per game (`<data>/installs/<id>.active.json`): it outlives an uninstall of Kiln,
 *  a deliberate "No profile" clears it. */
const remembered: Record<string, string | null> = {};

function fingerprint(p: Profile): string {
  const parts = MOD_KINDS.flatMap((k) => (p.mods[k] ?? []).filter((m) => m.enabled).map((m) => `${k}\0${m.id}\0${m.version}\0${m.hash}`)).sort();
  return "kms1:" + hex(parts.join("\n"));
}

/** What the next add result says it did in the profile besides adding (AddReport.notices; tests: `__kilnMock.planAddNotices`). */
const addNotices: string[] = [];

/** What the next activation reports it kept in the game folder (tests: `__kilnMock.planActivation`), by whatever path
 *  it runs (profile_activate, the re-apply after profile_save, a reinstall's resume). */
let activationPlan: Pick<Activation, "retained" | "keptFiles" | "warnings"> | null = null;

function activate(pid: string, gameId: string): Activation {
  const p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
  const g = gameOf(gameId);
  if (running[gameId]) reject("game_running", "Close the game first.");
  const prev = activations[gameId];
  const a: Activation = {
    gameId, profileId: pid, generation: (prev?.generation ?? 6) + 1, pathMode: g.pathMode, profileJson: `${g.path}/kiln/profile.json`,
    copied: 0, fingerprint: fingerprint(p), activated: now(), ...activationPlan,
  };
  activationPlan = null;
  activations[gameId] = a;
  remembered[gameId] = pid;
  return a;
}
if (!FRESH) activate("ember-run", "client-steam");

function summary(p: Profile): ProfileSummary {
  return {
    id: p.id, name: p.name, updated: p.updated, server: p.server,
    // profile::counts: every entry of each kind, on or off
    counts: Object.fromEntries(MOD_KINDS.map((k) => [k, (p.mods[k] ?? []).length])) as Record<ModKind, number>,
    active: Object.entries(activations).filter(([, a]) => a?.profileId === p.id).map(([g]) => g),
  };
}

function slug(name: string): string {
  const base = name.toLowerCase().normalize("NFKD").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40) || "profile";
  let id = base, n = 2;
  while (profiles.has(id)) id = `${base}-${n++}`;
  return id;
}

/** What the launcher says about a client's side "server" Kiln mods (kiln_manage::profile WITH_WORLD, HOSTED_OFF,
 *  WORLD_FAILS): they start with the player's own world (Kiln.Host ModManager.StartHosting). */
const HOSTED = {
  with: "Starts with your own world, when you play alone or friends join you. It does not run in someone else's world.",
  off: "Off in this game: its kiln.json turns off mods that start with your world (mods.hosted is false).",
  fails: "Will not start with your world: ",
};

/** The game's kiln.json `"mods": {"hosted": false}` keeps a client's server mods off (anything else: on). */
function kilnHosted(gameId: string | null): boolean {
  if (!gameId || !games.some((g) => g.id === gameId)) return true;
  const mods = (kilnFile(gameId).values as Record<string, Json> | null)?.mods;
  return !(mods && typeof mods === "object" && !Array.isArray(mods) && (mods as Record<string, Json>).hosted === false);
}

/** One resolver pass (resolver.rs, simplified): `hosting` is a client whose own world runs, so server mods match. */
function resolveOnce(p: Profile, side: "client" | "server", hosting: boolean): { order: string[]; rejected: Rejection[] } {
  const enabled = p.mods.kiln.filter((m) => m.enabled);
  const rejected: Rejection[] = [];
  const ok = new Map<string, MockManifest>();
  for (const m of enabled) {
    const man = manifestOf("kiln", m.id, m.version);
    if (!man) { rejected.push({ id: m.id, dir: m.id, state: "failed", reason: "kiln.mod.json: not found in the library" }); continue; }
    if (side === "client" && man.side === "server" && !hosting) { rejected.push({ id: m.id, dir: m.id, state: "skipped", reason: "side 'server': not loaded on a client" }); continue; }
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
  return { order: [...ok.keys()].sort(), rejected };
}

function resolve(p: Profile, side: "client" | "server", gameId: string | null): ResolvePreview {
  const kiln = resolveOnce(p, side, false);
  const withWorld: string[] = [];
  if (side === "client") {
    // kiln_manage::profile::with_world: the menu, then what the player's own world adds
    const world = kilnHosted(gameId) ? resolveOnce(p, side, true) : null;
    const server = new Set(p.mods.kiln.filter((m) => manifestOf("kiln", m.id, m.version)?.side === "server").map((m) => m.id));
    for (const r of kiln.rejected) {
      const sideSkip = r.state === "skipped" && server.has(r.id);
      if (!world) { if (sideSkip) r.reason = HOSTED.off; continue; }
      const x = world.rejected.find((w) => w.dir === r.dir);
      if (x) { if (sideSkip || x.reason !== r.reason) Object.assign(r, { state: x.state, reason: HOSTED.fails + x.reason }); }
      else if (world.order.includes(r.id)) { Object.assign(r, { state: "skipped", reason: HOSTED.with }); withWorld.push(r.id); }
    }
    if (world) withWorld.sort((a, b) => world.order.indexOf(a) - world.order.indexOf(b));
  }
  for (const m of p.mods.kiln.filter((x) => !x.enabled)) kiln.rejected.push({ id: m.id, dir: m.id, state: "disabled", reason: "disabled in the profile" });
  const native: NativePlanEntry[] = p.mods.native.map((m) => {
    const man = manifestOf("native", m.id, m.version);
    return { id: m.id, version: m.version, phase: man?.phase ?? "early", enabled: m.enabled, reason: m.enabled ? null : "disabled in the profile" };
  });
  const warnings: string[] = [];
  if (native.some((n) => n.enabled && n.phase === "dllmain")) warnings.push("dllmain mods load inside the proxy; a bad one can hang the start.");
  return { side, kiln: { order: kiln.order, rejected: kiln.rejected.sort((a, b) => a.dir.localeCompare(b.dir)) }, native, warnings, withWorld };
}

// ---------------------------------------------------------------- other engines' mods

/** ShroudForge `extended.mod.json` settings of the placed copies (settings_schema: JSON Schema with x-kiln-* hints). */
const sfSettings: Record<string, { schema: Json; values: Record<string, Json> }> = {
  "better-chests": {
    schema: {
      $schema: "https://json-schema.org/draft/2020-12/schema", title: "Better Chests", description: "Bigger, tidier chests", type: "object", additionalProperties: false,
      properties: {
        stackMultiplier: { type: "integer", title: "Stack multiplier", description: "Items per chest slot, times the game's", default: 2, minimum: 1, maximum: 10, "x-kiln-group": "Storage" },
        autoSort: { type: "boolean", title: "Sort on open", default: false, "x-kiln-group": "Storage" },
        layout: { type: "string", title: "Layout", enum: ["compact", "wide"], "x-kiln-enum-labels": ["Compact", "Wide"], default: "compact", "x-kiln-restart": true },
      },
    },
    values: { stackMultiplier: 2, autoSort: false, layout: "compact" },
  },
};
/** ShroudForge mods the player's own folder serves (engine_mods::sf_folder): their settings say `ownFolder`. Tests:
 *  `__kilnMock.setSfOwnFolder`. */
const sfOwn = new Set<string>();
/** `<profile configDir>/shroudtopia/shroudtopia.json` `mods`, by profile. */
const shroudtopiaFiles = new Map<string, Record<string, Record<string, Json>>>();
/** EML patched enshrouded.kfc (its `.kfc.bak` is the vanilla file). */
let emlPatched = true;
/** engine_mods::xhl_host's sentence when no XHL-Core is on the game's proxy/chain paths (the fake game has none). */
const XHL_NO_HOST = "Requires XHL-Core: no recognized host was found on this game's active proxy/chain paths. Import XHL-Core separately as a Native mod and enable it, or install it using its author's instructions. Kiln never loads companion plugin DLLs itself; unknown Wine drive mappings cannot be verified here.";

function schemaFromValues(v: Json): Json {
  if (v && typeof v === "object" && !Array.isArray(v)) return { type: "object", properties: Object.fromEntries(Object.entries(v).map(([k, x]) => [k, schemaFromValues(x)])) };
  if (typeof v === "boolean") return { type: "boolean" };
  if (typeof v === "number") return { type: Number.isInteger(v) ? "integer" : "number" };
  if (typeof v === "string") return { type: "string" };
  if (Array.isArray(v)) return { type: "array" };
  return {};
}

function engineSettings(kind: ModKind, id: string, pid: string) {
  const p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
  if (!/^[a-z0-9][a-z0-9._-]*$/.test(id)) reject("invalid_argument", "invalid engine mod id");
  if (!(p.mods[kind] ?? []).some((m) => m.id === id)) reject("not_found", `${kind} mod ${id} is not in profile ${pid}`);
  const none = (note: string) => ({ kind, id, format: "none", file: null, schema: { type: "object", properties: {} }, values: {}, note });
  const g = games[0];
  if (kind === "shroudforge") {
    const sf = sfSettings[id];
    if (!sf) return { kind, id, format: "extended.mod.json", file: `${g?.path}/shroudforge/mods/${id}/extended.mod.json`, schema: { type: "object", properties: {} }, values: {}, note: "this mod declares no settings" };
    return { kind, id, format: "extended.mod.json", file: `${g?.path}/shroudforge/mods/${id}/extended.mod.json`, schema: sf.schema, values: clone(sf.values), note: null,
      ...(sfOwn.has(id) ? { ownFolder: true } : {}) };
  }
  if (kind === "shroudtopia") {
    const values = shroudtopiaFiles.get(pid)?.[id] ?? { active: true, flySpeed: 1.5, hotkey: "F6" };
    return { kind, id, format: "shroudtopia.json", file: `/home/player/.local/share/kiln/profiles/${pid}/config/shroudtopia/shroudtopia.json`, schema: schemaFromValues(values), values: clone(values), note: null };
  }
  if (kind === "emf") return none("EMF settings: use the EMF ini editor");
  if (kind === "enshwick") return none("Enshwick keeps each mod's settings per world (<save>-mod-settings/<id>/settings.toml): use the game's Mods screen or `enshwick config` on the server");
  if (kind === "ember") return none("Ember mods don't run yet: Ember has not published its mod ABI");
  return none("this engine's mods declare no settings");
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
    id: g.id, running: !!r, pid: r?.pid ?? null, started: r ? new Date(r.at - (noProxy ? 31_000 : 0)).toISOString() : null, steamUpdating: false, runtime: withMissing(runtime[gameId] ?? { installed: false, receipt: null, proxy: null, modified: [], upgradeAvailable: null }, g.kind),
    activeProfile: otherFolder[gameId] ? null : activations[gameId]?.profileId ?? null,
    ...(otherFolder[gameId] ? { otherFolderProfile: otherFolder[gameId]! } : {}), playOncePending: !!r && r.mode !== "profile" && (noProxy || Date.now() - r.at < 2500),
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
  // Play writes a changed profile again first (profile::refresh_active): a planned report stands for what that kept
  const act = req.mode === "profile" && activationPlan && activations[gameId] ? activate(activations[gameId]!.profileId, gameId) : undefined;
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
  return { gameId: g.id, mode: req.mode, nonce, backup, uri: "steam://rungameid/1203620", started: now(), ...(act ? { activation: act } : {}) };
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
const mockReportFindings: ReportFinding[] = [
  { id: "crash", level: "warning", text: "Kiln wrote a crash report on 2026-11-03T22:10:04Z: kiln/crash-reports/2026-11-03T22-10-04Z-night-sky.txt (1 in all; the newest is in this report)." },
];
const crashes: CrashReport[] = [{ name: "2026-11-03T22-10-04Z-night-sky.txt", created: "2026-11-03T22:10:04Z", size: 4_211 }];

// ---------------------------------------------------------------- settings, servers

const assets = { root: "/home/player/.local/share/kiln", editor: { present: true, bytes: 0 }, admin: { map: false, catalog: false, bytes: 0 } };
let settings: LauncherSettings = { steamRoots: [], defaultGameId: FRESH ? null : "client-steam", allowHttpModpacks: false, savesKeep: 10, updateCheck: true, animatedFire: true,
  addToActiveProfile: true, modFolderNotes: true };
// how the (Linux) launcher draws its window; `?render=software` as after the fallback
let render: RenderInfo = typeof location !== "undefined" && new URLSearchParams(location.search).get("render") === "software"
  ? { mode: "software", why: "restart", marker: true, markerWhy: "The launcher turned this on by itself: the page did not start within 15 s of the window with the GPU (2026-11-11T21:04:10Z)" }
  : { mode: "gpu", why: "default", marker: false };

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
  app_info: (): AppInfo => ({ version: "0.2.0-dev", kiln: "0.2.0-dev", platform: "linux", arch: "x86_64", dataDir: mockDataDir(), commit: "0f1ffa6", mock: true,
    noWebgl: render.mode === "software", distribution: MOCK_DISTRIBUTION, nexusEnabled: NEXUS_MOCK_ENABLED, render: { ...render } }),
  app_started: () => null,
  app_render_set: (a) => {
    render = { ...render, marker: !!a.software, markerWhy: a.software ? `Turned on in Settings (${new Date().toISOString()})` : undefined };
    return { ...render };
  },
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
    if (opts.components) {
      try { selectParts(b, g.kind, opts.components); }
      catch (e) { reject("invalid_argument", (e as Error).message); }
    }
    const fail = failNextInstall;
    failNextInstall = null;
    return startTask("runtime_install", `Install Kiln ${b.kiln}`, ["Staging", "Verifying hashes", "Classifying dinput8.dll", "Backing up", "Placing files", "Writing receipt"], () => {
      const st = runtime[gameId];
      if (fail) reject("io", fail);
      if (st?.proxy?.kind === "shroudforge" && !opts.switchFromShroudforge) reject("refused", "ShroudForge is installed. Switch from ShroudForge to replace it.");
      // like install::resolve_components: an explicit choice records the defaults it leaves out; none keeps the game's
      // parts and adds every new or newly default one not turned off
      const prev = st?.receipt ?? null;
      const components = opts.components ? selectParts(b, g.kind, opts.components) : resolveParts(b, g.kind, prev);
      const deselected = opts.components ? deselectedFor(b, g.kind, components, prev) : prev?.side === g.kind ? prev.runtime.deselected ?? [] : [];
      const receipt: InstallReceipt = { ...baseReceipt, side: g.kind, installed: st?.receipt?.installed ?? now(), updated: now(), runtime: { version: b.version, sha256: b.sha256, components, deselected } };
      // like the backend: Kiln files changed by hand are replaced, their copies kept in the install's backup
      const changed = st?.modified ?? [];
      runtime[gameId] = { installed: true, receipt, proxy: kilnProxy, modified: [], upgradeAvailable: null };
      emitGame(g.id, "status");
      const backup = changed.length ? `/home/player/.local/share/kiln/backups/install/${receipt.installId}/install-${now().replace(/[-:]/g, "").slice(0, 15)}Z` : null;
      // the active profile, written again for this runtime (quick::resume_active): a planned report stands for what it kept
      const activation = activationPlan && activations[gameId] ? activate(activations[gameId]!.profileId, gameId) : null;
      return { ...receipt, changedBackedUp: changed, backup, resumedProfile: null, activation } satisfies InstallResult;
    });
  },
  runtime_uninstall: (a) => {
    const gameId = S(a.gameId);
    if (running[gameId]) reject("game_running", "Close the game first.");
    return startTask("runtime_uninstall", "Uninstall Kiln", ["Restoring dinput8.dll", "Removing files", "Cleaning chain.ini"], () => {
      const prev = runtime[gameId];
      const emf = prev?.receipt?.replaced?.some((r) => r.class === "emf");
      activations[gameId] = null; // kiln/profile.json goes with Kiln's files; the launcher's record of the profile stays
      runtime[gameId] = { installed: false, receipt: null, proxy: emf ? { kind: "emf", version: "0.3.3", abi: null, sha256: hex("emf"), size: 70_000, detail: "export emf_loader_identity" } : null, modified: [], upgradeAvailable: null };
      emitGame(gameId, "status");
      return { removed: 1, restored: [], adopted: [], kept: [], placements: { removed: [], retained: [] }, warnings: [] };
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
      mods: from ? clone(from.mods) : emptyMods(), dropins: from ? [...from.dropins] : [], dataDir: from?.dataDir ?? "shared", server: null };
    profiles.set(p.id, p);
    return clone(p);
  },
  profile_save: (a) => {
    const p = a.profile as Profile;
    const cur = profiles.get(p.id) ?? reject("not_found", `No profile '${p.id}'.`);
    if (cur.generation !== p.generation) reject("conflict", "The profile changed elsewhere. Reload it.");
    const next = { ...clone(p), generation: p.generation + 1, updated: now() };
    profiles.set(p.id, next);
    reapplyWhereActive(p.id, rendered(next) !== rendered(cur));
    return clone(next);
  },
  profile_delete: (a) => {
    const id = S(a.profileId);
    if (!profiles.has(id)) reject("not_found", `No profile '${id}'.`);
    if (Object.values(activations).some((x) => x?.profileId === id)) reject("conflict", "Active profiles can't be deleted. Activate another first.");
    profiles.delete(id);
    return null;
  },
  profile_resolve: (a) => resolve(profiles.get(S(a.profileId)) ?? reject("not_found", "No such profile."), (a.side as "client" | "server") ?? "client", a.gameId ? S(a.gameId) : null),
  // the real activation answers after it has resolved and written the profile (seconds with big mods): the UI must
  // not wait for the answer to show the new name
  profile_activate: async (a) => {
    const r = activate(S(a.profileId), S(a.gameId));
    await new Promise((ok) => setTimeout(ok, ACTIVATE_MS));
    emitGame(S(a.gameId), "status");
    return r;
  },
  profile_deactivate: (a) => { if (running[S(a.gameId)]) reject("game_running", "Close the game first."); activations[S(a.gameId)] = null; remembered[S(a.gameId)] = null; emitGame(S(a.gameId), "status"); return { removed: [], retained: [] }; },
  profile_active: (a) => (otherFolder[S(a.gameId)] ? null : activations[S(a.gameId)] ?? null),
  activation_notices: () => notices.splice(0),
  // notices.rs unseen / mark_seen: what the window has not shown yet for the game (only the safety lines with the
  // setting off; asking remembers nothing), and what it reports on screen (the backend keeps that in the data folder;
  // the mock for the page's life)
  notices_unseen: (a) => {
    const seen = noticesSeen[S(a.gameId)] ?? new Set<string>();
    const lines = ((a.lines as { text: string; safety?: boolean }[]) ?? []).filter((l) => settings.modFolderNotes !== false || l.safety).map((l) => l.text);
    return [...new Set(lines.filter((t) => !seen.has(t)))];
  },
  notices_shown: (a) => {
    const seen = (noticesSeen[S(a.gameId)] ??= new Set());
    ((a.lines as string[]) ?? []).forEach((t) => seen.add(t));
    return null;
  },
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
    const again = remembered[gameId];
    if (again && profiles.has(again)) {
      if (running[gameId]) reject("game_running", "Close the game first: Kiln turns your profile back on in the game folder.");
      const r = activate(again, gameId);
      emitGame(gameId, "status");
      return r;
    }
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
    const again = !activations[gameId] && !makeIt && remembered[gameId] && profiles.has(remembered[gameId]!) ? remembered[gameId]! : null;
    if (again && running[gameId]) reject("game_running", `Close the game first: Kiln turns ${profiles.get(again)!.name} back on in the game folder.`);
    return startTask("library_add", paths.length === 1 ? `Add ${paths[0].split(/[\\/]/).pop()}` : `Add ${paths.length} items`,
      ["Reading", "Hashing", "Adding to the profile", "Writing profile.json"], () => quickAdd(gameId, paths, makeIt), 300);
  },
  library_add_to_profile: (a) => {
    const gameId = S(a.gameId);
    const refs = (a.entries as LibraryRef[]) ?? [];
    if (!refs.length) reject("invalid_argument", "Nothing to add.");
    gameOf(gameId);
    const p = clone(profiles.get(S(a.profileId)) ?? reject("not_found", `No profile '${S(a.profileId)}'.`));
    const r: AddReport = { import: { imported: [], existing: [], skipped: [], notes: [], names: {} }, profileId: p.id, profileName: p.name, created: false, added: [], already: [],
      changes: [], applied: false, running: !!running[gameId], activation: null, missing: [], modpacks: [], resumed: false, libraryOnly: null, notices: addNotices.splice(0) };
    const named = (x: LibraryRef): AddedMod => ({ ...x, name: library.find((l) => l.kind === x.kind && l.id === x.id)?.name ?? x.id });
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
    if (activations[gameId]?.profileId === p.id && !running[gameId]) { r.activation = activate(p.id, gameId); r.applied = true; emitGame(gameId, "status"); }
    r.added = r.changes.map((c) => named({ kind: c.kind, ...c.after }));
    return r;
  },
  library_lint: () => ({ format: 1, tool: "kiln-lint", version: "mock", ok: true, checked: [], summary: { errors: 0, warnings: 0, infos: 0, hints: 0 }, catalog: null, diagnostics: [] }),
  library_unused: () => library.flatMap((e) => e.versions
    .map((v) => ({ kind: e.kind, id: e.id, version: v.version, hash: v.hash }))
    .filter((r) => !mockLibraryUsers(r).length)),
  library_users: (a) => mockLibraryUsers(a.entry as LibraryRef),
  library_remove: (a) => {
    if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading this mod.");
    mockLibraryRemove(a.entry as LibraryRef, Array.isArray(a.shown) ? (a.shown as string[]) : !!a.force);
    return null;
  },
  library_remove_many: (a) => {
    if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading this mod.");
    const report: RemoveReport = { removed: [], failed: [] };
    for (const r of a.entries as LibraryRef[]) {
      try { mockLibraryRemove(r, !!a.force); report.removed.push(r); }
      catch (e) { report.failed.push({ entry: r, error: e as KilnError }); }
    }
    return report;
  },

  // other engines' mods (kiln_manage::engine_mods)
  engine_settings_get: (a) => engineSettings(a.kind as ModKind, S(a.id), S(a.profileId)),
  engine_settings_set: (a) => {
    const kind = a.kind as ModKind, id = S(a.id), pid = S(a.profileId), key = S(a.key), value = a.value as Json;
    const cur = engineSettings(kind, id, pid);
    if ((a.gameId && running[S(a.gameId)]) || Object.entries(activations).some(([g, p]) => p?.profileId === pid && running[g])) reject("game_running", "Close the game first.");
    if (kind === "shroudforge" && (!a.gameId || activations[S(a.gameId)]?.profileId !== pid)) reject("conflict", "Activate this profile in the selected game before editing ShroudForge settings.");
    if (cur.format === "none") reject("unsupported", `${kind} mods have no settings Kiln can edit`);
    const old = (cur.values as Record<string, Json>)[key];
    if (old !== undefined && typeof old !== typeof value) reject("invalid_argument", `${key}: expected the same type as ${JSON.stringify(old)}`);
    if (kind === "shroudforge") {
      const sf = sfSettings[id] ?? reject("not_found", `ShroudForge mod ${id} is not in ShroudForge's mods folder: activate the profile first`);
      sf.values[key] = value;
    } else {
      const file = shroudtopiaFiles.get(pid) ?? {};
      file[cur.id] = { ...(cur.values as Record<string, Json>), [key]: value };
      shroudtopiaFiles.set(pid, file);
    }
    return engineSettings(kind, id, pid);
  },
  engine_status: (a) => {
    const g = gameOf(S(a.gameId));
    const st = (k: ModKind, file: string, state: Json | null) => ({ kind: k, plugin: `kiln-${k}`, state, file: `${g.path}/kiln/state/${file}` });
    return [
      st("emf", "emf.json", { mods: [{ id: "beastmaster", state: "loaded" }] }),
      st("eml", "eml.json", emlPatched
        ? { phase: "done", patched: 3, ms: 1263, mods: [{ id: "loot-tables-x", name: "Loot Tables X", state: "disabled", reason: "disabled in the profile" }] }
        : { phase: "idle", mods: [] }),
      // XHL-Core is external: only whether it is on the game's proxy/chain paths (engine_mods::xhl_host)
      { kind: "xhl", plugin: "", state: null, file: "", prerequisite: { available: false, text: XHL_NO_HOST } },
      // ShroudForge's own runtime report (shroudforge::effects): current while the fake game runs, stopped after
      { ...st("shroudforge", "shroudforge.json", { mode: "hosted", reason: null, version: "0.9.2", side: "client" }), effects: running[g.id]
        ? { file: `${g.path}/shroudforge/state.json`, state: "current", updatedAt: Math.floor(Date.now() / 1000), reason: null,
          mods: [{ id: "better-chests", state: "no-change", detail: "autoSort is off" }] }
        : { file: `${g.path}/shroudforge/state.json`, state: "stopped", updatedAt: 1793793660, reason: "ShroudForge runtime is stopped; previous mod effects are not current", mods: [] } },
      st("set", "set.json", null),
      st("greeneye", "greeneye.json", { mods: [{ id: "zoom-out", status: "started", generation: 1, hooks: 2 }] }),
      { ...st("flame", "flame.json", null), ownLoader: { kind: "flame", engine: "flame", file: "kiln/chain/flame-dinput8.dll", via: "chain", folder: "mods",
        delivery: "placed", enabled: 1, copied: ["torch-range"], notCopied: [],
        text: "Flame's own loader (kiln/chain/flame-dinput8.dll) is installed: it runs the Flame mods in mods/; Kiln copied this profile's 1 Flame mod(s) into mods/ for it" } },
      st("shroudtopia", "shroudtopia.json", { mods: [{ name: "Fly Mode", status: "active", updates: 120 }], refused: [] }),
      st("enshwick", "enshwick.json", { status: "started", phase: "early", mods: [{ folder: "you.lanterns", form: "pack", origin: "profile", action: "copy", note: "" }] }),
      st("shroudlink", "shroudlink.json", null),
      st("ember", "ember.json", { abi: null, steppedAside: false, mods: [{ id: "private-chests", status: "waiting-for-abi",
        reason: "not loaded: Ember has not published its mod ABI yet" }] }),
    ];
  },
  eml_backup: (a) => {
    const g = gameOf(S(a.gameId));
    return FRESH ? [] : [{ file: `${g.path}/enshrouded.kfc`, backup: `${g.path}/enshrouded.kfc.bak`, patched: emlPatched }];
  },
  eml_restore: (a) => {
    gameOf(S(a.gameId));
    if (running[S(a.gameId)]) reject("game_running", "Close the game first.");
    return reject("refused", "Direct restoration of shared archive backups cannot verify ownership. Disable EML mods and start the game through Kiln: its archive writer restores only output it can prove it wrote (earlier Kiln versions' too); other loaders' data is kept. When it cannot prove that, it leaves the data alone and says why (Logs > Engines); then enable any EML mod for one start and disable it again, or use Steam's \"Verify integrity of game files\".");
  },

  // mods in the game folder Kiln did not put there (kiln_manage::found)
  found_scan: (a) => foundScan(S(a.gameId), profiles.get(S(a.profileId)) ?? reject("not_found", `No profile '${S(a.profileId)}'.`)),
  found_add: (a) => {
    const gameId = S(a.gameId), pid = S(a.profileId), path = S(a.path);
    const p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
    const m = foundScan(gameId, p).mods.find((x) => x.path === path) ?? reject("not_found", `${path} is not a mod in the game folder (any more)`);
    return startTask("found_add", `Add ${path.split("/").pop()}`, ["Reading", "Copying", "Adding to the profile"], () => foundAdd(gameId, pid, m), 300);
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

  // what kiln-eml does before the game's window exists: only on request (tests: `__kilnMock.setPreStart`), so that a plain
  // start still says "Waiting for Kiln to boot."
  launch_prestart: (a): PreStart | null => (running[S(a.gameId)] ? preStart : null),

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
  report_save: (a) => {
    if (!S(a.path).trim()) reject("invalid_argument", "Choose where to save the report.");
    const path = /\.zip$/i.test(S(a.path)) ? S(a.path) : `${S(a.path)}.zip`;
    return { path, size: 48_213, findings: mockReportFindings, errors: [] } satisfies ReportSaved;
  },
  report_summary: () => [
    "Kiln support report, 2026-11-10T12:00:00Z",
    "kiln-launcher 0.2.0-dev (mock); Kiln in the game: 0.2.0-dev",
    "OS: Arch Linux; Proton: GE-Proton10-3; Steam Deck: no",
    "Game: client, Steam build 26430253, known to Kiln: yes",
    "Profile: Ember run (4 mods)",
    "Last start: 2026-11-04T12:01:02Z, hooks on",
    "Findings:",
    ...mockReportFindings.filter((f) => f.level !== "info").map((f) => `- ${f.text}`),
    "",
  ].join("\n"),

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
      const receipt: InstallReceipt = { ...baseReceipt, side: "server", installed: now(), runtime: { version: b.version, sha256: b.sha256, components: resolveParts(b, "server", null), deselected: [] } };
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
          mods: emptyMods(), dropins: [], dataDir: "profile", server: { id: "srv-4b8e2c", name: "Ember Isles" } };
        p.mods = emptyMods();
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
Object.assign(handlers, chainMockHandlers({ profiles, library, reject }));
Object.assign(handlers, dataFolderMockHandlers(startTask, (id) => tasks.get(id)));

/** The folder dialog's stand-in in a plain browser (shell/dataFolder.ts pickFolder; tests queue answers). */
export { mockPickFolder };

const EDITOR_FIXTURE = "/assets-local/fixtures/editor/";
let editorFix: Promise<{ saves: { path: string; file: string; modified: number }[]; blocks: Record<string, string> } | null> | null = null;
function editorFixture() {
  editorFix ??= fetch(EDITOR_FIXTURE + "index.json").then((r) => (r.ok ? r.json() : null)).catch(() => null);
  return editorFix;
}

/** "My mods": the profile a first-time player gets (every drop-in folder on, like kiln_manage::quick). */
function makeDefault(): Profile {
  const p: Profile = { format: 1, id: slug("My mods"), name: "My mods", created: now(), updated: now(), generation: 2, notes: "",
    mods: emptyMods(), dropins: [...KINDS], dataDir: "shared", server: null };
  profiles.set(p.id, p);
  return p;
}

/** notices_unseen's memory, by game. */
const noticesSeen: Record<string, Set<string>> = {};

/** Re-applies' activations the window has not taken (activation_notices), as the backend keeps them; tests put some
 *  there before the page loads with `window.__kilnMockNotices` (a re-apply before the window listened). */
const notices: Activation[] = typeof window === "undefined" ? [] :
  [...((window as unknown as { __kilnMockNotices?: Activation[] }).__kilnMockNotices ?? [])];

/** profile_save's follow-up: reconcile every active, stopped game, even if profile.json is current. Only a rendered
 *  change advances the game's generation; a report without a write still waits for the window. */
function reapplyWhereActive(pid: string, written = true) {
  for (const [g, act] of Object.entries(activations)) {
    if (act?.profileId !== pid || running[g]) continue;
    const a = written ? activate(pid, g) : { ...act, retained: [], keptFiles: [], warnings: [], ...activationPlan };
    activationPlan = null;
    if (a.retained?.length || a.keptFiles?.length || a.warnings?.length) {
      notices.push(a);
      mockEmit("kiln://activation", { gameId: g });
    }
    emitGame(g, "status");
  }
}

/** What profile.json holds of a profile (profile::render): name/notes edits need no write, but refresh_active still
 *  reconciles the mod folders and can report retained copies, kept settings or warnings. */
const rendered = (p: Profile) => JSON.stringify([p.mods, p.dropins, p.dataDir, p.late ?? []]);

/** Games' profile files listing a version without a profile listing it (tests: `__kilnMock.setGameFileUsers`):
 *  `<profile> (active in <game>)` per `<kind>/<id>/<version>`. */
let gameFileUsers: Record<string, string[]> = {};

/** library_users (kiln_manage::library::users): the profiles listing the version, then the games' profile files. */
function mockLibraryUsers(r: LibraryRef): string[] {
  const inProfiles = [...profiles.values()].filter((p) => p.mods[r.kind].some((m) => m.id === r.id && m.version === r.version)).map((p) => p.id);
  return [...new Set([...inProfiles, ...(gameFileUsers[`${r.kind}/${r.id}/${r.version}`] ?? [])])].sort();
}

/** One version out of the library, as library_remove after its running check (conflict while a profile lists it).
 *  `force`: true past every user, a list past exactly those (library::Force::Past). */
function mockLibraryRemove(r: LibraryRef, force: boolean | string[]) {
  const used = mockLibraryUsers(r);
  const past = Array.isArray(force) ? used.every((u) => force.includes(u)) : force || !used.length;
  if (!past) reject("conflict", `${r.id} ${r.version} is used by profile(s) ${used.join(", ")}`, used);
  const e = library.find((l) => l.kind === r.kind && l.id === r.id && l.versions.some((v) => v.version === r.version));
  if (!e) reject("not_found", `${r.kind} ${r.id} ${r.version} is not in the library`);
  e!.versions = e!.versions.filter((v) => v.version !== r.version);
  if (!e!.versions.length) library.splice(library.indexOf(e!), 1);
}

// ---------------------------------------------------------------- the game folder (kiln_manage::found)

/** What a player extracted into the mock game folder. Kiln drops none of these there. */
const gameFolder: { kind: ModKind | null; name: string; id: string | null; version: string | null; path: string; why?: string; wrong?: string }[] = FRESH ? [] : [
  { kind: "eml", name: "Hoarder's Helper", id: "HoardersHelper", version: "1.0.1", path: "mods/HoardersHelper" },
  { kind: "eml", name: "Less Dense Shroud Fog", id: "Less Dense Shroud Fog", version: "1.2.0", path: "mods/Less Dense Shroud Fog" },
  { kind: "greeneye", name: "Better Map", id: "better-map", version: "2.0.0", path: "greeneye/better-map" },
  { kind: "greeneye", name: "Zoom Out", id: "zoom-out", version: "1.1.0", path: "greeneye/zoom-out" },
  { kind: "set", name: "Fly Mode", id: "FlyMode", version: null, path: "shrouded_engine_tweaks/mods/FlyMode" },
  // an EMF mod extracted into mods/, where nothing loads EMF mods from
  { kind: "emf", name: "Pack Mule", id: "pack-mule", version: "0.3.0", path: "mods/pack-mule", wrong: "Nothing loads EMF mods from mods: Kiln looks for them in emf/mods." },
  { kind: null, name: "screenshots", id: null, version: null, path: "mods/screenshots", why: "No mod manifest, Lua code or mod DLL in this folder." },
];
const SHORT: Partial<Record<ModKind, string>> = { eml: "EML", greeneye: "Greeneye", set: "SET", emf: "EMF" };

/** profile.json `dropins` as kiln_manage::profile::Profile::dropins_file says: true, or the kinds named. */
function dropinsFile(p: Profile): true | ModKind[] {
  const core = ["kiln", "native", "plugin", "emf"] as ModKind[];
  if (!p.dropinsEngines) return core.every((k) => p.dropins.includes(k)) ? true : dropinKinds(p).filter((k) => k !== "set");
  const on = DROPIN_KINDS.filter((k) => p.dropins.includes(k));
  return on.length === DROPIN_KINDS.length ? true : on;
}

/** kiln_manage::found's rule for the kinds the mock folder holds: every plugin follows `dropins`; kiln-set always reads. */
function foundState(p: Profile, kind: ModKind): { loads: boolean; reason: string } {
  const f = dropinsFile(p);
  const label = SHORT[kind] ?? kind;
  if (kind === "set") return { loads: true, reason: "kiln-set always reads shrouded_engine_tweaks/mods." };
  const on = f === true || f.includes(kind);
  return { loads: on, reason: `${label} drop-ins are ${on ? "on" : "off"} in this profile.` };
}

function foundScan(gameId: string, p: Profile): FoundReport {
  gameOf(gameId);
  const kiln = !!runtime[gameId]?.installed;
  const mods: FoundMod[] = gameFolder.map((g) => {
    const base = { kind: g.kind, name: g.name, id: g.id, version: g.version, path: g.path };
    if (!g.kind) return { ...base, state: "cannotRun", reason: g.why ?? "Not a mod Kiln knows." };
    const mine = (p.mods[g.kind] ?? []).find((m) => g.id && (m.id.toLowerCase() === g.id.toLowerCase() || m.id === g.id.toLowerCase().replace(/[^a-z0-9._-]/g, "-")));
    if (mine) return { ...base, state: "inProfile", reason: mine.enabled ? "This profile has this mod too. The profile's copy loads." : "This profile has this mod too, switched off. The profile's copy wins.", profileMod: mine.id };
    if (!kiln) return { ...base, state: "off", reason: "Kiln is not installed in this game." };
    if (g.wrong) return { ...base, state: "off", reason: g.wrong, fix: "add" };
    const st = foundState(p, g.kind);
    if (st.loads) return { ...base, state: "loads", via: "kiln", reason: st.reason };
    const withIt: Profile = { ...p, dropins: [...new Set([...dropinKinds(p), g.kind])], dropinsEngines: true };
    return { ...base, state: "off", reason: st.reason, fix: foundState(withIt, g.kind).loads ? "dropins" : "add" };
  });
  return { gameId, profileId: p.id, kilnInstalled: kiln, mods, loaders: [], unreadable: [], truncated: false };
}

/** found_add: the folder into the Library (an id as the Library makes it) and into the profile, enabled. */
function foundAdd(gameId: string, pid: string, m: FoundMod): AddReport {
  const kind = m.kind ?? reject("refused", `${m.path}: ${m.reason}`);
  const id = (m.id ?? m.name).toLowerCase().replace(/[^a-z0-9._-]+/g, "-");
  const man: MockManifest = { id, name: m.name, version: m.version ?? "1.0.0", side: "both" };
  let e = library.find((l) => l.kind === kind && l.id === id);
  if (!e) { e = entryFor(kind, man, now(), `${gameOf(gameId).path}/${m.path}`); library.push(e); }
  const v = e.versions[0];
  const ref: LibraryRef = { kind, id, version: v.version, hash: v.hash };
  const p = clone(profiles.get(pid)!);
  const after: ProfileMod = { id, version: v.version, hash: v.hash, enabled: true };
  const before = p.mods[kind].find((x) => x.id === id) ?? null;
  p.mods[kind] = [...p.mods[kind].filter((x) => x.id !== id), after];
  profiles.set(pid, { ...p, generation: p.generation + 1, updated: now() });
  const active = activations[gameId]?.profileId === pid && !running[gameId];
  if (active) { activate(pid, gameId); emitGame(gameId, "status"); }
  const added: AddedMod = { kind, id, version: v.version, hash: v.hash, name: m.name };
  return { import: { imported: [ref], existing: [], skipped: [], notes: [], names: { [`${kind}/${id}`]: m.name } }, profileId: pid, profileName: p.name, created: false, added: [added], already: [],
    changes: [{ kind, before, after }], applied: active, running: !!running[gameId], activation: active ? activations[gameId] : null, missing: [], modpacks: [],
    resumed: false, libraryOnly: null, notices: addNotices.splice(0) };
}

/** library_add (kiln_manage::quick::add): import, add to the active profile ("My mods" when there is none at all), apply. */
function quickAdd(gameId: string, paths: string[], makeIt: boolean): AddReport {
  const r: AddReport = { import: { imported: [], existing: [], skipped: [], notes: [], names: {} }, profileId: null, profileName: null, created: false, added: [], already: [],
    changes: [], applied: false, running: !!running[gameId], activation: null, missing: [], modpacks: [], resumed: false, libraryOnly: null, notices: addNotices.splice(0) };
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
  const again = remembered[gameId];
  if (active) p = clone(profiles.get(active)!);
  else if (makeIt) {
    p = makeDefault();
    r.activation = activate(p.id, gameId);
    r.created = r.applied = true;
  } else if (again && profiles.has(again)) {
    p = clone(profiles.get(again)!);
    r.activation = activate(p.id, gameId);
    r.resumed = r.applied = true;
  } else {
    r.libraryOnly = "No profile is active in this game, so the mod is only in your Library. Add it to a profile to play with it.";
    return r;
  }
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
  const report: ImportReport = { imported: [], existing: [], skipped: [], notes: [], names: {} };
  for (const p of paths.flatMap((x) => { const d = detect(x); return d.kind === "unknown" && d.items.length ? d.items.map((i) => i.path) : [x]; })) {
    const d = p.includes(".zip/") ? detect(p.split(".zip/")[0].replace(/-pack$/i, "") + ".zip") : detect(p);
    const kind: ModKind | null = d.kind === "bare-dll" ? "native" : d.kind === "kiln" || d.kind === "native" || d.kind === "emf" || d.kind === "plugin" ? d.kind : null;
    if (!kind || !d.id) { report.skipped.push({ path: p, reason: d.reason }); continue; }
    const version = d.version ?? "1.0.0";
    const ex = library.find((l) => l.kind === kind && l.id === d.id);
    const hash = kdh(kind + d.id + version);
    const ref: LibraryRef = { kind, id: d.id, version, hash };
    if (ex?.versions.some((v) => v.version === version)) { report.names[`${kind}/${d.id}`] = ex.name; report.existing.push(ref); continue; }
    // a seed mod's file ("more-hotbars-2.1.0.zip") keeps the seed's manifest (its name, requires, side)
    const seed = seedMods[kind].find((x) => x.id === d.id);
    const m: MockManifest = seed ? { ...seed, version } : { id: d.id, name: d.id.replace(/-/g, " ").replace(/\b\w/g, (c) => c.toUpperCase()), version, side: "both", phase: kind === "native" ? "early" : undefined };
    const fresh = entryFor(kind, m, now(), p);
    if (ex) ex.versions.unshift(fresh.versions[0]);
    else {
      // library::list's order: kind (ModKind::ALL), then id; a new Kiln mod lands among the Kiln mods, not at the end
      const at = library.findIndex((l) => MOD_KINDS.indexOf(l.kind) > MOD_KINDS.indexOf(kind) || (l.kind === kind && l.id > fresh.id));
      library.splice(at < 0 ? library.length : at, 0, fresh);
    }
    report.names[`${kind}/${d.id}`] = ex?.name ?? fresh.name;
    report.imported.push(ref);
  }
  return report;
}

// ---------------------------------------------------------------- state across reloads (tests)

/** `sessionStorage["kiln.mock.persist"] = "1"` (a test, before a reload): profiles, activations and the library survive
 *  reloads of the page, as the backend's files do. Off by default: every page load starts from the seed. */
const PERSIST = typeof sessionStorage !== "undefined" && sessionStorage.getItem("kiln.mock.persist") === "1";
const STATE_KEY = `kiln.mock.state:${MODE ?? "seed"}`;
function saveState() {
  sessionStorage.setItem(STATE_KEY, JSON.stringify({ profiles: [...profiles.entries()], activations, remembered, library }));
}
if (PERSIST) {
  const raw = sessionStorage.getItem(STATE_KEY);
  if (raw) {
    const st = JSON.parse(raw) as { profiles: [string, Profile][]; activations: typeof activations; remembered: typeof remembered; library: LibraryEntry[] };
    profiles.clear();
    for (const [id, p] of st.profiles) profiles.set(id, p);
    for (const k of Object.keys(activations)) delete activations[k];
    Object.assign(activations, st.activations);
    for (const k of Object.keys(remembered)) delete remembered[k];
    Object.assign(remembered, st.remembered);
    library.splice(0, library.length, ...st.library);
  } else saveState();
}

export async function mockInvoke<T>(cmd: string, args: Record<string, unknown>): Promise<T> {
  commandCalls.push(cmd);
  const plan = commandPlans[cmd]?.shift();
  if (plan?.delay) await new Promise((r) => setTimeout(r, plan.delay));
  if (plan?.error) throw plan.error;
  if (plan && Object.hasOwn(plan, "result")) return clone(plan.result) as T;
  await new Promise((r) => setTimeout(r, 30)); // async like IPC
  // a pointer to a data folder that cannot be used: only the data folder's own commands answer (mockDataFolder.ts)
  const blocked = dataFolderBlocks(cmd);
  if (blocked) throw blocked;
  const h = handlers[cmd];
  if (!h) reject("not_implemented", `${cmd} has no mock (and no backend in a plain browser)`);
  const r = (await h(args)) ?? null;
  if (PERSIST) saveState();
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
  planActivation: (report: Pick<Activation, "retained" | "keptFiles" | "warnings">) => { activationPlan = report; },
  planAddNotices: (lines: string[]) => { addNotices.push(...lines); },
  setSfOwnFolder: (id: string, own = true) => { if (own) sfOwn.add(id); else sfOwn.delete(id); },
  setGameFileUsers: (users: Record<string, string[]>) => { gameFileUsers = users; },
  failNextInstall: (message: string) => { failNextInstall = message; },
  setModified: (files: string[], gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], modified: files }; emitGame(gameId, "status"); },
  startOutside: mockStartOutside, setServer: mockSetServer, opened: () => [...openedUrls],
  setPreStart: (p: PreStart | null) => { preStart = p; },
  setNoProxy: (on: boolean, gameId = "client-steam") => { noProxy = on; emitGame(gameId, "status"); },
  setOtherFolderProfile: (o: { id: string; root?: string | null } | null, gameId = "client-steam") => { otherFolder[gameId] = o; emitGame(gameId, "status"); },
  setNewerMods: (mods: NewerKilnMod[], gameId = "client-steam") => { newerMods[gameId] = mods; emitGame(gameId, "status"); },
  /** A hand edit of the game's kiln.json (a key the launcher's Settings do not list, such as `mods.hosted`). */
  setKilnJson: (path: string, value: Json, gameId = "client-steam") => {
    const f = kilnFile(gameId);
    const values = setPath(f.values, path, value);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2) + "\n", modified: now() });
  },
  setUpgradeAvailable: (version: string | null, gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], upgradeAvailable: version }; emitGame(gameId, "status"); } };
