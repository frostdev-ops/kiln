import { mockStoppedFile, readMockStop, type StopScript } from "./mockStoppedStart";
// Browser-only mock of the Tauri backend. commands.ts uses it automatically when
// window.__TAURI_INTERNALS__ is absent (vite dev in a plain browser, Playwright smoke tests in mock mode), and for
// read-only not_implemented commands in dev builds (./fallback.ts). State lives in memory: profiles, library, tasks,
// a fake game that "runs" after Play. Fixtures are built on schema/examples (the contract examples).
// Commands without a mock reject with not_implemented, like the wave-0 Rust stubs.
import type {
  Activation, AddReport, BootSnapshot, EmfDepProblem, BundlePart, RunSide, AddedMod, AdminEndpoint, AppInfo, BootJson, BootState, ConfigDocument, CookJob, CookReason, CookedMod, ConfigFileInfo, ConfigTarget, CrashReport, ReportFinding, ReportSaved, Detected, Distribution,
  FoundMod, FoundReport, GameInstall, GameStatus, ImportReport, InstallReceipt, InstallResult, InstallState, Json, KilnError, LastServer, LaunchRequest, NewerKilnMod, GraphicsToolNote, GraphicsStepAside, GraphicsStateFile,
  LaunchResult, LauncherSettings, RenderInfo, SandboxInfo, DesktopInfo, LibraryEntry, LibraryRef, LibraryVersion, RollbackReport, VersionRef, TakenOver, LogFile, LogTarget, ModKind, ModpackInstallRequest, ModpackInstallResult, ModsJson,
  NativeJson, NativePlanEntry, NativeUserState, PreStart, Profile, ProfileChange, ProfileMod, ProfileSummary, ProxyClass, Rejection, RemoveReport, ResolvePreview, SettingsRemoval, RuntimeBundle, SaveBackup, SaveSlot,
  SchemaSnapshot, ServerInstance, ServerSpec, ServerStatus, Task, TaskState, StartMode, StartSettings, StartSetup, RunnerInfo, BuildCheck,
} from "./commands";
import type { SfBackend, SfBackendCard, SfBackendPreview, SfResolution, SfSetting } from "./commands";
import type { Preflight, StartWarning } from "./commands";
import type { DataRestoreBase, EngineStatus } from "./commands";
import { adminMockHandlers } from "../admin/mock";
import { serverMockHandlers, webPanelMockHooks } from "../pages/servers/mockServers";
import { updateMockHandlers } from "./mockUpdate";
import { deckMockHandlers } from "../deck/mockDeck";
import { nexusMockHandlers, NEXUS_MOCK_ENABLED } from "./mockNexus";
import { webPanelModMockHooks } from "../pages/servers/mockServers";
import { communityMockHandlers, communityMockHooks } from "./mockCommunity";
import { mockReportContents } from "./mockReport";
import { updatesMockHandlers } from "./mockUpdates";
import { MOCK_PREVIEW, vendorMockHandlers } from "./mockVendor";
import { chainMockHandlers } from "./mockChain";
import { crucibleMockHandlers } from "./mockCrucible";
import { studioMockHandlers } from "./mockStudio";
import { dataFolderBlocks, dataFolderMockHandlers, mockDataDir, mockPickFolder } from "./mockDataFolder";
import { mockSpeak, bootJsonAt, clearStartHistory, mockAsked, mockBegin, mockHoldsNextStart, mockProgress, mockStartEnded, ownerScript, persistStarts, processAt, scriptedStart, setStartScript, type StartScript } from "./mockProgress";
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
/** `?mock=no-steam` (a PC without Steam) starts as empty does: nothing found. */
const FRESH = MODE === "fresh" || MODE === "empty" || MODE === "no-steam";
/** `?dist=nexus`: the Nexus Mods build (app_info.distribution "nexus": no self-update; mockUpdate.ts answers
 *  "disabled"). Anything else: a direct build. */
export const MOCK_DISTRIBUTION: Distribution = typeof location !== "undefined" && new URLSearchParams(location.search).get("dist") === "nexus" ? "nexus" : "direct";
/** Every URL app_open_url was asked to open (tests: `__kilnMock.opened()`). */
const openedUrls: string[] = [];
/** Deterministic IPC delays/failures for browser regressions; each plan is consumed by one call. */
/** `gate`: the call waits until a test opens that gate (`__kilnMock.openGate(name)`), for an order that must not
 *  depend on timing. */
interface CommandPlan { delay?: number; gate?: string; error?: KilnError; result?: unknown }
const gates = new Map<string, { open: boolean; waiters: (() => void)[] }>();
const gateOf = (name: string) => { let g = gates.get(name); if (!g) { g = { open: false, waiters: [] }; gates.set(name, g); } return g; };
const commandPlans: Record<string, CommandPlan[]> = typeof window === "undefined" ? {} :
  (window as unknown as { __kilnMockPlans?: Record<string, CommandPlan[]> }).__kilnMockPlans ?? {};
const commandCalls: string[] = [];
/** report_save's `exclude` each time ("What's in this report"; tests: `__kilnMock.reportSaves()`). */
const reportSaves: string[][] = [];
const openedPaths: string[] = [];
/** "Remove its settings too": each mod whose settings went ("<kind>/<id>"; tests: `__kilnMock.settingsRemoved()`). */
const settingsRemoved: string[] = [];
/** library_remove's `settings` / library_remove_settings (kiln_manage::mod_settings::remove): refused while the Library
 *  holds a version, else the mod's settings files go, after a copy into the backups folder. */
function mockRemoveSettings(kind: ModKind, id: string): SettingsRemoval {
  if (library.some((l) => l.kind === kind && l.id === id && l.versions.length)) reject("conflict", `${kind} ${id} is still in the Library: its settings stay`);
  settingsRemoved.push(`${kind}/${id}`);
  return {
    removed: [`/home/player/.local/share/kiln/profiles/default/config/${id}.json`, `/home/player/.local/share/Steam/steamapps/common/Enshrouded/kiln/state/placed-settings/${kind}/${id}`],
    backup: `/home/player/.local/share/kiln/backups/mod-settings/${kind}-${id}-20261120T120000Z`, notes: [],
  };
}

// ---------------------------------------------------------------- games

const STEAM = "/home/player/.local/share/Steam";
const steamGame: GameInstall = {
  id: "client-steam", kind: "client", path: `${STEAM}/steamapps/common/Enshrouded`, exe: "enshrouded.exe", appId: 1203620,
  library: STEAM, platform: "proton", prefix: `${STEAM}/steamapps/compatdata/1203620/pfx`, buildId: "26404966",
  exeSha256: "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618", steamFlavor: "native", pathMode: "z", source: "steam",
};
/** `?mock=no-steam`: a PC without Steam: no game found, no Steam folder, umu-run and GE-Proton outside Steam. */
const NO_STEAM = MODE === "no-steam";
const games: GameInstall[] = FRESH ? [] : [steamGame];
const baseReceipt = receiptExample as unknown as InstallReceipt;
const kilnProxy: ProxyClass = { kind: "kiln", version: "0.2.0-dev", abi: 2, sha256: hex("proxy"), size: 98304, detail: "export KilnProxyInfo" };
/** `?upgrade=<bundle version>`: the game's Kiln is older than that bundle from the start (InstallState.upgradeAvailable), as
 *  on the first start after a launcher update that brought a newer runtime. */
const UPGRADE_SEED = typeof location !== "undefined" ? new URLSearchParams(location.search).get("upgrade") : null;
const runtime: Record<string, InstallState> = FRESH ? {} : {
  "client-steam": { installed: true, receipt: baseReceipt, proxy: kilnProxy, modified: [], upgradeAvailable: UPGRADE_SEED },
};
const running: Record<string, { pid: number; mode: LaunchRequest["mode"]; nonce: string | null; at: number; profile: string | null;
  /** A scripted start (mockProgress.ts): the process exists from then, boot.json is written at `bootAt`. */
  from?: number; bootAt?: number; safeIdle?: string | null } | null> = {};
/** The fake game's process as the status sees it: a scripted start's exists only once Steam has started it. */
const live = (gameId: string) => { const r = running[gameId]; return r && (!r.from || Date.now() >= r.from) ? r : null; };
let pidSeq = 4100;
/** Starts launch_begin began whose game process has not come yet (the update's restart waits for them). */
const startsBegun = new Set<string>();
/** How long profile_activate takes to answer. */
const ACTIVATE_MS = 1500;
/** The next runtime install fails with this message as an `io` error, or with this whole error (a `permission` error with
 *  `detail.blocked`: a DLL Windows refused, inbox 92) (tests: `__kilnMock.failNextInstall`). */
let failNextInstall: string | KilnError | null = null;
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
/** Steam is updating the game (GameStatus.steamUpdating; tests: `__kilnMock.setSteamUpdating(true)`). */
const steamUpdating: Record<string, boolean> = {};
/** GameStatus.graphicsTools per game (tests: `__kilnMock.setGraphicsTools`). */
const graphicsTools: Record<string, GraphicsToolNote[]> = {};
/** GameStatus.graphicsStepAside per game (tests: `__kilnMock.setGraphicsStepAside`). The preview's Steam client has
 *  DLSSTweaks next to it, so the Graphics card shows a line; tests start from it or set their own. */
const graphicsStepAside: Record<string, GraphicsStepAside[]> = {
  "client-steam": [{ kind: "dlsstweaks", file: "dlsstweaks.ini", frameCap: false, upscaler: false }],
};
/** (dev.29.18) GameStatus.graphicsState per game: the game's kiln/state/graphics.json (tests: `__kilnMock.setGraphicsState`;
 *  `current` is decided as the backend does: the file's game runs). */
const graphicsState: Record<string, GraphicsStateFile> = {};

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
  incompatible?: string[]; phase?: NativePlanEntry["phase"]; description?: string; clients?: "none" | "kiln" | "mod";
  /** EMF: mod.json `depends` (id -> range, or {version, optional, url}). */
  depends?: Record<string, string | { version?: string; optional?: boolean; url?: string }> }

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
  // packages for Enshrouded ModLoader (a DLL with its .dll.mod, run by kiln-dllmod from Mods/EnshroudedModLoader/<id>/):
  // none seeded, so the other pages' mod counts stay as they are (engine_status has kiln-dllmod's state file)
  dllmod: [],
};

/** `?emfdeps=1`: two EMF mods in Ember run whose dependency is not there (kiln_manage::emf_deps): Mage Skills needs EMF's
 *  lua mod, which comes with EMF's release zip (PepegaJedi's report, 0.2.0-dev.30.1), and Pet Tricks needs a mod whose
 *  mod.json entry gives its page. */
const EMF_DEPS_SEED = typeof location !== "undefined" && new URLSearchParams(location.search).has("emfdeps");
if (EMF_DEPS_SEED) {
  seedMods.emf.push(
    { id: "mage_skills", name: "Mage Skills", version: "1.0.0", side: "both", depends: { lua: ">=1.36" } },
    { id: "pet-tricks", name: "Pet Tricks", version: "0.2.0", side: "both", depends: { "pet-core": { version: "^2.0", url: "https://www.nexusmods.com/enshrouded/mods/9001" } } },
  );
}
/** EMF's own mods (kiln_manage::emf_deps::EMF_RELEASE_MODS) and EMF's page. */
const EMF_RELEASE: Record<string, string> = { lua: "EMF's lua mod", beastmaster: "Beastmaster", bounty: "Bounty Board", champion: "Wandering Champion",
  grave: "Grave Keeper", handover: "Hand-over", kill_counter: "Kill Counter", life_skills: "Life Skills", minimap: "Minimap", necromancer: "Necromancer" };
const EMF_PAGE = "https://github.com/matenm/emf-enshrouded";

/** kiln_manage::emf_deps::profile_problems, as far as the mock goes: a required dependency missing or switched off. */
function emfProblems(p: Profile): EmfDepProblem[] {
  const out: EmfDepProblem[] = [];
  for (const m of p.mods.emf.filter((x) => x.enabled)) {
    for (const [dep, spec] of Object.entries(manifestOf("emf", m.id, m.version)?.depends ?? {})) {
      const d = typeof spec === "string" ? { version: spec } : spec;
      const have = p.mods.emf.find((x) => x.id === dep);
      if (d.optional || (have && have.enabled)) continue;
      const release = EMF_RELEASE[dep];
      out.push({ id: m.id, dep, depName: have ? manifestOf("emf", have.id, have.version)?.name ?? dep : release ?? null, range: d.version ?? "",
        state: have ? "off" : "missing", found: have?.version ?? null, withEmf: !!release, url: d.url ?? (release ? EMF_PAGE : null),
        reason: `needs ${dep}, which is ${have ? "switched off" : "not installed"}` });
      break;
    }
  }
  return out;
}

const library: LibraryEntry[] = FRESH ? [] : MOD_KINDS.flatMap((kind) => [...seedMods[kind]].sort((a, b) => a.id < b.id ? -1 : a.id > b.id ? 1 : 0)
  .map((m) => entryFor(kind, m, "2026-11-0" + (2 + (m.id.length % 3)) + "T09:00:00Z")));

/** `?kept=1`: Zoom Out's earlier version 0.1.0 is kept for a roll back (kiln_manage::kept), with a settings file. */
const KEPT_SEED = typeof location !== "undefined" && new URLSearchParams(location.search).has("kept");
if (KEPT_SEED) {
  const e = library.find((l) => l.kind === "greeneye" && l.id === "zoom-out");
  if (e) {
    const hash = kdh("greeneye" + e.id + "0.1.0");
    e.versions.push({ ...e.versions[0], version: "0.1.0", hash, folder: `library/greeneye/zoom-out/0.1.0+${hash.slice(5, 13)}`, imported: "2026-10-30T09:00:00Z",
      manifest: { ...(e.versions[0].manifest as object), version: "0.1.0" } as unknown as Json,
      kept: { kept: "2026-11-02T10:00:00Z", replacedBy: e.versions[0].version, settings: ["greeneye/zoom-out/zoom.ini"], settingsSize: 120 } });
  }
}

/** library_rollback and an Undo of one (profile_revert): the version left is kept, a kept one that comes back is not;
 *  kept versions after the others (kiln_manage::kept::follow). */
function followKept(profileId: string, kind: ModKind, id: string, from: VersionRef, to: VersionRef) {
  const e = library.find((l) => l.kind === kind && l.id === id);
  if (!e) return;
  const keep = settings.keepVersions ?? 1;
  const at = (r: VersionRef) => e.versions.find((v) => v.version === r.version && v.hash === r.hash);
  const back = at(to);
  const settingsOf = back?.kept?.settings ?? [];
  if (back) back.kept = null;
  const left = at(from);
  if (left && keep > 0) left.kept = { kept: now(), replacedBy: to.version, settings: settingsOf.length ? settingsOf : [], settingsSize: 0, profiles: [profileId] };
  const kept = e.versions.filter((v) => v.kept).sort((a, b) => b.kept!.kept.localeCompare(a.kept!.kept));
  kept.slice(keep).forEach((v) => { v.kept = null; });
  e.versions.sort((a, b) => Number(!!a.kept) - Number(!!b.kept) || b.version.localeCompare(a.version));
}

function mockRollback(a: Record<string, unknown>): RollbackReport {
  const pid = S(a.profileId), kind = a.kind as ModKind, id = S(a.id), to = a.to as VersionRef | null;
  const p = clone(profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`));
  const list = p.mods[kind] ?? [];
  const i = list.findIndex((m) => m.id === id);
  if (i < 0) reject("not_found", `${id} is not in the profile ${p.name}: add it there first`);
  const e = library.find((l) => l.kind === kind && l.id === id);
  const kept: LibraryVersion[] = (e?.versions ?? []).filter((v) => v.kept && !(v.version === list[i].version && v.hash === list[i].hash));
  const pick = to ? kept.find((v) => v.version === to.version && v.hash === to.hash) : kept[0];
  if (!pick) reject("not_found", `No earlier version of ${e?.name ?? id} is kept for a roll back.`);
  const before = list[i];
  const after = { ...before, version: pick!.version, hash: pick!.hash };
  const files = pick!.kept?.settings ?? [];
  list[i] = after;
  const next = { ...p, generation: p.generation + 1, updated: now() };
  profiles.set(pid, next);
  followKept(pid, kind, id, before, after);
  reapplyWhereActive(pid);
  return { profileId: pid, profileName: p.name, kind, id, name: e?.name ?? id, from: { version: before.version, hash: before.hash },
    to: { version: after.version, hash: after.hash }, changes: [{ kind, before, after }], kept: (settings.keepVersions ?? 1) > 0, settings: files, notices: [] };
}

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
      emf: [pm("emf", "beastmaster"), ...(EMF_DEPS_SEED ? [pm("emf", "mage_skills"), pm("emf", "pet-tricks")] : [])],
      xhl: [], eml: [pm("eml", "loot-tables-x", false)], shroudforge: [pm("shroudforge", "better-chests")], set: [], greeneye: [pm("greeneye", "zoom-out")],
      flame: [], shroudtopia: [pm("shroudtopia", "fly-mode")], enshwick: [], shroudlink: [], ember: [], dllmod: [],
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

/** The player's own ShroudForge folders as engine_own_folders lists them (engine_mods::own_folders): where each is, the name Kiln would give
 *  it, and why Kiln cannot take it over, if so. An activation says its own-folder note for each. Tests:
 *  `__kilnMock.setOwnFolder`. */
const ownFolders = new Map<string, { path: string; renameTo?: string; refusal?: string }>();
/** engine_mods::own_folder's sentence for a ShroudForge entry the player's own folder serves. */
const ownFolderNote = (id: string, path: string, on: boolean) => on
  ? `shroudforge:${id}: the game uses your own folder ${path} for this mod; Kiln leaves it as it is and places no copy of its own.`
  : `shroudforge:${id}: it is off in the profile, and your own folder ${path} stays as it is (Kiln never writes into a folder it did not place). `
    + `While Kiln hosts ShroudForge, Kiln has its runtime skip this folder at each start when that runtime takes Kiln's skip list `
    + `(Kiln's bundled runtime does from 0.2.0-dev.31.12 on); kiln.log says at each start whether it was skipped. Another runtime (your `
    + `installed ShroudForge, "Use my installed ShroudForge") or ShroudForge's own loader still runs it while its own ShroudForge switch `
    + `is on: to stop that, switch it off in Kiln's in-game menu (Mods > ShroudForge) or in ShroudForge's Modloader, choose "Let Kiln manage `
    + `this folder" in Notes, or move the folder out of the mods folder.`;
/** kiln_manage::cook in the fake game: the mods with something to cook (by id), why each is due, and what its tools
 *  say when its cook fails. Tests: `__kilnMock.setCook`. */
const cookMods = new Map<string, { reason: CookReason | null; fails?: string; last?: CookedMod }>();
/** How long each line of a cook task shows (tests: `__kilnMock.setCookPace`). */
let cookStepMs = 120;
/** What boot_snapshot_run (Play once with another profile) comes to, by profile id: absent = no mod of that start
 *  needs the snapshot (`skipped`). Tests: `__kilnMock.setOnceBootSnapshot`. */
const onceBootSnapshots = new Map<string, BootSnapshot>();

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
  // the player's own folders that serve ShroudForge entries: a note each, a safety note for a mod that is off
  for (const m of p.mods.shroudforge ?? []) {
    const own = ownFolders.get(m.id);
    if (!own) continue;
    const note = ownFolderNote(m.id, own.path, m.enabled);
    a.warnings = [...(a.warnings ?? []), note];
    if (!m.enabled) a.safetyNotes = [...(a.safetyNotes ?? []), note];
  }
  activations[gameId] = a;
  remembered[gameId] = pid;
  return a;
}
if (!FRESH) activate("ember-run", "client-steam");

/** cook::plan: the enabled Kiln mods of the profile the game uses that have something to cook. */
function cookPlan(gameId: string): CookJob[] {
  const g = gameOf(gameId);
  const p = profiles.get(activations[gameId]?.profileId ?? "");
  if (!p) return [];
  return (p.mods.kiln ?? []).filter((m) => m.enabled && cookMods.has(m.id)).map((m) => {
    const c = cookMods.get(m.id)!;
    const name = library.find((e) => e.kind === "kiln" && e.id === m.id)?.name ?? m.id;
    return { id: m.id, name, folder: `${g.path}/kiln/profiles/${p.id}/kiln/${m.id}`, identity: m.hash, ...(c.reason ? { reason: c.reason } : {}), ...(c.last ? { last: c.last } : {}) };
  });
}

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
  return { side, kiln: { order: kiln.order, rejected: kiln.rejected.sort((a, b) => a.dir.localeCompare(b.dir)) }, native, warnings, withWorld, emf: emfProblems(p) };
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
    const file = `${g?.path}/shroudforge/mods/${id}/extended.mod.json`;
    // the game's copy is there to open while this profile is the game's (engine_mods::settings `openFile`)
    return { kind, id, format: "extended.mod.json", file, schema: sf.schema, values: clone(sf.values), note: null,
      ...(sfOwn.has(id) ? { ownFolder: true } : {}), ...(g && activations[g.id]?.profileId === pid ? { openFile: file } : {}) };
  }
  if (kind === "shroudtopia") {
    const values = shroudtopiaFiles.get(pid)?.[id] ?? { active: true, flySpeed: 1.5, hotkey: "F6" };
    const file = `/home/player/.local/share/kiln/profiles/${pid}/config/shroudtopia/shroudtopia.json`;
    // shroudtopia.json exists once a setting was written (or the game wrote its defaults)
    return { kind, id, format: "shroudtopia.json", file, schema: schemaFromValues(values), values: clone(values), note: null,
      ...(shroudtopiaFiles.has(pid) ? { openFile: file } : {}) };
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

/** Tasks of a kind held before their last step until a test opens the gate (`__kilnMock.holdTasks(kind, gate)`): a slow
 *  activation of that kind. `fail`: the task then fails with this message instead of finishing. */
const taskHolds = new Map<string, { gate: string; fail?: string }>();

type TaskStep = string | { stage: string; message: string };

/** A fake long task: `steps` progress messages and optional stage IDs, then `finish()` gives the result. `failed`:
 *  called when it fails instead (a held task's `fail`, or `finish` throwing). */
function startTask(kind: string, title: string, steps: readonly TaskStep[], finish: () => unknown, stepMs = 450, failed?: () => void): string {
  const id = `t${++taskSeq}-${kind}`;
  let t: Task = { id, kind, title, state: "running", progress: 0, message: null, started: now(), finished: null };
  const emitStep = (step: TaskStep, progress: number) => {
    const fields = typeof step === "string" ? { message: step } : step;
    t = { ...t, ...fields, progress };
    emitTask(t);
  };
  if (steps.length) emitStep(steps[0], 0);
  else emitTask(t);
  const hold = taskHolds.get(kind);
  let i = 0;
  const end = () => {
    let state: TaskState = "done";
    let result: unknown = null;
    let error: KilnError | undefined;
    try {
      if (hold?.fail) reject("io", hold.fail);
      result = finish();
    } catch (e) { state = "failed"; error = e as KilnError; failed?.(); }
    emitTask({ ...t, state, progress: 1, message: error ? error.message : "Done", finished: now(), result, error });
  };
  const tick = () => {
    if (cancels.has(id)) { emitTask({ ...t, state: "canceled", message: "Canceled", finished: now(), error: { code: "canceled", message: "Canceled." } }); return; }
    i++;
    if (i < steps.length) {
      emitStep(steps[i], i / steps.length);
      setTimeout(tick, stepMs);
      return;
    }
    if (hold && !gateOf(hold.gate).open) { gateOf(hold.gate).waiters.push(() => setTimeout(end, 0)); return; }
    end();
  };
  setTimeout(tick, stepMs);
  return id;
}

// ---------------------------------------------------------------- game state

function gameOf(id: unknown): GameInstall {
  return games.find((g) => g.id === id) ?? reject("not_found", `No game '${String(id)}'.`);
}

// ---------------------------------------------------------------- direct starts (kiln_manage::direct, dev.29.17)

/** The player's start choices per game (InstallRecord.start). */
const startSettings: Record<string, StartSettings> = {};
const HEROIC_GE = "/home/player/.config/heroic/tools/proton/GE-Proton10-25";
const mockRunners: RunnerInfo[] = NO_STEAM ? [
  { kind: "umu", path: "/usr/bin/umu-run", label: "umu-run", source: "system", proton: HEROIC_GE },
  { kind: "proton", path: HEROIC_GE, label: "GE-Proton10-25", source: "compat" },
  { kind: "wine", path: "/usr/bin/wine", label: "Wine (/usr/bin/wine)", source: "system" },
] : [
  { kind: "proton", path: `${STEAM}/compatibilitytools.d/GE-Proton10-25`, label: "GE-Proton10-25", source: "compat" },
  { kind: "proton", path: `${STEAM}/steamapps/common/Proton 9.0`, label: "Proton 9.0", source: "steam" },
  { kind: "wine", path: "/usr/bin/wine", label: "Wine (/usr/bin/wine)", source: "system" },
];
/** A folder no Steam library knows (direct::is_custom). */
const isCustom = (g: GameInstall) => g.kind === "client" && g.source === "manual" && !g.library;
function startOf(g: GameInstall): StartMode {
  if (g.kind !== "client") return "steam";
  return startSettings[g.id]?.mode ?? (isCustom(g) ? "direct" : "steam");
}
/** The Steam copy's build is known; a folder added by hand holds another build (`?mock=known-custom`: a known one). */
function buildOf(g: GameInstall): BuildCheck {
  const sha = g.exeSha256 ?? hex(g.path).padEnd(64, "0").slice(0, 64);
  const known = g.source === "steam" || MODE === "known-custom";
  return { sha256: sha, short: sha.slice(0, 12), known, from: runtime[g.id]?.installed ? "game" : "bundle" };
}
function startSetup(g: GameInstall): StartSetup {
  const settings = startSettings[g.id] ?? {};
  const own = `/home/player/.local/share/kiln-prefixes/${g.id}`;
  const runner = settings.runner ? mockRunners.find((r) => r.path === settings.runner) ?? { kind: "proton", path: settings.runner, label: settings.runner.split("/").pop() ?? settings.runner, source: "custom" } : mockRunners[0];
  const compat = settings.prefix ?? own;
  const pfx = runner.kind === "proton" || !settings.prefix ? `${compat}/pfx` : compat;
  const exe = `${g.path}/enshrouded.exe`;
  // as kiln_manage::direct::command_for builds it (Proton without Steam: Kiln's own empty client folder)
  const env: [string, string][] = runner.kind === "umu"
    ? [["WINEPREFIX", pfx], ["GAMEID", "umu-default"], ...(runner.proton ? [["PROTONPATH", runner.proton] as [string, string]] : []), ["WINEDLLOVERRIDES", "dinput8=n,b"]]
    : runner.kind === "proton"
      ? [["STEAM_COMPAT_DATA_PATH", compat], ["STEAM_COMPAT_CLIENT_INSTALL_PATH", NO_STEAM ? "/home/player/.local/share/kiln-prefixes/_compat-client" : STEAM], ["WINEDLLOVERRIDES", "dinput8=n,b"]]
      : [["WINEPREFIX", pfx], ["WINEDLLOVERRIDES", "mshtml=;dinput8=n,b"]];
  return {
    mode: startOf(g), settings, custom: isCustom(g), windows: WIN_MOCK, steamFound: !NO_STEAM, runners: WIN_MOCK ? [] : mockRunners, runner: WIN_MOCK ? null : runner,
    defaultRunner: WIN_MOCK ? null : mockRunners[0], ownPrefix: WIN_MOCK ? null : own,
    command: WIN_MOCK ? { program: `${g.path}\\enshrouded.exe`, args: [], cwd: g.path, env: [], envRemove: ["KILN_DISABLE"], runner: null, prefix: null } : {
      program: runner.kind === "proton" ? `${runner.path}/proton` : runner.path, args: runner.kind === "proton" ? ["run", exe] : [exe], cwd: g.path,
      env, envRemove: ["WINEPREFIX", "SteamAppId", "SteamGameId", "GAMEID", "PROTONPATH"], runner, prefix: pfx,
    },
  };
}
/** `?mock=windows-start`: the start dialog as Windows shows it (no Proton, Wine or prefix). */
const WIN_MOCK = MODE === "windows-start";

/** Games started in this session (a first run has no boot.json before its first start). */
const launched = new Set<string>();

function bootFor(gameId: string): BootJson | null {
  const r = live(gameId);
  if (!r && FRESH && !launched.has(gameId)) return null;
  if (r && noProxy) return null;
  const bootAt = r?.bootAt ?? (r ? r.at + 2500 : 0);
  if (!r || Date.now() < bootAt || r.mode === "vanilla") return r ? null : clone(bootExample as unknown as BootJson);
  const b = clone(bootExample as unknown as BootJson);
  b.pid = r.pid;
  b.written = new Date(bootAt).toISOString();
  if (r.safeIdle) b.safeIdle = r.safeIdle;
  b.playOnce = { mode: r.mode === "once" ? "profile" : r.mode === "no-mods" ? "no-mods" : "none", nonce: r.nonce };
  const p = r.profile ? profiles.get(r.profile) : null;
  b.profile = { ...b.profile, mode: r.mode === "no-mods" ? "no-mods" : r.mode === "once" ? "once" : "active", id: p?.id ?? null, name: p?.name ?? null,
    generation: activations[gameId]?.generation ?? 1 };
  return b;
}

function status(gameId: string): GameStatus {
  const g = gameOf(gameId);
  const r = live(gameId);
  return {
    id: g.id, running: !!r, pid: r?.pid ?? null, started: r ? new Date(r.at - (noProxy ? 31_000 : 0)).toISOString() : null, steamUpdating: !!steamUpdating[gameId], runtime: withMissing(runtime[gameId] ?? { installed: false, receipt: null, proxy: null, modified: [], upgradeAvailable: null }, g.kind),
    activeProfile: otherFolder[gameId] ? null : activations[gameId]?.profileId ?? null,
    ...(otherFolder[gameId] ? { otherFolderProfile: otherFolder[gameId]! } : {}), playOncePending: !!r && r.mode !== "profile" && (noProxy || Date.now() - r.at < 2500),
    boot: bootFor(gameId), launchOptions: MODE === "native-runner"
      // Steam's selected runner loads dinput8 natively (GE-Proton 11-11+): no routine advice, the fallback kept
      ? { value: null, hasDllOverride: false, chainedOverrideMissing: false, advice: null, fallbackAdvice: 'WINEDLLOVERRIDES="dinput8=n,b" %command%', dinput8NativeByRunner: true }
      : noProxy ? { value: null, hasDllOverride: false, chainedOverrideMissing: false, advice: 'WINEDLLOVERRIDES="dinput8=n,b" %command%' }
      : { value: 'WINEDLLOVERRIDES="dinput8=n,b" %command%', hasDllOverride: true, chainedOverrideMissing: MODE === "chained-override",
          advice: MODE === "chained-override" ? 'WINEDLLOVERRIDES="dinput8,xinput9_1_0=n,b" %command%' : null },
    newerMods: newerMods[gameId] ?? [],
    ...(graphicsTools[gameId]?.length ? { graphicsTools: graphicsTools[gameId] } : {}),
    ...(g.kind === "client" && graphicsStepAside[gameId]?.length ? { graphicsStepAside: graphicsStepAside[gameId] } : {}),
    ...(g.kind === "client" && graphicsState[gameId] ? { graphicsState: { current: !!r, state: graphicsState[gameId] } } : {}),
    ...(g.kind === "client" ? { start: startOf(g), build: buildOf(g) } : {}),
    ...(startOf(g) === "direct" ? { launchOptions: null } : {}),
  };
}

function emitGame(gameId: string, kind: "started" | "stopped" | "boot" | "mods" | "native" | "status") {
  const s = status(gameId);
  mockEmit("kiln://game", { gameId, kind, pid: s.pid, status: s });
}

let stopScript: StopScript | null = null;
const stopStorage = "kiln-mock-stopped-starts";
const stoppedStarts: Record<string, import("./commands").StoppedStart | null> = (() => {
  try { return JSON.parse(localStorage.getItem(stopStorage) ?? "{}"); } catch { return {}; }
})();
/** A Steam start without the launcher's Play path. Keep old IPC stop evidence to exercise late replies too. */
function mockExternalStart(gameId: string, at = Date.now()) {
  gameOf(gameId);
  running[gameId] = { pid: ++pidSeq, mode: "profile", nonce: null, at, profile: activations[gameId]?.profileId ?? null };
  launched.add(gameId);
  emitGame(gameId, "started");
  setTimeout(() => emitGame(gameId, "boot"), 2600);
}
export function scriptStoppedStart(kind: StopScript) { stopScript = kind; }
/** (0.2.0-dev.31.13) commands/launch.rs refuse_pending: another start of the game is pending (Steam asked, no game yet). */
function refusePending(gameId: string) {
  if (mockHoldsNextStart(gameId, !!live(gameId))) reject("busy", "The game is already starting.", { kind: "already_starting" });
}
function launch(gameId: string, req: LaunchRequest): LaunchResult {
  // launch_play never starts a start whose warnings nobody acknowledged
  if (!sfConfirming) sfChecks.push(`play ${gameId} ${req.mode}`);
  if (!sfConfirming && sfStart[gameId]?.warnings.length) sfPending(sfPreflight(gameId, req), "Some ShroudForge mods will not run this start. Read the warning and confirm it first; the game was not started.");
  const g = gameOf(gameId);
  stoppedStarts[gameId] = null;
  localStorage.setItem(stopStorage, JSON.stringify(stoppedStarts));
  const stop = stopScript; stopScript = null;
  // a scripted start Steam never made a game for (noProcess) is no game: Play again replaces it (once its wait is over)
  if (running[gameId] && running[gameId]!.from !== Infinity) reject("game_running", "The game is already running.");
  refusePending(gameId);
  if (req.mode === "once" && !req.profileId) reject("invalid_argument", "Pick a profile to play once.");
  const st = runtime[gameId];
  if (!st?.installed && req.mode !== "vanilla") reject("unsupported", "Kiln is not installed in this game.");
  const active = req.mode === "profile" && activations[gameId] ? profiles.get(activations[gameId]!.profileId) : undefined;
  if (active) rejectChanged(active);
  // Play writes a changed profile again first (profile::refresh_active): a planned report stands for what that kept
  const act = req.mode === "profile" && activationPlan && activations[gameId] ? activate(activations[gameId]!.profileId, gameId) : undefined;
  const pid = ++pidSeq;
  const nonce = req.mode === "profile" ? null : hex(String(pid)).slice(0, 32);
  const profile = req.mode === "once" ? req.profileId ?? null : req.mode === "profile" ? activations[gameId]?.profileId ?? null : null;
  let backup: SaveBackup | null = null;
  if (!req.skipBackup && req.mode !== "vanilla") backup = makeBackup(gameId, "launch", null, profile);
  running[gameId] = { pid, mode: req.mode, nonce, at: Date.now(), profile };
  // the start's process is the game's now (a scripted one's comes at its `from`: still starting until then)
  startsBegun.delete(gameId);
  launched.add(gameId);
  if (stop) setTimeout(() => {
    const ended = Date.now();
    stoppedStarts[gameId] = readMockStop(mockStoppedFile(stop, pid, ended), pid, running[gameId]?.at ?? ended, ended);
    localStorage.setItem(stopStorage, JSON.stringify(stoppedStarts));
    mockStopGame(gameId);
    (window as unknown as { __kilnStopCompleted: number }).__kilnStopCompleted = ((window as unknown as { __kilnStopCompleted?: number }).__kilnStopCompleted ?? 0) + 1;
  }, 250);

  const sc = scriptedStart(gameId);
  if (sc) mockAsked(gameId);
  if (sc) {
    // the scripted start: Steam starts the process when the script says, boot.json comes at the end of Kiln's boot
    // (a safe idle start: where it stops, with its reason)
    const at = (ms: number) => sc.began + ms * sc.script.scale;
    const idle = sc.script.idle;
    const bootMs = idle ? bootJsonAt({ ...sc.script, runtime: sc.script.runtime.slice(0, sc.script.runtime.findIndex((x) => x.id === idle.at) + 1), loadingAfter: idle.at }) : bootJsonAt(sc.script);
    Object.assign(running[gameId]!, sc.script.noProcess ? { from: Infinity }
      : { from: at(processAt(sc.script)), at: at(processAt(sc.script)), bootAt: at(bootMs), safeIdle: idle?.why ?? null });
    if (!sc.script.noProcess) setTimeout(() => emitGame(gameId, "started"), Math.max(0, at(processAt(sc.script)) - Date.now()) + 50);
    if (!sc.script.noProcess) setTimeout(() => emitGame(gameId, "boot"), Math.max(0, at(bootMs) - Date.now()) + 50);
  } else {
    setTimeout(() => emitGame(gameId, "started"), 300);
    setTimeout(() => emitGame(gameId, "boot"), 2600);
    setTimeout(() => emitGame(gameId, "mods"), 3200);
  }
  const via = startOf(g);
  return { gameId: g.id, mode: req.mode, nonce, backup, uri: via === "direct" ? "" : "steam://rungameid/1203620", started: now(), via, ...(act ? { activation: act } : {}) };
}

/** Dev/test helper: the fake game was started outside the launcher (Steam) `secondsAgo` seconds ago. */
export function mockStartOutside(secondsAgo: number, gameId = "client-steam") {
  if (running[gameId]) return;
  running[gameId] = { pid: ++pidSeq, mode: "profile", nonce: null, at: Date.now() - secondsAgo * 1000, profile: activations[gameId]?.profileId ?? null };
  emitGame(gameId, "started");
}

/** Dev/test helper: the fake game exits. */
/** A crash: Kiln writes a crash report now, and the game closes (tests: `__kilnMock.crash()`). */
function mockCrash(gameId = "client-steam") {
  const now = new Date();
  crashes.unshift({ name: `${now.toISOString().slice(0, 19).replace(/:/g, "-")}Z-crash.txt`, created: now.toISOString(), size: 4_211 });
  mockStopGame(gameId);
}

export function mockStopGame(gameId = "client-steam") {
  if (!running[gameId]) return;
  running[gameId] = null;
  startsBegun.delete(gameId);
  mockStartEnded(gameId);
  emitGame(gameId, "stopped");
}

// ---------------------------------------------------------------- last server (Play's refusal banner)

// <game>/kiln/state/last-server.json and dismissed.json, filtered like commands/modpack.rs (worth_showing_in; the mock's
// game has no server id of its own, so only `hosted` says its own world).
let lastServer: LastServer | null = FRESH ? null : clone(lastServerExample as unknown as LastServer);
const dismissedOffers: Record<string, string> = {};
const dismissKey = (l: LastServer) => (l.modpack ? l.modpack.sha256 : `refused ${l.written}`);
function pendingLastServer(): LastServer | null {
  const l = lastServer;
  if (!l || dismissedOffers[l.server.id ?? ""] === dismissKey(l)) return null;
  return l.outcome === "rejected" || (l.outcome === "welcome" && !l.hosted && !!l.modpack && l.missing.length > 0) ? l : null;
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

/** "Clear settings" in the Configs window: each file that went (tests: `__kilnMock.configsCleared()`). */
const configsCleared: string[] = [];

/** kiln_manage::config_orphans::Installed: the ids of every mod a row can show: the Library (any version), the target
 *  profile's entries (on or off), and the game folder's found mods (their id, name and file or folder name). */
function mockInstalledIds(t: ConfigTarget): Set<string> {
  const ids = new Set<string>();
  for (const l of library) if (l.versions.length) ids.add(l.id.toLowerCase());
  const p = "profile" in t ? profiles.get(t.profile) : undefined;
  for (const list of Object.values(p?.mods ?? {})) for (const m of list ?? []) ids.add(m.id.toLowerCase());
  for (const g of gameFolder) for (const n of [g.id, g.name, g.path.split("/").pop()]) if (n) ids.add(n.toLowerCase());
  return ids;
}

/** kiln_manage::config_orphans::owner_of, as far as the mock's files go: the snapshot's mod, else `<kind>/<id>.<ext>` of an
 *  engine's kind; Kiln's own files and anything else have none. */
const KILN_OWN_FILES = ["native", "kiln", "network", "chat", "permissions", "keybinds", "rules", "compat-rules", "tk-boot", "tk-config", "menus-client", "menus-server", "config-sync"];
function mockOwner(file: string, mod: string | null): string | null {
  const parts = file.split("/");
  const stem = (n: string) => (n.includes(".") ? n.slice(0, n.lastIndexOf(".")) : null);
  if (parts.length === 1 && KILN_OWN_FILES.includes((stem(parts[0]) ?? "").toLowerCase())) return null;
  // EMF's own: its core's settings and the switch of the Lua host Kiln bundles
  if (parts.length === 2 && parts[0] === "emf" && ["emf_core", "lua"].includes((stem(parts[1]) ?? "").toLowerCase())) return null;
  let id = mod;
  if (!id) {
    const engine = MOD_KINDS.find((k) => k === parts[0] && !["kiln", "native", "plugin"].includes(k));
    id = engine && parts.length === 2 ? stem(parts[1]) : null;
    if (id?.toLowerCase() === "shroudtopia" && parts[0] === "shroudtopia") id = null;
  }
  return id && id.toLowerCase() !== "kiln" ? id : null;
}
/** Not installed anywhere, and its mod known for sure (an unknown mapping is never an orphan). Profiles only. */
function mockOrphaned(installed: Set<string>, file: string, mod: string | null): boolean {
  const owner = mockOwner(file, mod);
  return !!owner && !installed.has(owner.toLowerCase());
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
      if (e.min !== undefined && !e.softMin && v < e.min) errors.push({ path: e.path, message: `below the minimum ${e.min}` });
      if (e.max !== undefined && !e.softMax && v > e.max) errors.push({ path: e.path, message: `above the maximum ${e.max}` });
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

// kiln.json shroudforge.backend as kiln_manage::sfi_backend reads and writes it
const SF_BACKENDS: SfBackend[] = ["hosted", "installed", "kiln"];
/** Why Kiln's interpreter is held in the mock build (`__kilnMock.setSfHeld`; null: not held); kiln.json "preview" lifts it. */
let sfHeld: string | null = "game test pending: sfi-backend-roundtrip, sfi-save-sensitive, sfi-three-surface-warning";
let sfRoute: NonNullable<SfBackendCard["interpreterRoute"]> = "ready";
const sfLast: Record<string, SfBackendCard["last"]> = {};
const sfRevision = (values: Json) => hex(JSON.stringify(values ?? {}));
const sfSection = (values: Json) => {
  const sf = (values as Record<string, Json> | null)?.shroudforge;
  return sf && typeof sf === "object" && !Array.isArray(sf) ? sf as Record<string, Json> : null;
};
function sfSetting(values: Json): SfSetting {
  const sf = sfSection(values);
  if (sf && "backend" in sf) {
    const b = sf.backend;
    return typeof b === "string" && (SF_BACKENDS as string[]).includes(b) ? { kind: "explicit", backend: b as SfBackend }
      : { kind: "invalid", value: typeof b === "string" ? `"${b}"` : JSON.stringify(b) };
  }
  return sf?.useInstalled === true ? { kind: "inherited", backend: "installed" } : { kind: "default" };
}
function sfHeldNow(values: Json): string | null {
  const p = (values as Record<string, Json> | null)?.preview;
  const lifted = p === true || p === "all" || (Array.isArray(p) && p.includes("kiln-sfi.interpreter"));
  return lifted ? null : sfHeld;
}
function sfResolve(values: Json): SfResolution {
  const s = sfSetting(values);
  const base = { refusal: null, envOffNote: null } as const;
  if (s.kind === "invalid") return { ...base, requested: null, selected: null, source: "setting", refusal: "settingInvalid", reason: `kiln.json shroudforge.backend: ${s.value} is not "hosted", "installed" or "kiln"` };
  if (s.kind === "default") return { ...base, requested: "hosted", selected: "hosted", source: "default", reason: "no kiln.json setting: hosted, the default" };
  if (s.kind === "inherited") return { ...base, requested: "installed", selected: "installed", source: "useInstalled", reason: "kiln.json shroudforge.useInstalled: true (the older setting): installed" };
  const held = s.backend === "kiln" ? sfHeldNow(values) : null;
  if (held) return { ...base, requested: "kiln", selected: null, source: "setting", refusal: "interpreterHeld", reason: `kiln.json asks for Kiln's interpreter (shroudforge.backend: "kiln"), but the capability kiln-sfi.interpreter is held (${held})` };
  return { ...base, requested: s.backend, selected: s.backend, source: "setting", reason: `kiln.json shroudforge.backend: "${s.backend}"` };
}
/** kiln.json's values with `backend` written (null: removed) and the older useInstalled removed. */
function sfWritten(values: Json, backend: SfBackend | null): Json {
  const v = clone(values ?? {}) as Record<string, Json>;
  const sf = { ...(sfSection(v) ?? {}) };
  delete sf.useInstalled;
  if (backend) sf.backend = backend; else delete sf.backend;
  if (Object.keys(sf).length) v.shroudforge = sf; else delete v.shroudforge;
  return v;
}
function sfCard(gameId: string): SfBackendCard {
  const v = kilnFile(gameId).values;
  const next = sfResolve(v);
  const held = sfHeldNow(v);
  const route = emlRLoader ? "eml-r" : sfRoute;
  return { gameId, setting: sfSetting(v), next, revision: sfRevision(v), ...(held ? { interpreterHeld: held } : {}),
    ...(next.selected === "kiln" ? { interpreterRoute: route, ...((route === "own-loader" || route === "eml-r") ? { ownLoader: "dbghelp.dll" } : {}) } : {}),
    ...(sfLast[gameId] ? { last: sfLast[gameId] } : {}) };
}
function sfPreview(gameId: string, backend: SfBackend | null): SfBackendPreview {
  const v = kilnFile(gameId).values;
  const revision = sfRevision(v);
  const held = sfHeldNow(v);
  if (backend === "kiln" && held) return { gameId, backend, revision, refused: `Kiln's interpreter cannot be chosen: the capability kiln-sfi.interpreter is held (${held})`, unchanged: false };
  const after = sfWritten(v, backend);
  return { gameId, backend, revision, next: sfResolve(after), unchanged: JSON.stringify(after) === JSON.stringify(v ?? {}) };
}

// the ShroudForge row in kiln mode, kiln_manage::sfi_state, and the restore's BASE
const SFI_ROW = typeof location !== "undefined" ? new URLSearchParams(location.search).get("sfiRow") : null;
const SFI_BASE = typeof location !== "undefined" ? new URLSearchParams(location.search).get("restoreBase") : null;
const SFI_ROWS: Record<string, { state: Json | null; error?: string }> = {
  running: { state: { mode: "kiln interpreter", status: "running", backend: "kiln", launchId: "00000000000000b2", pid: 4242, updatedMs: 1795000000000, previous: "incomplete", mods: [
    { id: "skeleton-commander", version: "1.2.0", state: "running" },
    { id: "sf-unlock-blueprints", version: "1.0.2", state: "data applied" },
    { id: "combat_tweaks", version: "0.7.2", state: "skipped", reason: "it needs runtime.ui.draw_list, which Kiln's interpreter does not offer yet" },
    { id: "sf-survival", version: "1.0.0", state: "off" }] } },
  refused: { state: { mode: "kiln interpreter", status: "refused", backend: "kiln", launchId: "00000000000000b2", pid: 4242, updatedMs: 1795000000000,
    reason: "kiln-eml's startup pass granted no archive generation",
    mods: [{ id: "combat_tweaks", version: "0.7.2", state: "skipped", reason: "the startup pass failed" }] } },
  stale: { state: { mode: "kiln interpreter", status: "unknown", recordPhase: "running", staleCode: "not-running", backend: "kiln", launchId: "00000000000000b2", pid: 4242,
    updatedMs: 1795000000000, reason: "kiln-sfi's record says running, but the game is not running: that start ended without its last record (it may have crashed)", mods: [] } },
  corrupt: { state: null, error: "/games/Enshrouded/kiln/state/sfi.json: expected value at line 1 column 3" },
};
function sfiEngineRows<T extends { kind: string }>(path: string, rows: T[]): (T | EngineStatus)[] {
  const r = SFI_ROW ? SFI_ROWS[SFI_ROW] : undefined;
  if (!r) return rows;
  return rows.map((s) => s.kind !== "shroudforge" ? s : { kind: "shroudforge", plugin: "kiln-sfi", state: r.state, error: r.error ?? null, file: `${path}/kiln/state/sfi.json` });
}
const SFI_BASES: Record<string, DataRestoreBase> = {
  recorded: { state: "recorded", token: "first-managed-unclassified", label: "unclassified: game files as found when Kiln first managed this install", baselineSha256: hex("base"), reason: null },
  unrecorded: { state: "unrecorded", token: "unrecorded", label: "unrecorded: a working baseline from an earlier start; where its files came from was not recorded", baselineSha256: hex("base"),
    reason: "the origin in Kiln's record is of other backups than the ones in the game folder now" },
  unknown: { state: "unknown", token: null, label: null, baselineSha256: null, reason: "the game data Kiln backed up is not in the game folder (all of it): Kiln cannot tell what it would put back" },
};
const sfiRestoreBase = (): DataRestoreBase | null => (SFI_BASE ? SFI_BASES[SFI_BASE] ?? null : null);

// the check before Play as kiln_manage::sfi_launch answers it: prepare, confirm, cancel
/** What the next start of a game warns about (`__kilnMock.setStartWarnings`; none: no dialog). */
const sfStart: Record<string, { warnings: StartWarning[]; advisories: StartWarning[] }> = {};
/** The waiting tokens: one prepared start per game; any confirm takes its token, whatever comes of it. */
const sfTokens = new Map<string, { gameId: string; request: LaunchRequest; warnings: string }>();
/** Each launch_play / prepare / confirm / cancel as `<command> <gameId> <mode>` (`__kilnMock.startChecks`). */
const sfChecks: string[] = [];
let sfConfirming = false;
function sfPreflight(gameId: string, request: LaunchRequest): Preflight {
  const s = sfStart[gameId] ?? { warnings: [], advisories: [] };
  for (const [k, v] of sfTokens) if (v.gameId === gameId) sfTokens.delete(k);
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  const token = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  const warnings = JSON.stringify(s.warnings);
  sfTokens.set(token, { gameId, request: clone(request) as LaunchRequest, warnings });
  return { gameId, request: clone(request) as LaunchRequest, token, expires: new Date(Date.now() + 600_000).toISOString(), warnings: clone(s.warnings) as StartWarning[],
    warningsSha256: hex(warnings), ...(s.advisories.length ? { advisories: clone(s.advisories) as StartWarning[] } : {}), reinspectsEveryPlay: false };
}
function sfPending(p: Preflight, message: string): never {
  reject("consent_required", message, { preflight: p });
}
const sfSame = (a: LaunchRequest, b: LaunchRequest) => a.mode === b.mode && (a.profileId ?? null) === (b.profileId ?? null) && !!a.skipBackup === !!b.skipBackup;
function sfPrepare(gameId: string, req: LaunchRequest): Preflight {
  sfChecks.push(`prepare ${gameId} ${req.mode}`);
  gameOf(gameId);
  if (running[gameId] && running[gameId]!.from !== Infinity) reject("game_running", "The game is already running.");
  if (req.mode === "once" && !req.profileId) reject("invalid_argument", "Pick a profile to play once.");
  if (!runtime[gameId]?.installed && req.mode !== "vanilla") reject("unsupported", "Kiln is not installed in this game.");
  return sfPreflight(gameId, req);
}
function sfConfirm(gameId: string, token: string, req: LaunchRequest): LaunchResult {
  sfChecks.push(`confirm ${gameId} ${req.mode}`);
  const p = sfTokens.get(token);
  sfTokens.delete(token);
  const refused = (why: string, message: string) => reject("refused", message, { token: why });
  if (!p) refused("unknown", "This start was already confirmed, canceled or replaced by a newer one, or the launcher restarted since: press Play again.");
  if (p!.gameId !== gameId) refused("game", "This confirmation belongs to another game: press Play again.");
  if (!sfSame(p!.request, req)) refused("request", "This confirmation belongs to another kind of start: press Play again.");
  const now = sfStart[gameId]?.warnings ?? [];
  if (now.length && JSON.stringify(now) !== p!.warnings)
    sfPending(sfPreflight(gameId, req), "What will not run this start changed since the warning was shown. Read it again and confirm it; the game was not started.");
  sfConfirming = true;
  try { return launch(gameId, req); } finally { sfConfirming = false; }
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
/** runtime_install's versions (tests: `__kilnMock.installs()`). */
const runtimeInstalls: string[] = [];
let settings: LauncherSettings = { steamRoots: [], defaultGameId: FRESH ? null : "client-steam", allowHttpModpacks: false, savesKeep: 10, updateCheck: true, animatedFire: true,
  addToActiveProfile: true, modFolderNotes: true, keepVersions: 1 };
// how the (Linux) launcher draws its window
// `?render=software`: the fallback's restart; `?render=marker`: a later start, from the marker the fallback wrote
/** `?ownFiles=uninstall.exe`: the launcher's own files an antivirus took (app_info.ownFilesMissing, inbox 92). */
const OWN_FILES = (typeof location !== "undefined" ? new URLSearchParams(location.search).get("ownFiles") ?? "" : "").split(",").filter(Boolean);
const RENDER_Q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("render") : null;
const FELL_BACK = "The launcher turned this on by itself: the page did not start within 15 s of the window with the GPU (2026-11-11T21:04:10Z)";
let render: RenderInfo = RENDER_Q === "software" ? { mode: "software", why: "restart", marker: true, markerWhy: FELL_BACK }
  : RENDER_Q === "marker" ? { mode: "software", why: "marker", marker: true, markerWhy: FELL_BACK }
  : { mode: "gpu", why: "default", marker: false };
// `?x11=default|restart|marker|tried|yours|off`: a Wayland session with XWayland (render.rs' X11 switch); absent: none
const X11_Q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("x11") : null;
if (X11_Q) render = { ...render, x11: { on: X11_Q === "restart" || X11_Q === "marker", why: X11_Q as NonNullable<RenderInfo["x11"]>["why"], auto: X11_Q !== "off",
  ...(X11_Q === "yours" ? { yours: "wayland" } : {}) } };
// `?nvidia=blocked|on|failed`: Ubuntu WebKit's NVIDIA block found (switch off), the switch on, or the launcher turned
// it off after a forced start died
const NVIDIA_Q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("nvidia") : null;
const TURNED_OFF = "The launcher turned this off by itself: its start at 2026-11-22T21:04:10Z (Kiln Launcher 0.2.0-dev) with GPU drawing forced on NVIDIA ended before its page had started (2026-11-22T21:04:30Z)";
render = { ...render, force: NVIDIA_Q === "recovery" ? { on: true, step: "shm", why: "recovery", blocked: false } : NVIDIA_Q === "blocked" ? { on: false, step: "off", why: "off", blocked: true }
  : NVIDIA_Q === "on" ? { on: true, step: "shm", why: "on", blocked: false }
  : NVIDIA_Q === "failed" ? { on: false, step: "off", why: "off", blocked: true, failedWhy: TURNED_OFF }
  : { on: false, step: "off", why: "off", blocked: false } };
// WebKit's sandbox (sandbox.rs): a default start does not use it; `?sandbox=off` as after a message's "off" on a
// system where bwrap cannot start
const SANDBOX_UNUSED = "WebKitGTK's GTK 3 API, which the launcher uses, runs it only when WEBKIT_FORCE_SANDBOX=1 is set";
let sandbox: SandboxInfo = typeof location !== "undefined" && new URLSearchParams(location.search).get("sandbox") === "off"
  ? { want: "off", why: "marker", marker: true, markerWhy: "Turned off by the player in the launcher's message at the start: WebKit's sandbox could not start (2026-11-18T16:04:10Z)",
    webkit: "off", webkitWhy: "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 is set", probe: "fails", probeSays: "bwrap fails: bwrap: setting up uid map: Permission denied", probeFailed: true }
  : { want: "on", why: "default", marker: false, webkit: "unused", webkitWhy: SANDBOX_UNUSED, probe: "works", probeSays: "bwrap works", probeFailed: false };
// the AppImage's app menu entry (desktop.rs)
let desktop: DesktopInfo = { on: true, installed: true, foreign: false, file: "/home/player/.local/share/applications/kiln-launcher.desktop", integrator: null };

const server: ServerInstance = {
  id: "lab", name: "Lab server", kind: "local", dir: "/home/player/srv/enshrouded/lab", port: 26090, queryPort: 26091,
  wine: "ge:GE-Proton10-3", policy: "any", source: "kilnctl", remote: null,
};
const runningServer: ServerStatus = {
  id: server.id, state: "running", pid: 5120, uptimeS: 3600, lastStartS: 1, players: 0, kiln: "0.2.0-dev", installed: "0.2.0-dev+20261104.0f1ffa6",
  boot: { ...(bootExample as unknown as BootJson), side: "server" }, endpoint: endpointExample as unknown as AdminEndpoint,
};
/** One server the mock knows: its instance, its status, the status a start gives it (`running`), and the pids it uses. */
interface MockServer {
  inst: ServerInstance;
  status: ServerStatus;
  running: ServerStatus;
  /** Each server_start's number; a stop moves it on, so a start it interrupted fails as the real one does. */
  startSeq: number;
  maxPlayers: number;
  /** its running pid; a start gives it basePid + 1, a restart basePid + 2 */
  basePid: number;
  world: string;
}
const mockServers = new Map<string, MockServer>();
/** `?servers=3` (the servers-revamp story, mockup A): Ember Isles runs with 3 players and Kiln 0.2.0-dev.30.4, Hollow Lab
 *  is stopped with Kiln 0.2.0-dev.30.2 (an update: the runtime list gets 30.4 and 30.2), Frostwood runs on vps-01 over
 *  SSH. Without it: the one "lab" server the other tests know. */
const SERVERS3 = typeof location !== "undefined" && new URLSearchParams(location.search).get("servers") === "3";
const stopped = (s: ServerStatus): ServerStatus => ({ ...s, state: "stopped", pid: null, uptimeS: null, players: null, kiln: null, boot: null, endpoint: null });
/** What run::remote_status can tell over SSH: running when the admin endpoint answers (else unknown), the Kiln it
 *  names; never a pid, uptime, players, last start, installed Kiln or boot. */
const remoteStatus = (s: ServerStatus): ServerStatus => {
  const up = s.state === "running" || s.state === "stopping";
  return { ...s, state: up ? "running" : "unknown", pid: null, uptimeS: null, lastStartS: null, players: null, installed: null, boot: null,
    kiln: up ? s.kiln : null, endpoint: up ? s.endpoint : null };
};
/** `__kilnMock.namedStages(false)`: start tasks name no stage (a backend before Task.stage): Overview reads the lines. */
let namedStages = true;
const unnamed = (steps: TaskStep[]): TaskStep[] => (namedStages ? steps : steps.map((s) => (typeof s === "string" ? s : s.message)));
if (!SERVERS3) {
  mockServers.set(server.id, { inst: server, status: runningServer, running: runningServer, startSeq: 0, maxPlayers: 4, basePid: 5120, world: "Lab" });
} else {
  const part = (b: RuntimeBundle) => b.parts;
  bundles.unshift(
    { version: "0.2.0-dev.30.4", kiln: "0.2.0-dev.30.4", file: "runtime/kiln-runtime-0.2.0-dev.30.4.zip", sha256: hex("rt304"), created: "2026-11-22T02:30:00Z",
      sides: ["server", "client"], components: bundles[0].components, parts: part(bundles[0]) },
    { version: "0.2.0-dev.30.2", kiln: "0.2.0-dev.30.2", file: "runtime/kiln-runtime-0.2.0-dev.30.2.zip", sha256: hex("rt302"), created: "2026-11-21T01:10:00Z",
      sides: ["server", "client"], components: bundles[0].components, parts: part(bundles[0]) },
  );
  const local = (id: string, name: string, port: number): ServerInstance => ({
    id, name, kind: "local", dir: `/home/player/srv/enshrouded/${id}`, port, queryPort: port + 1, wine: "ge:GE-Proton10-3", policy: "verified-only", source: "kilnctl", remote: null, runner: "kilnctl",
  });
  const ember: ServerStatus = { ...runningServer, id: "ember-isles", pid: 6120, uptimeS: 8040, players: 3, kiln: "0.2.0-dev.30.4", installed: "0.2.0-dev.30.4" };
  mockServers.set("ember-isles", { inst: local("ember-isles", "Ember Isles", 15636), status: ember, running: ember, startSeq: 0, maxPlayers: 16, basePid: 6120, world: "Ember Isles" });
  const hollow: ServerStatus = { ...runningServer, id: "hollow-lab", pid: 6220, uptimeS: 0, players: 0, kiln: "0.2.0-dev.30.2", installed: "0.2.0-dev.30.2" };
  mockServers.set("hollow-lab", { inst: local("hollow-lab", "Hollow Lab", 15638), status: { ...stopped(hollow), installed: hollow.installed }, running: hollow, startSeq: 0, maxPlayers: 8, basePid: 6220, world: "Hollow" });
  // a remote server's status as run::remote_status gives it: running from its admin endpoint, no pid, uptime, players,
  // last start or folder here
  const frost: ServerStatus = remoteStatus({ ...runningServer, id: "frostwood", kiln: "0.2.0-dev.30.4" });
  mockServers.set("frostwood", {
    inst: { id: "frostwood", name: "Frostwood", kind: "remote", dir: null, port: 15636, queryPort: 15637, wine: null, policy: "verified-only", source: "remote",
      remote: { host: "vps-01", port: 22, dir: "/srv/enshrouded/frostwood", instance: "frostwood" }, runner: "remote" },
    status: frost, running: frost, startSeq: 0, maxPlayers: 8, basePid: 2210, world: "Frostwood",
  });
}
/** The server a command names (`serverId`; none: the lab server, as the mock always answered). */
function srvOf(a: Record<string, unknown>): MockServer {
  const id = a.serverId === undefined || a.serverId === null ? server.id : S(a.serverId);
  return mockServers.get(id) ?? reject("not_found", `no server '${id}'`);
}
function setServerStatus(rec: MockServer, status: ServerStatus) {
  if (rec.inst.kind === "remote") status = remoteStatus(status);
  rec.status = status;
  mockEmit("kiln://server", { serverId: rec.inst.id, status });
}
/** Tests (`__kilnMock.setServer`): the lab server stopped and with or without Kiln in its folder, or back to running. */
function mockSetServer(o: { running?: boolean; installed?: string | null }) {
  const rec = mockServers.get(server.id);
  if (!rec) return;
  const installed = o.installed === undefined ? runningServer.installed : o.installed;
  setServerStatus(rec, !(o.running ?? true)
    ? { ...stopped(runningServer), installed }
    : installed ? { ...runningServer, installed } : { ...runningServer, installed: null, kiln: null, boot: null, endpoint: null });
}

/** A start's lines with its named stages (Task.stage), as kiln_manage::server::run reports them; a remote start only
 *  prepares (`kilnctl start` over SSH) and runs. */
function startStages(id: string, pid: number, world: string, remote?: MockServer["inst"]["remote"]): TaskStep[] {
  if (remote) return unnamed([{ stage: "preparing", message: `ssh ${remote.host} kilnctl start ${remote.instance ?? id}` }, { stage: "running", message: `${id} started` }]);
  return unnamed([
    { stage: "preparing", message: "checking the server folder and Kiln's setup" },
    { stage: "wine", message: "GE-Proton10-3, prefix ready" },
    { stage: "process", message: `started GE-Proton10-3 ./enshrouded_server.exe (wine pid ${pid}), output in /home/player/.local/state/kiln/${id}.run.log` },
    { stage: "world", message: `[server] Loading world '${world}' (seed 1847)` },
    { stage: "world", message: "[ecs] LoadEcsScene to Load" },
    { stage: "world", message: "[streaming] 812 of 1490 chunks" },
    { stage: "running", message: "[ecs] LoadEcsScene to Run" },
  ]);
}

// ---------------------------------------------------------------- handlers

type Handler = (args: Record<string, unknown>) => unknown;
const S = (v: unknown) => String(v ?? "");

const handlers: Record<string, Handler> = {
  shroudlink_notice: (a) => {
    const p = a.profile as Profile;
    if (games.find((g) => g.id === a.gameId)?.kind !== "client" || !p.mods.shroudlink?.some((m) => m.enabled)) return null;
    const addons = library.filter((e) => e.kind === "native" && e.id === "shroudlink-addon").flatMap((e) => e.versions.map((v) => ({ kind: e.kind, id: e.id, version: v.version, hash: v.hash })));
    if (addons.some((a) => p.mods.native.some((m) => m.enabled && m.id === a.id && m.hash === a.hash && m.version === a.version))) return null;
    return { mods: p.mods.shroudlink.filter((m) => m.enabled).map((m) => m.id), addons };
  },
  snapshot_status: () => ({ phase: "ready", reasons: [] }),
  snapshot_continue: () => null,
  // app
  app_info: (): AppInfo => ({ version: "0.2.0-dev", kiln: "0.2.0-dev", platform: "linux", arch: "x86_64", dataDir: mockDataDir(), commit: "0f1ffa6", mock: true,
    noWebgl: render.mode === "software", distribution: MOCK_DISTRIBUTION, nexusEnabled: NEXUS_MOCK_ENABLED, render: { ...render }, desktop: { ...desktop }, sandbox: { ...sandbox },
    ...(MOCK_PREVIEW.length ? { preview: [...MOCK_PREVIEW] } : {}), ...(OWN_FILES.length ? { ownFilesMissing: [...OWN_FILES] } : {}) }),
  app_started: () => null,
  app_close_seen: () => null,
  app_close_unsaved: () => null,
  app_render_frame: (a) => a.painted && a.visible && NVIDIA_Q === "recovery" ? "The previous normal NVIDIA startup never confirmed a visible frame. Kiln is trying shared-memory GPU drawing." : null,
  app_render_report: () => null,
  app_render_set: (a) => {
    render = { ...render, marker: !!a.software, markerWhy: a.software ? `Turned on in Settings (${new Date().toISOString()})` : undefined };
    return { ...render };
  },
  app_render_x11_set: (a) => {
    if (render.x11) render = { ...render, x11: { ...render.x11, auto: !!a.on } };
    return { ...render };
  },
  app_render_force_set: (a) => {
    if (render.force) render = { ...render, force: { ...render.force, on: !!a.on, failedWhy: undefined } };
    return { ...render };
  },
  app_sandbox_set: (a) => {
    sandbox = { ...sandbox, marker: !a.on, markerWhy: a.on ? undefined : `Turned off in Settings (${new Date().toISOString()})` };
    return { ...sandbox };
  },
  // a plain browser has no dialogs; Downloads stands in (ui/dialogFolder.ts)
  app_dialog_default: (a) => (a.preferred ? S(a.preferred) : a.fileName ? `/home/player/Downloads/${S(a.fileName)}` : "/home/player/Downloads"),
  app_dialog_remember: () => null,
  app_desktop_set: (a) => (desktop = { ...desktop, on: !!a.on, installed: !!a.on, foreign: false }),
  app_locales: () => ({ dir: `${mockDataDir()}/locales`, files: [], errors: [] }),
  app_settings_get: () => settings,
  app_settings_set: (a) => (settings = { ...(a.settings as LauncherSettings) }),
  app_open_path: (a) => { openedPaths.push(S(a.path)); return null; },
  app_open_url: (a) => { if (!S(a.url).startsWith("https://")) reject("refused", "Only https links open."); openedUrls.push(S(a.url)); return null; },
  // the Discord app is "installed" with ?discordapp=1 (kiln_manage::community::open: the app link, else the web link)
  app_open_discord: (a) => {
    const url = S(a.url), fallback = a.fallback ? S(a.fallback) : url;
    if (!url.startsWith("https://") || !fallback.startsWith("https://")) reject("refused", "Only https links open.");
    const m = /^https:\/\/discord\.com\/channels\/(\d{1,24}(?:\/\d{1,24}){0,2})$/.exec(url);
    if (m && typeof location !== "undefined" && new URLSearchParams(location.search).has("discordapp")) { openedUrls.push(`discord://-/channels/${m[1]}`); return "app"; }
    openedUrls.push(fallback);
    return "web";
  },
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
  game_start_get: (a) => startSetup(gameOf(a.gameId)),
  game_start_set: (a) => {
    const g = gameOf(a.gameId);
    if (live(g.id)) reject("game_running", "Close the game first.");
    if (g.kind !== "client") reject("invalid_argument", "a dedicated server starts on the Servers page; its start is not set here");
    const set = (a.settings ?? {}) as StartSettings;
    for (const p of [set.runner, set.prefix]) if (p && !p.startsWith("/")) reject("invalid_argument", `the path must be absolute, not ${p}`);
    if (set.mode || set.runner || set.prefix) startSettings[g.id] = { ...set }; else delete startSettings[g.id];
    return g;
  },

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
    runtimeInstalls.push(b.version);
    // runtime_rollback's own task, as the backend names it (its kind and "Roll back to <version>")
    const back = a.rollback === true;
    return startTask(back ? "runtime_rollback" : "runtime_install", back ? `Roll back to ${b.version}` : `Install Kiln ${b.kiln}`, ["Staging", "Verifying hashes", "Classifying dinput8.dll", "Backing up", "Placing files", "Writing receipt"], () => {
      const st = runtime[gameId];
      if (typeof fail === "string") reject("io", fail);
      if (fail) reject(fail.code, fail.message, fail.detail);
      if (st?.proxy?.kind === "shroudforge" && !opts.switchFromShroudforge) reject("refused", "ShroudForge is installed. Switch from ShroudForge to replace it.");
      // like install::resolve_components: an explicit choice records the defaults it leaves out; none keeps the game's
      // parts and adds every new or newly default one not turned off
      const prev = st?.receipt ?? null;
      const components = opts.components ? selectParts(b, g.kind, opts.components) : resolveParts(b, g.kind, prev);
      const deselected = opts.components ? deselectedFor(b, g.kind, components, prev) : prev?.side === g.kind ? prev.runtime.deselected ?? [] : [];
      const receipt: InstallReceipt = { ...baseReceipt, side: g.kind, installed: st?.receipt?.installed ?? now(), updated: now(), runtime: { version: b.version, sha256: b.sha256, components, deselected } };
      // like the backend: Kiln files changed by hand are replaced, their copies kept in the install's backup; missing
      // ones are put back (there is nothing to keep)
      const changed = (st?.modified ?? []).filter((f) => !(st?.missingFiles ?? []).includes(f));
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
  runtime_rollback: (a) => handlers.runtime_install({ ...a, options: {}, rollback: true }),

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
    if (cur.created !== p.created) reject("conflict", `profile ${p.id} was deleted and made again meanwhile; this change was for the deleted one, reload it`);
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
      if (c.before && (c.before.version !== c.after.version || c.before.hash !== c.after.hash)) followKept(id, c.kind, c.after.id, c.after, c.before);
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
  library_take_over: (a) => mockTakeOver(a.entry as LibraryRef),
  library_rollback: (a) => mockRollback(a),
  library_remove: (a) => {
    if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading this mod.");
    const r = a.entry as LibraryRef;
    mockLibraryRemove(r, Array.isArray(a.shown) ? (a.shown as string[]) : !!a.force);
    // its settings go with its last version
    return a.settings && !library.some((l) => l.kind === r.kind && l.id === r.id) ? mockRemoveSettings(r.kind, r.id) : null;
  },
  library_remove_settings: (a) => {
    if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading the mod's settings.");
    return mockRemoveSettings(a.kind as ModKind, a.id as string);
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
  // engine_mods::own_folders / adopt: the player's own ShroudForge folders, and "Let Kiln manage this folder"
  engine_own_folders: (a) => {
    const pid = S(a.profileId), p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
    gameOf(S(a.gameId));
    if (activations[S(a.gameId)]?.profileId !== pid) return [];
    return (p.mods.shroudforge ?? []).flatMap((m) => {
      const own = ownFolders.get(m.id);
      return own ? [{ kind: "shroudforge", id: m.id, version: m.version, path: own.path, ...(own.renameTo ? { renameTo: own.renameTo } : {}), ...(own.refusal ? { refusal: own.refusal } : {}) }] : [];
    });
  },
  engine_adopt_folder: (a) => {
    const kind = a.kind as ModKind, id = S(a.id), pid = S(a.profileId), gid = S(a.gameId);
    const p = profiles.get(pid) ?? reject("not_found", `No profile '${pid}'.`);
    gameOf(gid);
    if (kind !== "shroudforge") reject("unsupported", `Kiln takes over ShroudForge folders only, not ${kind} folders`);
    const m = (p.mods.shroudforge ?? []).find((x) => x.id === id) ?? reject("not_found", `shroudforge mod ${id} is not in profile ${pid}`);
    if (running[gid]) reject("game_running", "Close the game first.");
    if (activations[gid]?.profileId !== pid) reject("conflict", "Load this profile in the selected game first: Kiln takes a folder over for the profile the game uses.");
    const own = ownFolders.get(id);
    if (!own) return { kind, id, version: m.version, path: `${gameOf(gid).path}/mods/${id}`, already: true };
    if (own.refusal) reject("refused", `${own.path}: ${own.refusal}`);
    ownFolders.delete(id);
    sfOwn.delete(id);
    return { kind, id, version: m.version, path: own.renameTo ?? own.path, ...(own.renameTo ? { renamedFrom: own.path } : {}) };
  },
  // kiln_manage::cook: what needs a cook, and the cook as a task whose lines are the tools'
  cook_plan: (a) => cookPlan(S(a.gameId)),
  cook_run: (a) => {
    const gid = S(a.gameId), one = a.modId ? S(a.modId) : null;
    gameOf(gid);
    let jobs = cookPlan(gid);
    if (one) {
      jobs = jobs.filter((j) => j.id === one).map((j) => ({ ...j, reason: "asked" as CookReason }));
      if (!jobs.length) reject("not_found", `${one} is not a mod of this game's profile with something to cook`);
    }
    const due = jobs.filter((j) => j.reason);
    const steps = due.flatMap((j, i) => [`Cooking ${j.name} (${i + 1} of ${due.length})`, `${j.name}: kiln: cooking assets/cook.json (modelcook)`]);
    return startTask("cook_run", one ? `Cook ${one}` : "Cook mods", steps.length ? steps : ["Nothing to cook"], () => {
      if (running[gid]) reject("game_running", "Close the game first: a cook writes the files the game is using.");
      const mods = due.map((j) => {
        const m = cookMods.get(j.id)!;
        const ok = !m.fails;
        m.last = { identity: j.identity, gameBuild: "steam:100", at: now(), ok, files: ok, ...(ok ? {} : { error: m.fails }) };
        m.reason = ok ? null : "lastFailed";
        return { id: j.id, name: j.name, reason: j.reason!, ok, ...(ok ? {} : { error: m.fails }) };
      });
      return { mods };
    }, cookStepMs);
  },
  // kiln_manage::boot_snapshot for a single start with another profile: the game data Play makes after its cook
  boot_snapshot_run: (a) => {
    const gid = S(a.gameId), pid = S(a.profileId);
    gameOf(gid);
    if (!profiles.has(pid)) reject("not_found", `no profile ${pid}`);
    const s = onceBootSnapshots.get(pid) ?? { status: "skipped" as const, emptyKinds: [], reason: "no mod needs the boot snapshot" };
    const steps = s.status === "skipped" ? ["no mod needs the boot snapshot"] : ["Game data for OnBoot mods (about 300 MB, once per game build): reading the game's archive"];
    return startTask("boot_snapshot_run", "Game data for OnBoot mods", steps, () => {
      if (running[gid]) reject("game_running", "Close the game first.");
      return s;
    }, cookStepMs);
  },
  engine_status: (a) => {
    const g = gameOf(S(a.gameId));
    const st = (k: ModKind, file: string, state: Json | null) => ({ kind: k, plugin: `kiln-${k}`, state, file: `${g.path}/kiln/state/${file}` });
    const rows = [
      st("emf", "emf.json", { mods: [{ id: "beastmaster", state: "loaded" }] }),
      st("eml", "eml.json", emlPatched
        ? { phase: "done", patched: 3, ms: 1263, mods: [{ id: "loot-tables-x", name: "Loot Tables X", state: "disabled", reason: "disabled in the profile" }] }
        : { phase: "idle", mods: [] }),
      // XHL-Core is external: only whether it is on the game's proxy/chain paths (engine_mods::xhl_host)
      { kind: "xhl", plugin: "", state: null, file: "", prerequisite: { available: false, text: XHL_NO_HOST } },
      // ShroudForge's own runtime report (shroudforge::effects): current while the fake game runs, stopped after
      { ...st("shroudforge", "shroudforge.json", sfSelfScript === "started" || sfSelfScript === "removed" ? { mode: "self-hosted", loaderFile: "dbghelp.dll" } : sfSelfScript === "before" ? null : { mode: "hosted", reason: null, version: "0.9.2", side: "client" }), ownLoader: sfSelfLoader ? { ...sfSelfLoader, kind: "shroudforge", via: "proxy", folder: "mods", delivery: "placed", enabled: 1, copied: [], notCopied: [], text: "ShroudForge runs itself" } : null, effects: running[g.id]
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
      st("dllmod", "dllmod.json", { status: "waiting", reason: "the mods start when a world of your own starts", abi: 1, steppedAside: false,
        mods: [{ id: "carryon", package: "carryon/carryon.dll", version: "0.1.50-dev", status: "waiting", reason: "" }] }),
    ];
    // ?sfiRow=<case>, kiln-sfi's row in place of ShroudForge's
    return sfiEngineRows(g.path, rows);
  },
  eml_data_latest: () => null,
  // ?restoreBase=recorded|unrecorded|unknown, as kiln_manage::restore_data::base_origin answers
  eml_data_base: () => sfiRestoreBase(),
  eml_data_plan: () => {
    if (!new URLSearchParams(location.search).has("restoreData")) return reject("refused", "No unproven recovery state.");
    return { fingerprint: "mock-restore", files: ["enshrouded_055.dat", "enshrouded.kfc.bak", "enshrouded.kfc_resources.bak"].map(from => ({ from, size: 4096, sha256: "mock" })) };
  },
  eml_data_apply: (a) => {
    if (!a.verified) return reject("refused", "Verify the game files in Steam first.");
    return { folder: "kiln/eml/displaced/restore-mock", files: ["enshrouded_055.dat", "enshrouded.kfc.bak", "enshrouded.kfc_resources.bak"].map(from => ({ from, size: 4096, sha256: "mock" })) };
  },
  eml_data_undo: () => ({ folder: "kiln/eml/displaced/restore-mock", files: [] }),
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
  config_list: (a) => {
    const target = a.target as ConfigTarget;
    const installed = mockInstalledIds(target);
    return [...configFolder(target).entries()].map(([file, f]): ConfigFileInfo =>
      ({ file, mod: f.mod, kind: f.kind, hasSchema: !!f.schema, modVersion: f.schema?.modVersion ?? null, modified: f.modified, orphaned: "profile" in target && mockOrphaned(installed, file, f.mod) }));
  },
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
  // kiln_manage::mod_settings::clear_orphans: only a profile's files, only orphans, all or nothing, a copy in the backups first
  config_clear_orphans: (a) => {
    const target = a.target as ConfigTarget;
    if (!("profile" in target)) reject("unsupported", "Only a profile's configs can be cleared here.");
    if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading the settings.");
    const folder = configFolder(target);
    const installed = mockInstalledIds(target);
    const files = [...new Set((a.files as string[]) ?? [])];
    for (const file of files) {
      const f = folder.get(file) ?? reject("not_found", `No config ${file} in this profile.`);
      if (!mockOrphaned(installed, file, f.mod)) reject("conflict", `${file} is not the settings of a mod that is gone: it stays.`);
    }
    if (!files.length) return { removed: [], backup: null, notes: [] } satisfies SettingsRemoval;
    for (const file of files) { folder.delete(file); configsCleared.push(file); }
    return {
      removed: files.map((file) => `/home/player/.local/share/kiln/profiles/${target.profile}/config/${file}`),
      backup: `/home/player/.local/share/kiln/backups/mod-settings/config-${target.profile}-20261128T120000Z`, notes: [],
    } satisfies SettingsRemoval;
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
  // Settings' ShroudForge card, as kiln_manage::sfi_backend answers
  sfi_backend_read: (a) => sfCard(S(a.gameId)),
  sfi_backend_preview: (a) => sfPreview(S(a.gameId), (a.backend ?? null) as SfBackend | null),
  sfi_backend_set: (a) => {
    const g = S(a.gameId);
    gameOf(g);
    if (live(g)) reject("game_running", "Close the game first.");
    const f = kilnFile(g);
    if (sfRevision(f.values) !== S(a.revision)) reject("conflict", "kiln.json changed since the ShroudForge card read it: nothing was written; look at the card again");
    const p = sfPreview(g, (a.backend ?? null) as SfBackend | null);
    if (p.refused) reject("refused", p.refused);
    const values = sfWritten(f.values, p.backend);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2) + "\n", modified: now() });
    return sfCard(g);
  },

  // launch
  launch_play: (a) => launch(S(a.gameId), a.request as LaunchRequest),
  // what a start is doing (mockProgress.ts): only a scripted start has a timeline; a plain one is a dev.26 runtime's
  launch_begin: (a) => {
    gameOf(S(a.gameId));
    refusePending(S(a.gameId));
    mockBegin(S(a.gameId), S(a.mode));
    // a start the backend tracks until the game's process is there (commands/launch.rs start_tracked; a Vanilla start
    // too since 0.2.0-dev.31.13)
    startsBegun.add(S(a.gameId));
    return null;
  },
  launch_progress: (a) => {
    const g = S(a.gameId);
    const t = mockProgress(g);
    // Steam did not load Kiln's proxy: no boot.json ever, so the backend's answer stays "waiting"
    if (t && noProxy && t.phase === "legacy") t.phase = "waiting";
    return t;
  },
  launch_end: (a) => { mockStartEnded(S(a.gameId)); startsBegun.delete(S(a.gameId)); return null; },
  // the check before Play
  launch_prepare: (a) => sfPrepare(S(a.gameId), a.request as LaunchRequest),
  launch_confirm: (a) => sfConfirm(S(a.gameId), S(a.token), a.request as LaunchRequest),
  launch_cancel: (a) => {
    const g = S(a.gameId), p = sfTokens.get(S(a.token));
    sfChecks.push(`cancel ${g}`);
    if (!p || p.gameId !== g) return false;
    sfTokens.delete(S(a.token));
    return true;
  },
  launch_state: (a): BootState => {
    const boot = bootFor(S(a.gameId));
    const r = live(S(a.gameId));
    // the example's run logged two errors after its boot (a service tick at the menu); a run started here has none
    const log = !boot ? null : r ? { errors: 0, first: [] } : { errors: 2, first: [
      "[E 12:01:20.749] [kiln] kiln service tick 'kiln creature brains' threw (1x): System.InvalidOperationException: wrong thread",
      "[E 12:01:22.073] [kiln] kiln service tick 'kiln creature brains' threw (2x): System.InvalidOperationException: wrong thread",
    ] };
    return { stoppedStart: stoppedStarts[S(a.gameId)] ?? null, boot, mods: boot ? (modsExample as unknown as ModsJson) : null, native: boot ? (nativeExample as unknown as NativeJson) : null, stale: !r && !!boot, log };
  },

  // what kiln-eml does before the game's window exists: only on request (tests: `__kilnMock.setPreStart`), so that a plain
  // start still says "Waiting for Kiln to boot."
  launch_prestart: (a): PreStart | null => (live(S(a.gameId)) ? preStart : null),

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
    reportSaves.push(Array.isArray(a.exclude) ? a.exclude.map(String) : []);
    const name = /\.zip$/i.test(S(a.path)) ? S(a.path) : `${S(a.path)}.zip`;
    const path = a.destination === "downloads" ? `/home/player/Downloads/${name.split(/[\\/]/).pop()}`
      : a.destination === "automatic" ? `/home/player/.local/state/kiln/${name.split(/[\\/]/).pop()}` : name;
    return { path, size: 48_213, findings: mockReportFindings, errors: [] } satisfies ReportSaved;
  },
  report_preview: (a) => mockReportContents(a.exclude),
  report_note: () => null,
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

  // modpack (the refusal banner on Play); servers: the server mock handlers below override these
  modpack_pending: () => pendingLastServer(),
  server_list: () => [...mockServers.values()].map((r) => r.inst),
  server_get: (a) => srvOf(a).inst,
  server_status: (a) => srvOf(a).status,
  server_deploy: (a) => {
    const rec = srvOf(a);
    const version = S(a.version);
    const b = bundles.find((x) => x.version === version) ?? reject("not_found", `No runtime bundle ${version}.`);
    if (rec.inst.kind === "remote") reject("unsupported", "A remote server has no folder on this PC.");
    if (rec.status.pid) reject("conflict", "Stop the server before installing Kiln.");
    return startTask("server_deploy", `Installing Kiln ${version} on ${rec.inst.id}`, ["Verifying the bundle", "Staging", "Placing files", "Writing receipt"], () => {
      rec.running = { ...rec.running, installed: b.version, kiln: b.kiln };
      setServerStatus(rec, { ...rec.status, installed: b.version });
      const receipt: InstallReceipt = { ...baseReceipt, side: "server", installed: now(), runtime: { version: b.version, sha256: b.sha256, components: resolveParts(b, "server", null), deselected: [] } };
      return { ...receipt, changedBackedUp: [], backup: null } satisfies InstallResult;
    });
  },
  // a start, stop, restart and game update with the real tasks' progress lines (kiln_manage::server::run)
  server_start: (a) => {
    const rec = srvOf(a);
    const id = rec.inst.id;
    if (rec.status.pid) reject("conflict", `${id} is running already (pid ${rec.status.pid})`);
    const mine = ++rec.startSeq;
    const pid = rec.basePid + 1;
    // the status says starting once the process is there (the real status reads the pid), not at the click
    setTimeout(() => {
      if (mine !== rec.startSeq || rec.status.pid) return;
      setServerStatus(rec, { ...rec.status, state: "starting", pid, uptimeS: 0 });
    }, 1500);
    const t0 = Date.now();
    return startTask("server_start", `Starting ${id}`, startStages(id, pid, rec.world, rec.inst.remote), () => {
      // stopped while it started: the process is gone before its world ran (run::wait_ready)
      if (mine !== rec.startSeq) reject("io", "the server exited before its world ran (exit code 0)");
      setServerStatus(rec, { ...rec.running, pid, uptimeS: 0, lastStartS: Math.max(1, Math.round((Date.now() - t0) / 1000)) });
      return rec.status;
    }, 700, () => {
      // a failed start: its process is gone (a later start or stop has moved on: that one's status stands)
      if (mine === rec.startSeq) { rec.startSeq++; setServerStatus(rec, stopped(rec.status)); }
    });
  },
  server_stop: (a) => {
    const rec = srvOf(a);
    rec.startSeq++;
    const p = rec.status.pid;
    setServerStatus(rec, { ...rec.status, state: "stopping" });
    return startTask("server_stop", `Stopping ${rec.inst.id}`, [`SIGINT -> pid ${p ?? "-"}; waiting up to 60 s for the save`, "waiting for wineserver"], () => {
      setServerStatus(rec, stopped(rec.running));
      return rec.status;
    });
  },
  server_restart: (a) => {
    const rec = srvOf(a);
    const t0 = Date.now();
    const remote = rec.inst.remote;
    return startTask("server_restart", `Restarting ${rec.inst.id}`, [
      ...unnamed(remote ? [{ stage: "stopping", message: `ssh ${remote.host} kilnctl stop ${remote.instance ?? rec.inst.id}` }] : [
        { stage: "stopping", message: `SIGINT -> pid ${rec.status.pid ?? rec.basePid}; waiting up to 60 s for the save` },
        { stage: "stopping", message: "waiting for wineserver" },
        { stage: "stopping", message: `${rec.inst.id} stopped, world saved` },
      ]),
      ...startStages(rec.inst.id, rec.basePid + 2, rec.world, remote),
    ], () => {
      setServerStatus(rec, { ...rec.running, pid: rec.basePid + 2, uptimeS: 0, lastStartS: Math.max(1, Math.round((Date.now() - t0) / 1000)) });
      return rec.status;
    }, 300);
  },
  server_update: (a) => {
    const rec = srvOf(a);
    if (rec.status.pid) reject("conflict", "Stop the server before updating it.");
    return startTask("server_update", `Updating ${rec.inst.id} (SteamCMD)`, ["Logging in to Steam anonymously", "Update state (0x61) downloading, progress: 42.10", "Success! App '2278520' fully installed."], () => ({ updated: true }));
  },
  // the new-server wizard's install (kiln_manage::server::instance::create): the instance file first (the server is in
  // the list from then on, also when a later step fails), the reflink copy, SteamCMD, the config and the Wine prefix
  server_create: (a) => {
    const spec = a.spec as ServerSpec;
    if (!/^[a-z0-9][a-z0-9_-]{0,47}$/.test(spec.id)) reject("invalid_argument", `'${spec.id}' is not a valid server name`);
    if (!spec.name?.trim() || new TextEncoder().encode(spec.name).length > 64) reject("invalid_argument", "server name: 1 to 64 characters");
    if (spec.port < 1024) reject("invalid_argument", `port ${spec.port} is below 1024`);
    if (!spec.dir.startsWith("/")) reject("invalid_argument", `${spec.dir} must be an absolute path`);
    const half = mockServers.get(spec.id);
    const resume = !!(spec as ServerSpec & { resume?: boolean }).resume;
    // Try again after a failed create: the half-made server in the same folder is finished (instance::resumable)
    if (half && !(resume && half.inst.dir === spec.dir && half.inst.runner === "kilnctl" && !half.status.pid))
      reject("already_exists", resume && half.inst.dir !== spec.dir ? `a server '${spec.id}' exists in ${half.inst.dir}; trying again finishes one in the same folder only`
        : `a server '${spec.id}' exists`);
    const inst: ServerInstance = { id: spec.id, name: spec.name.trim(), kind: "local", dir: spec.dir, port: spec.port, queryPort: spec.port + 1, wine: "ge:GE-Proton10-3",
      policy: spec.policy ?? "verified-only", source: "kilnctl", remote: null, runner: "kilnctl" };
    const running: ServerStatus = { ...runningServer, id: spec.id, pid: null, uptimeS: 0, players: 0, kiln: null, installed: null };
    const basePid = 7000 + mockServers.size * 100;
    if (!half) mockServers.set(spec.id, { inst, status: { ...stopped(running), installed: null }, running: { ...running, pid: basePid }, startSeq: 0, maxPlayers: 16, basePid, world: inst.name });
    return startTask("server_create", `Creating server ${inst.name}`, [
      `instance file /home/player/.config/kiln/instances/${spec.id}.toml${half ? " (kept from the first try)" : ""}`,
      ...(spec.reflinkFrom ? [`copying the game files from ${spec.reflinkFrom} (reflink)`] : []),
      ...(spec.skipSteamcmd ? [] : ["steamcmd: Logging in to Steam anonymously", "steamcmd: Update state (0x61) downloading, progress: 38.20 (1205 / 3155 MB)",
        "steamcmd: Update state (0x81) verifying update, progress: 61.24 (1932 / 3155 MB)", "steamcmd: Success! App '2278520' fully installed."]),
      "writing enshrouded_server.json", "creating the Wine prefix (wineboot)",
    ], () => inst, 500);
  },
  server_remove: (a) => {
    const rec = srvOf(a);
    if (rec.status.pid) reject("conflict", `${rec.inst.id} is running: stop it first`);
    mockServers.delete(rec.inst.id);
    return null;
  },
  server_backup_create: (a) => {
    const rec = srvOf(a);
    const name = `${rec.inst.id}-savegame-${now().replace(/[-:]/g, "").slice(0, 15)}Z.tar.gz`;
    return startTask("server_backup_create", `Backing up ${rec.inst.id}`, [
      rec.status.pid ? "asking Kiln.Admin for a save" : `tar -czf ${name} savegame`, "12 files, 412 KB",
    ], () => ({ name, created: now(), bytes: 412_000, source: "kilnctl" }));
  },
  admin_sessions: () => [],
  ...serverMockHandlers,
  // the server's own name and slots (?servers=3: each server's)
  server_query: (a) => {
    const rec = srvOf(a);
    return { name: rec.inst.name, players: rec.status.players ?? 0, maxPlayers: rec.maxPlayers, version: "0.0.15.0", pingMs: 2 };
  },
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
// the update's restart waits while a game starts (launch_begin and no process yet, a scripted start before its process
// exists), not while one runs (selfupdate.rs restart_refusal)
/** commands/launch.rs start_tracked as the mock has it: a start launch_begin began (any mode: the backend tracks a
 *  Vanilla start too since 0.2.0-dev.31.13) whose game process has not come yet, or a scripted start before its process. */
export function mockStartTracked(): boolean {
  for (const g of [...startsBegun]) if (live(g)) startsBegun.delete(g);
  return startsBegun.size > 0 || Object.keys(running).some((g) => !!running[g] && !live(g));
}
Object.assign(handlers, updateMockHandlers(mockEmit, MOCK_DISTRIBUTION, () => settings.updateChannel, () =>
  mockStartTracked() ? { code: "busy", message: "The game is starting: restart into the update once it runs, or once the start is over." } : null));
Object.assign(handlers, deckMockHandlers(handlers));
// the Discord features: the relay, Discord and the Discord app, faked (./mockCommunity.ts, `?discord=` flags)
Object.assign(handlers, communityMockHandlers({ emit: mockEmit, gameRunning: () => Object.keys(running).some((g) => !!live(g)) }));
Object.assign(handlers, nexusMockHandlers(handlers, mockEmit, startTask, quickAdd));
Object.assign(handlers, updatesMockHandlers(handlers, mockEmit));
Object.assign(handlers, vendorMockHandlers(startTask, (id) => gameOf(id).kind));
Object.assign(handlers, chainMockHandlers({ profiles, library, reject }));
Object.assign(handlers, dataFolderMockHandlers(startTask, (id) => tasks.get(id)));
// Crucible packs (./mockCrucible.ts, `?crucible=` flags): a fake Crucible, an instance with an update waiting
Object.assign(handlers, crucibleMockHandlers({ handlers, emit: mockEmit, startTask, profiles, library, activations, activate, importPaths, makeBackup, slug,
  isRunning: (g) => !!live(g), emitGame: (g) => emitGame(g, "status"), seeded: !FRESH }));
// Pack Studio (./mockStudio.ts, `?studio=` flags): a pack source "Ember run" from the profile of that name
Object.assign(handlers, studioMockHandlers({ handlers, startTask, profiles, library, slug, activate, seeded: !FRESH }));
// the save editor: synthetic character saves in memory, or a local fixture with ?editor=fixture (mockEditor.ts). Loaded
// at the first editor command: its saves bring hash and buffer code no other page of the mock needs, and loading that
// with the mock made every first page of a cold dev server slower
let editorMock: Promise<Record<string, Handler>> | null = null;
for (const cmd of ["editor_list_saves", "editor_file_info", "editor_read_file", "editor_zstd_decompress", "editor_game_running", "editor_backup_file",
  "editor_write_save", "editor_list_backups", "editor_restore_backup", "editor_open_backups", "editor_storage_read", "editor_return_storage"]) {
  handlers[cmd] = async (a) => (await (editorMock ??= import("./mockEditor").then((m) => m.editorMockHandlers(reject, () => Object.values(running).some(Boolean)))))[cmd](a);
}

/** The folder dialog's stand-in in a plain browser (shell/dataFolder.ts pickFolder; tests queue answers). */
export { mockPickFolder };

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
/** What a player changed in a Library folder (tests: `__kilnMock.editInLibrary`): the version reads as changed, and
 *  what its manifest says now waits for library_take_over. */
const edited = new Map<string, { name?: string; version?: string }>();
const editKey = (kind: ModKind, id: string, hash: string) => `${kind}/${id}/${hash}`;

/** A Library version whose files were changed (library::changed_error): Play refuses it, naming it, with the entry to take over. */
function rejectChanged(p: Profile) {
  for (const kind of MOD_KINDS) {
    for (const m of p.mods[kind] ?? []) {
      const e = library.find((l) => l.kind === kind && l.id === m.id);
      const v = e?.versions.find((x) => x.hash === m.hash);
      if (!m.enabled || !e || !v?.changed) continue;
      reject("hash_mismatch", `the active profile changed and cannot be applied: ${e.name} was changed in its folder (${v.folder}): its files are not the ones Kiln imported, so Kiln does not load it as it is. Use the changed files (Kiln takes them over as the mod's new files), or put the old files back.`,
        { libraryChanged: { kind, id: m.id, version: m.version, hash: m.hash }, name: e.name, folder: v.folder });
    }
  }
}

/** library_take_over (kiln_manage::library::take_over): the changed files become the mod's entry; profiles follow. */
function mockTakeOver(r: LibraryRef): TakenOver {
  if (Object.values(running).some(Boolean)) reject("game_running", "Close Enshrouded first: the game may be reading this mod.");
  const e = library.find((l) => l.kind === r.kind && l.id === r.id) ?? reject("not_found", `${r.kind} ${r.id} ${r.version} is not in the library`);
  const i = e.versions.findIndex((v) => v.hash === r.hash);
  if (i < 0) reject("not_found", `${r.kind} ${r.id} ${r.version} is not in the library`);
  const v = e.versions[i];
  if (!v.changed) return { from: r, to: r, name: e.name };
  const change = edited.get(editKey(r.kind, r.id, r.hash)) ?? {};
  edited.delete(editKey(r.kind, r.id, r.hash));
  const version = change.version ?? v.version;
  const hash = kdh(`${r.kind}${r.id}${version}edited${v.hash}`);
  const manifest = { ...(v.manifest as Record<string, Json>), version, ...(change.name ? { name: change.name } : {}) } as Json;
  e.versions[i] = { ...v, version, hash, folder: `library/${r.kind}/${r.id}/${version}+${hash.slice(5, 13)}`, imported: now(), manifest, changed: undefined };
  if (change.name) e.name = change.name;
  const to: LibraryRef = { kind: r.kind, id: r.id, version, hash };
  for (const [pid, p] of profiles) {
    const list = p.mods[r.kind] ?? [];
    if (!list.some((m) => m.id === r.id && m.hash === r.hash)) continue;
    profiles.set(pid, { ...p, mods: { ...p.mods, [r.kind]: list.map((m) => (m.id === r.id && m.hash === r.hash ? { ...m, version, hash } : m)) },
      generation: p.generation + 1, updated: now() });
    reapplyWhereActive(pid);
  }
  return { from: r, to, name: e.name };
}

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
  const loaders = [sfSelfLoader, emlRLoader].filter((loader) => loader !== null);
  return { gameId, profileId: p.id, kilnInstalled: kiln, mods, loaders, unreadable: [], truncated: false };
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
  // (0.2.0-dev.31.13) the starts the backend follows (launch_begin, Steam asked) survive the reload too
  persistStarts(sessionStorage.getItem(`${STATE_KEY}:starts`), (text) => sessionStorage.setItem(`${STATE_KEY}:starts`, text));
}

const loaderParams = new URLSearchParams(typeof location === "undefined" ? "" : location.search);
const sfSelfScript = loaderParams.get("sfSelf");
const sfSelfLoader = sfSelfScript === "before" || sfSelfScript === "started" || sfSelfScript === "empty" || sfSelfScript === "takeover"
  ? { engine: "shroudforge" as const, name: "ShroudForge", kinds: ["shroudforge" as const], file: sfSelfScript === "takeover" ? "dinput8.dll" : "dbghelp.dll" } : null;
if (sfSelfScript === "empty") for (const p of profiles.values()) p.mods.shroudforge = [];
const emlRLoader = loaderParams.get("emlR") === "present"
  ? { engine: "eml-r", name: "Enshrouded Mod Loader - Revamped", kinds: [], file: "dbghelp.dll" } : null;

export async function mockInvoke<T>(cmd: string, args: Record<string, unknown>): Promise<T> {
  commandCalls.push(cmd);
  const plan = commandPlans[cmd]?.shift();
  if (plan?.delay) await new Promise((r) => setTimeout(r, plan.delay));
  if (plan?.gate) { const g = gateOf(plan.gate); if (!g.open) await new Promise<void>((r) => g.waiters.push(r)); }
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
if (typeof window !== "undefined") (window as unknown as { __kilnMock?: unknown }).__kilnMock = { community: communityMockHooks(mockEmit), crash: mockCrash, scriptStoppedStart, externalStart: mockExternalStart, stopGame: mockStopGame, emit: mockEmit, setLastServer: mockSetLastServer,
  calls: () => [...commandCalls],
  /** "Open web panel": Kiln Web Admin on the mock server, the gateways (src/pages/servers/mockServers.ts). */
  webPanel: webPanelMockHooks,
  // Settings' ShroudForge card
  /** Why Kiln's interpreter is held (null: this mock build does not hold it). */
  setSfHeld: (why: string | null) => { sfHeld = why; },
  /** What a selected kiln meets in the game folder. */
  setSfRoute: (route: NonNullable<SfBackendCard["interpreterRoute"]>) => { sfRoute = route; },
  /** kiln/state/launch.json of the last start (null: none). */
  setSfLast: (last: SfBackendCard["last"] | null, gameId = "client-steam") => { if (last) sfLast[gameId] = last; else delete sfLast[gameId]; },
  /** A second client install (as Settings' "Add folder" adds one), Kiln in it: its id. */
  addSfGame: (path: string) => {
    const g: GameInstall = { ...steamGame, id: `client-${hex(path).slice(0, 8)}`, path, library: null, source: "manual", buildId: null, steamFlavor: null };
    if (!games.some((x) => x.id === g.id)) games.push(g);
    runtime[g.id] ??= { ...runtime["client-steam"] };
    return g.id;
  },
  // the check before Play
  /** What the next start of `gameId` warns about (empty: no dialog); advisories are shown, never acknowledged. */
  setStartWarnings: (warnings: StartWarning[], advisories: StartWarning[] = [], gameId = "client-steam") => { sfStart[gameId] = { warnings, advisories }; },
  /** Each prepare, confirm and cancel so far ("prepare client-steam profile"). */
  startChecks: () => [...sfChecks],
  /** How many prepared starts wait for their confirm. */
  startTokens: () => sfTokens.size,
  /** Whether the game runs (a start was opened). */
  gameRunning: (gameId = "client-steam") => !!live(gameId),
  /** Each report_save's `exclude`, in order. */
  reportSaves: () => reportSaves.map((x) => [...x]),
  openedPaths: () => [...openedPaths],
  settingsRemoved: () => [...settingsRemoved],
  configsCleared: () => [...configsCleared],
  /** One more config file in a target's folder (tests: a long Configs list); `mod`: the mod its snapshot names. */
  addConfig: (target: ConfigTarget, file: string, mod: string | null) => {
    configFolder(target).set(file, { text: "{}", values: {}, schema: null, mod, kind: file.startsWith("emf/") ? "emf" : "kiln", modified: "2026-11-04T09:00:00Z" });
  },
  planCommand: (cmd: string, plan: CommandPlan) => { (commandPlans[cmd] ??= []).push(plan); },
  openGate: (name: string) => { const g = gateOf(name); g.open = true; g.waiters.splice(0).forEach((w) => w()); },
  planActivation: (report: Pick<Activation, "retained" | "keptFiles" | "warnings">) => { activationPlan = report; },
  /** "Open web panel": Kilnwarden and/or Kiln Web Admin on the lab server (src/pages/servers/mockServers.ts). */
  webPanelMods: webPanelModMockHooks,
  planAddNotices: (lines: string[]) => { addNotices.push(...lines); },
  setSfOwnFolder: (id: string, own = true) => { if (own) sfOwn.add(id); else sfOwn.delete(id); },
  setCookPace: (ms: number) => { cookStepMs = ms; },
  /** Tasks of `kind` started from now on wait before their last step until `openGate(gate)`; with `fail`, they then
   *  fail with that message. null clears it. */
  holdTasks: (kind: string, hold: { gate: string; fail?: string } | null) => { if (hold) taskHolds.set(kind, hold); else taskHolds.delete(kind); },
  /** Start and restart tasks from now on name their stages (Task.stage), or not (`false`: a backend before stages). */
  namedStages: (on: boolean) => { namedStages = on; },
  setOnceBootSnapshot: (profileId: string, s: BootSnapshot | null) => { if (s) onceBootSnapshots.set(profileId, s); else onceBootSnapshots.delete(profileId); },
  setCook: (id: string, cook: { reason?: CookReason | null; fails?: string | null } | null) => {
    if (!cook) { cookMods.delete(id); return; }
    const m = cookMods.get(id) ?? { reason: null };
    if ("reason" in cook) m.reason = cook.reason ?? null;
    if ("fails" in cook) m.fails = cook.fails ?? undefined;
    cookMods.set(id, m);
  },
  setOwnFolder: (id: string, own: { path: string; renameTo?: string; refusal?: string } | null) => {
    if (own) { ownFolders.set(id, own); sfOwn.add(id); } else { ownFolders.delete(id); sfOwn.delete(id); }
  },
  setGameFileUsers: (users: Record<string, string[]>) => { gameFileUsers = users; },
  failNextInstall: (failure: string | KilnError) => { failNextInstall = failure; },
  /** The player changes a mod's files in its Library folder ("Open folder", mod.json edited): its newest version. */
  editInLibrary: (kind: ModKind, id: string, change: { name?: string; version?: string }) => {
    const e = library.find((l) => l.kind === kind && l.id === id);
    if (!e) throw new Error(`${kind} ${id} is not in the mock library`);
    e.versions[0] = { ...e.versions[0], changed: true };
    edited.set(editKey(kind, id, e.versions[0].hash), change);
  },
  setModified: (files: string[], gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], modified: files }; emitGame(gameId, "status"); },
  /** Receipt files gone from the game folder (an antivirus took them): missing, and changed as the backend counts them
   *  (install::status). An install puts them back. */
  setMissing: (files: string[], gameId = "client-steam") => {
    const st = runtime[gameId];
    if (st) runtime[gameId] = { ...st, modified: [...new Set([...st.modified, ...files])], missingFiles: files };
    emitGame(gameId, "status");
  },
  startOutside: mockStartOutside, setServer: mockSetServer, opened: () => [...openedUrls],
  setPreStart: (p: PreStart | null) => { preStart = p; },
  /** The next Play plays the owner's start of 13 Nov (mockProgress.ts), compressed and changed by `o`; null: off. */
  scriptStart: (o: Partial<StartScript> | null) => setStartScript(o ? ownerScript(o) : null),
  /** The running game's Kiln writes its first progress.json now (after a slow chained DLL): the watcher's event. */
  speak: (gameId = "client-steam") => { mockSpeak(gameId); mockEmit("kiln://game", { gameId, kind: "progress", pid: running[gameId]?.pid ?? null, status: null }); },
  /** Forgets the earlier starts: the next one is a first start. */
  clearStartHistory: (gameId = "client-steam") => clearStartHistory(gameId),
  setNoProxy: (on: boolean, gameId = "client-steam") => { noProxy = on; emitGame(gameId, "status"); },
  setOtherFolderProfile: (o: { id: string; root?: string | null } | null, gameId = "client-steam") => { otherFolder[gameId] = o; emitGame(gameId, "status"); },
  setNewerMods: (mods: NewerKilnMod[], gameId = "client-steam") => { newerMods[gameId] = mods; emitGame(gameId, "status"); },
  setGraphicsTools: (tools: GraphicsToolNote[], gameId = "client-steam") => { graphicsTools[gameId] = tools; emitGame(gameId, "status"); },
  setGraphicsStepAside: (tools: GraphicsStepAside[], gameId = "client-steam") => { graphicsStepAside[gameId] = tools; emitGame(gameId, "status"); },
  /** (dev.29.18) What the game's Kiln wrote to kiln/state/graphics.json (null: none); "Now" while the game runs. */
  setGraphicsState: (state: GraphicsStateFile | null, gameId = "client-steam") => {
    if (state) graphicsState[gameId] = state;
    else delete graphicsState[gameId];
    emitGame(gameId, "status");
  },
  /** The game's kiln.json values as the launcher last wrote them (tests read what Settings saved). */
  kilnJsonValues: (gameId = "client-steam") => clone(kilnFile(gameId).values),
  /** A hand edit of the game's kiln.json (a key the launcher's Settings do not list, such as `mods.hosted`). */
  setKilnJson: (path: string, value: Json, gameId = "client-steam") => {
    const f = kilnFile(gameId);
    const values = setPath(f.values, path, value);
    Object.assign(f, { values, text: JSON.stringify(values, null, 2) + "\n", modified: now() });
  },
  setSteamUpdating: (on: boolean, gameId = "client-steam") => { steamUpdating[gameId] = on; emitGame(gameId, "status"); },
  setUpgradeAvailable: (version: string | null, gameId = "client-steam") => { if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], upgradeAvailable: version }; emitGame(gameId, "status"); },
  /** (dev.29) The game's Kiln came with a test build and the launcher ships the older `to` (InstallState.switchBack);
   *  null clears it. An install (runtime_install) clears it too, as the backend's next status does. */
  setSwitchBack: (from: string | null, to: string | null, gameId = "client-steam") => {
    if (runtime[gameId]) runtime[gameId] = { ...runtime[gameId], switchBack: from && to ? { from, to } : null };
    emitGame(gameId, "status");
  },
  /** The runtime versions runtime_install put in, in order. */
  installs: () => [...runtimeInstalls] };

// Deterministic browser fixture for the missing Shroudlink client addon.
const slNotice = typeof location === "undefined" ? null : new URLSearchParams(location.search).get("slNotice");
if (slNotice) {
  for (const p of profiles.values()) {
    p.mods.shroudlink = slNotice === "empty" ? [] : [{ id: "assistants", version: "1", hash: "sl", enabled: slNotice !== "off" }];
    p.mods.native = [];
  }
  if (slNotice !== "missing") library.push({ kind: "native", id: "shroudlink-addon", name: "Shroudlink client addon", versions: [{ version: "1", hash: "sl-addon", folder: "/mock/addon", imported: "2026-11-15", side: "client", clients: "none", size: 1, source: null, manifestId: null, manifest: null }] });
  if (slNotice === "server") for (const g of games) g.kind = "server";
}

// `?problem=crash` (the Discord work's demo): the game runs for four minutes, then closes with a crash report, so Play
// and Deck Home show the crash notice with its "Send to Kiln support". `?problem=nokiln`: the next Play starts the
// game without Kiln ("Kiln is not loading").
if (typeof location !== "undefined") {
  const problem = new URLSearchParams(location.search).get("problem");
  // the game starts once the launcher follows the game's events (a start it did not see gets no notice: on a busy
  // machine the window can take longer than a fixed delay to listen)
  const play = () => { mockExternalStart("client-steam", Date.now() - 4 * 60_000); setTimeout(() => mockCrash(), 900); };
  const whenListening = () => (listeners.get("kiln://game")?.size ? setTimeout(play, 600) : setTimeout(whenListening, 100));
  if (problem === "crash") setTimeout(whenListening, 100);
  if (problem === "nokiln") noProxy = true;
}
