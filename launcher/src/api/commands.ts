// Typed wrappers for every Tauri command of the Kiln Launcher.
// Contract: docs/kiln/launcher/contracts.md sections 9-11. Command names and argument shapes are stable; change them
// together with the contract. Pages and stores import from here, never call `invoke` directly.
// Types mirror crates/kiln-manage (serde camelCase) and launcher/src-tauri/src/commands/*.rs.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { listen as tauriListen } from "@tauri-apps/api/event";
import { canFallBack, noteFallback } from "./fallback";
import { mockInvoke, mockListen } from "./mock";

// ---------------------------------------------------------------- plumbing

/** True inside the Tauri webview; false in a plain browser (the mock layer answers then). */
export const isTauri = (): boolean => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

export type ErrorCode =
  | "not_implemented" | "invalid_argument" | "not_found" | "already_exists" | "io" | "parse" | "schema" | "conflict"
  | "game_running" | "steam_updating" | "permission" | "unsupported" | "network" | "hash_mismatch"
  | "consent_required" | "refused" | "timeout" | "canceled" | "busy" | "auth_failed" | "admin_unavailable"
  | "op_failed" | "internal";

/** What every command rejects with. */
export interface KilnError {
  code: ErrorCode;
  message: string;
  detail?: unknown;
}

export function isKilnError(e: unknown): e is KilnError {
  return typeof e === "object" && e !== null && "code" in e && "message" in e;
}

/** Any rejection as a KilnError (Tauri passes the serialized error through; anything else becomes "internal"). */
export function toKilnError(e: unknown): KilnError {
  if (isKilnError(e)) return e;
  return { code: "internal", message: e instanceof Error ? e.message : String(e) };
}

async function call<T>(cmd: string, args: Record<string, unknown> = {}): Promise<T> {
  try {
    return isTauri() ? await tauriInvoke<T>(cmd, args) : await mockInvoke<T>(cmd, args);
  } catch (e) {
    const err = toKilnError(e);
    // Development aid (./fallback.ts): read-only commands the crate does not implement yet answer from the mock.
    if (isTauri() && err.code === "not_implemented" && canFallBack(cmd)) {
      noteFallback(cmd);
      return mockInvoke<T>(cmd, args).catch((m) => { throw toKilnError(m); });
    }
    throw err;
  }
}

// ---------------------------------------------------------------- shared types

export type GameId = string;
export type ProfileId = string;
export type ServerId = string;
export type TaskId = string;
/** kiln_manage::manifest::ModKind: Kiln's own four, then other engines' mods (eml ... shroudtopia run by Kiln's engine
 *  plugins), then the reserved kinds (no importer yet). Labels and helpers: ./kinds.ts. */
export type ModKind = "kiln" | "native" | "plugin" | "emf"
  | "eml" | "shroudforge" | "set" | "greeneye" | "flame" | "shroudtopia"
  | "enshwick" | "shroudlink" | "ember";
export type Side = "server" | "client" | "both";
export type RunSide = "server" | "client";
export type Clients = "none" | "kiln" | "mod";
export type LogTarget = { game: GameId } | { server: ServerId };
export type ConfigTarget = { profile: ProfileId } | { server: ServerId } | { game: GameId };
export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

// app
export interface AppInfo {
  version: string;
  kiln: string;
  platform: "linux" | "windows" | "macos";
  arch: string;
  dataDir: string;
  commit: string | null;
  mock: boolean;
  /** KILN_LAUNCHER_NO_WEBGL=1 for this start: the plain Play button, no WebGL fire. */
  noWebgl?: boolean;
  /** Steam Deck / SteamOS / gamescope detection and `--deck` / `--desktop` (src/deck/mode.tsx chooses the UI). */
  deck?: DeckInfo;
  /** "direct" (frostdev.io, GitHub: updates itself) or "nexus" (Nexus Mods: no self-update; the UI shows no update
   *  checks, notices or buttons, only a link to the Kiln page there). Absent (an older backend): direct. */
  distribution?: Distribution;
}
/** Where this launcher build comes from (src-tauri/src/distribution.rs, api/distribution.ts). */
export type Distribution = "direct" | "nexus";
export interface LauncherSettings {
  steamRoots: string[];
  defaultGameId: GameId | null;
  allowHttpModpacks: boolean;
  savesKeep: number;
  /** Check for launcher updates at the start and every 6 hours (default on). */
  updateCheck: boolean;
  /** Absent or null: the channel of the running build (a dev build follows dev). */
  updateChannel?: UpdateChannel | null;
  /** The WebGL fire on Play (default on); off draws the plain Play button. */
  animatedFire?: boolean;
  /** A dropped mod goes into the active profile (library_add; default on); off: it is only imported. */
  addToActiveProfile?: boolean;
  /** The UI to start in; "auto" or absent: the Deck UI on a Steam Deck, SteamOS or gamescope (docs/kiln/launcher/deck.md). */
  uiMode?: UiMode | null;
}
export type TaskState = "queued" | "running" | "done" | "failed" | "canceled";
export interface Task<R = unknown> {
  id: TaskId;
  kind: string;
  title: string;
  state: TaskState;
  progress: number | null;
  message: string | null;
  started: string;
  finished: string | null;
  result?: R;
  error?: KilnError;
}

// game
export type GameKind = "client" | "server";
export type Platform = "proton" | "wine" | "windows";
export type PathMode = "z" | "native" | "copy";
export type SteamFlavor = "native" | "flatpak" | "snap" | "windows";
export interface GameInstall {
  id: GameId;
  kind: GameKind;
  path: string;
  exe: string;
  appId: number;
  library: string | null;
  platform: Platform;
  prefix: string | null;
  buildId: string | null;
  exeSha256: string | null;
  steamFlavor: SteamFlavor | null;
  pathMode: PathMode;
  source: "steam" | "manual" | "kilnctl";
}
export interface LaunchOptionsInfo {
  value: string | null;
  hasDllOverride: boolean;
  advice: string | null;
}
export interface GameStatus {
  id: GameId;
  running: boolean;
  pid: number | null;
  /** When the running game's process started (RFC 3339 UTC), however it was started; null when stopped or unknown. */
  started?: string | null;
  steamUpdating: boolean;
  runtime: InstallState;
  activeProfile: ProfileId | null;
  playOncePending: boolean;
  boot: BootJson | null;
  launchOptions: LaunchOptionsInfo | null;
  /** Kiln mods of the active profile built against a newer Kiln than the game has: they may not load until Kiln is updated. */
  newerMods?: NewerKilnMod[];
}
export interface NewerKilnMod {
  id: string;
  version: string;
  /** The Kiln build it compiled against ("0.2.0-dev+20261106.0f0a3f6"). */
  builtWith: string;
}

// runtime
export interface RuntimeBundle {
  version: string;
  kiln: string;
  file: string;
  sha256: string;
  created: string | null;
  sides: RunSide[];
  components: string[];
}
export type ProxyKind = "none" | "kiln" | "shroudforge" | "emf" | "unknown" | "notdll"
  // other engines' loaders and tools under a proxy name (kiln_manage::engines)
  | "eml" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame" | "shroudlink" | "ember" | "modloaderproxy"
  | "ultimateasiloader" | "aldrich" | "livemap" | "sharedxp" | "reshade" | "chainloader";
export interface ProxyClass {
  kind: ProxyKind;
  version: string | null;
  abi: number | null;
  sha256: string | null;
  size: number | null;
  detail: string | null;
}
export interface ReceiptFile {
  path: string;
  sha256: string;
  size?: number;
  component?: string;
  config?: boolean;
}
export interface ReplacedFile {
  path: string;
  sha256: string;
  size?: number;
  class?: "kiln" | "shroudforge" | "emf" | "unknown" | "file";
  action: "chained" | "backed-up" | "replaced" | "adopted";
  chainPath?: string;
  backup?: string;
  /** The engine it belongs to, when Kiln knows it. */
  engine?: ProxyKind;
}
export interface InstallReceipt {
  format: number;
  installId: string;
  installed: string;
  updated?: string;
  installer?: string;
  runtime: { version: string; sha256?: string; components: string[] };
  side: RunSide;
  platform?: Platform;
  game?: { exe?: string; sha256?: string; steamBuild?: string };
  files: ReceiptFile[];
  replaced: ReplacedFile[];
}
/** The result of runtime_install, runtime_rollback and server_deploy: the receipt, plus what the install backed up. */
export interface InstallResult extends InstallReceipt {
  /** Kiln files changed by hand that the install replaced; the changed copies are in `backup`. */
  changedBackedUp?: string[];
  /** The install's backup folder in the launcher's data dir (`backups/install/<installId>/install-<ts>`), or null. */
  backup?: string | null;
}
export interface InstallOptions {
  components?: string[] | null;
  switchFromShroudforge?: boolean;
}
export interface InstallState {
  installed: boolean;
  receipt: InstallReceipt | null;
  proxy: ProxyClass | null;
  modified: string[];
  upgradeAvailable: string | null;
}

// profiles
export interface ModpackRef {
  url: string;
  sha256: string;
  name?: string;
  version?: string;
  size?: number;
}
export interface BoundServer {
  id: ServerId;
  name?: string;
  address?: string;
  modpack?: ModpackRef;
}
export interface ProfileMod {
  id: string;
  version: string;
  hash: string;
  enabled: boolean;
}
export interface Profile {
  format: number;
  id: ProfileId;
  name: string;
  created: string;
  updated: string;
  generation: number;
  notes: string;
  mods: Record<ModKind, ProfileMod[]>;
  dropins: ModKind[];
  dataDir: "shared" | "profile";
  server: BoundServer | null;
}
export interface ProfileSummary {
  id: ProfileId;
  name: string;
  updated: string;
  counts: Record<ModKind, number>;
  server: BoundServer | null;
  active: GameId[];
}
export interface Activation {
  gameId: GameId;
  profileId: ProfileId;
  generation: number;
  pathMode: PathMode;
  profileJson: string;
  copied: number;
  fingerprint: string;
  activated: string;
}
export type ModState = "pending" | "loaded" | "disabled" | "failed" | "skipped";
export interface Rejection {
  id: string;
  dir: string;
  state: ModState;
  reason: string;
}
export interface Resolution {
  order: string[];
  rejected: Rejection[];
}
export type NativePhase = "dllmain" | "early" | "boot" | "world";
export interface NativePlanEntry {
  id: string;
  version: string;
  phase: NativePhase;
  enabled: boolean;
  reason: string | null;
}
export interface ResolvePreview {
  side: RunSide;
  kiln: Resolution;
  native: NativePlanEntry[];
  warnings: string[];
}

// library
export interface LibraryRef {
  kind: ModKind;
  id: string;
  version: string;
  hash: string;
}
export interface LibraryVersion {
  version: string;
  hash: string;
  folder: string;
  imported: string;
  side: Side | null;
  clients: Clients | null;
  size: number;
  source: { type: "file" | "folder" | "modpack"; from: string } | null;
  manifest: Json | null;
}
export interface LibraryEntry {
  kind: ModKind;
  id: string;
  name: string;
  versions: LibraryVersion[];
}
export type DetectKind = "kiln" | "native" | "plugin" | "emf" | "modpack" | "runtime" | "bare-dll" | "unknown"
  // made for other engines, or not mods: imported only once Kiln supports them; `reason` says why not yet
  | "eml" | "shroudforge" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame" | "shroudlink" | "ember"
  | "proxy" | "data" | "save" | "reshade" | "cheat-table" | "asi";
/** kiln_manage::engines::Engine */
export type EngineId = "kiln" | "emf" | "shroudforge" | "eml" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame"
  | "shroudlink" | "ember" | "mod-loader-proxy" | "ultimate-asi-loader" | "aldrich" | "live-map" | "shared-xp" | "reshade"
  | "chain-loader";
export interface Detected {
  kind: DetectKind;
  path: string;
  id: string | null;
  version: string | null;
  name: string | null;
  reason: string;
  items: Detected[];
  /** The engine it was made for (other engines' kinds and loaders). */
  engine?: EngineId;
}
export interface ImportReport {
  imported: LibraryRef[];
  existing: LibraryRef[];
  skipped: { path: string; reason: string }[];
}
/** library_add's result (kiln_manage::quick::AddReport): what the library took, and what reached the profile. */
export interface AddedMod {
  kind: ModKind;
  id: string;
  version: string;
  hash: string;
  name: string;
}
/** One profile entry an add changed; profile_revert puts `before` back (null: the entry was new). */
export interface ProfileChange {
  kind: ModKind;
  before: ProfileMod | null;
  after: ProfileMod;
}
export interface AddReport {
  import: ImportReport;
  /** The profile the mods went into; null: nothing to add, or profiles exist but none is active in this game. */
  profileId: ProfileId | null;
  profileName: string | null;
  /** "My mods" was made now (and activated). */
  created: boolean;
  /** Now in the profile and enabled (new, another version, or switched on). */
  added: AddedMod[];
  /** Already in the profile as they are. */
  already: AddedMod[];
  changes: ProfileChange[];
  /** The game's profile.json was written now. */
  applied: boolean;
  /** The game runs: saved, applies at the next start. */
  running: boolean;
  activation: Activation | null;
  /** Mods the added ones require that the profile lacks. */
  missing: { id: string; range: string; by: string }[];
  /** Modpack zips among the paths: not imported, they go through the consent flow (modpack_install). */
  modpacks: string[];
}
// kiln lint: schema/lint-report.schema.json
export type LintSeverity = "error" | "warning" | "info" | "hint";
export interface LintSpan {
  start: number;
  end: number;
}
export interface LintFix {
  title: string;
  edits: { file: string; span: LintSpan; newText: string }[];
}
export interface LintDiagnostic {
  file: string;
  code: string;
  severity: LintSeverity;
  rule: string;
  message: string;
  span: LintSpan;
  line: number;
  col: number;
  endLine: number;
  endCol: number;
  pointer?: string;
  schema?: string;
  fixes: LintFix[];
  related: { file: string; span: LintSpan; message: string }[];
  docs?: string;
}
export interface LintReport {
  format: number;
  tool: string;
  version: string;
  ok: boolean;
  checked: string[];
  summary: { errors: number; warnings: number; infos: number; hints: number };
  catalog: { path: string; side: string | null; build: string | null } | null;
  diagnostics: LintDiagnostic[];
}

// configs
export interface SnapshotEntry {
  path: string;
  type: string;
  comment: string | null;
  default: Json;
  requiresRestart: boolean;
  synced: boolean;
  min?: number;
  max?: number;
  options?: string[];
}
export interface SchemaSnapshot {
  format: number;
  source: "kiln" | "emf";
  file: string;
  valueFormat: "jsonc" | "ini";
  id: string;
  mod: string;
  modVersion: string | null;
  kind: string | null;
  written: string | null;
  kiln: string | null;
  sections: { path: string; comment: string | null; synced: boolean }[];
  entries: SnapshotEntry[];
}
export interface ConfigFileInfo {
  file: string;
  mod: string | null;
  kind: "kiln" | "emf" | "native" | "other";
  hasSchema: boolean;
  modVersion: string | null;
  modified: string | null;
}
export interface ValidationError {
  path: string;
  message: string;
}
export interface ConfigDocument {
  file: string;
  text: string;
  values: Json;
  schema: SchemaSnapshot | null;
  errors: ValidationError[];
}
export interface NativeUserState {
  format: number;
  mods: Record<string, { enabled?: boolean }>;
}

// launch and state files
export type LaunchMode = "profile" | "vanilla" | "no-mods" | "once";
export interface LaunchRequest {
  mode: LaunchMode;
  profileId?: ProfileId | null;
  skipBackup?: boolean;
}
export interface LaunchResult {
  gameId: GameId;
  mode: LaunchMode;
  nonce: string | null;
  backup: SaveBackup | null;
  uri: string;
  started: string;
}
export type ChainStatus = "loaded" | "failed" | "missing" | "refused" | "duplicate" | "skipped";
export type RefusalDetail = "self" | "system" | "kiln" | "shroudforge" | "not-dll" | "outside" | "too-many";
export interface BootJson {
  format: number;
  kiln: string;
  abi?: number;
  pid: number;
  written: string;
  bootMs?: number;
  side: RunSide;
  exe?: { name?: string; sha256?: string; verified?: boolean };
  policy?: "verified-only" | "any";
  hooks: boolean;
  safeIdle?: string | null;
  playOnce?: { mode: string; nonce: string | null };
  profile: {
    mode: "legacy" | "active" | "once" | "no-mods" | "invalid";
    id?: string | null;
    name?: string | null;
    generation?: number;
    file?: string | null;
    error?: string | null;
    configDir?: string;
    dataDir?: string;
    counts?: Record<ModKind, number>;
  };
  paths?: Partial<Record<"game" | "kiln" | "mods" | "logs" | "data" | "control" | "config" | "state", string>>;
  chain: { path: string; source: "chain" | "emf" | "profile"; status: ChainStatus; detail: RefusalDetail | null; error?: number }[];
  services?: string[];
  plugins?: { file: string; id?: string; version?: string; status: "loaded" | "failed" | "skipped"; error?: string | null }[];
  native?: { loaded: number; failed: number; pending: number };
  clr?: { started: boolean; error: string | null };
  errors?: string[];
}
export interface ModsJson {
  format: number;
  pid: number;
  written: string;
  generation: number;
  side?: RunSide;
  api?: string;
  profile?: { id: string | null; generation?: number; mode?: string };
  mods: {
    id: string;
    version?: string | null;
    name?: string | null;
    folder?: string;
    source?: "profile" | "dropin";
    state: ModState | "unloaded";
    reason?: string | null;
    loadIndex?: number | null;
    side?: Side;
    clients?: Clients;
    faults?: number;
  }[];
}
export interface NativeJson {
  format: number;
  pid: number;
  written: string;
  policy: "always" | "verified-only" | "never";
  phase?: NativePhase;
  mods: {
    id: string;
    version?: string | null;
    folder?: string;
    source?: "profile" | "dropin";
    phase?: NativePhase;
    state: "pending" | "loaded" | "failed" | "disabled" | "skipped" | "policy" | "unsupported";
    reason?: string | null;
    notes?: string[];
    loadIndex?: number | null;
    dlls?: { file: string; status: ChainStatus; error?: number; ms?: number }[];
    init?: { export?: string; result?: number | null; fault?: string | null; ms?: number } | null;
  }[];
}
export interface BootState {
  boot: BootJson | null;
  mods: ModsJson | null;
  native: NativeJson | null;
  stale: boolean;
  /** kiln.log's error lines for the run that wrote boot.json (null: kiln.log is another run's or missing). */
  log?: { errors: number; first: string[] } | null;
}
export interface RequiredMod {
  id: string;
  version: string;
  hash: string;
  kind?: ModKind;
  name?: string;
}
export interface LastServer {
  format: number;
  written: string;
  outcome: "rejected" | "welcome" | "vanilla";
  reason?: string | null;
  server: { id?: string | null; name?: string | null; address?: string | null; kiln?: string | null };
  modpack?: ModpackRef | null;
  required: RequiredMod[];
  truncated: boolean;
  missing: string[];
  history?: LastServer[];
}

// logs
export interface LogFile {
  name: string;
  path: string;
  size: number;
  modified: string;
  kind: "kiln" | "proxy" | "mod" | "game" | "sigdb" | "crash" | "other";
}
export interface LogChunk {
  file: string;
  offset: number;
  next: number;
  size: number;
  text: string;
  truncated: boolean;
}
export interface CrashReport {
  name: string;
  created: string;
  size: number;
}

// saves and the save editor
export interface SaveSlot {
  kind: "world" | "character" | "other";
  file: string;
  path: string;
  modified: string;
  size: number;
  steamId: string | null;
}
export interface SaveBackup {
  id: string;
  created: string;
  reason: "launch" | "modpack" | "manual" | "restore";
  profileId: ProfileId | null;
  fingerprint: string | null;
  note: string | null;
  files: number;
  bytes: number;
}
export interface EditorSaveFile {
  path: string;
  file: string;
  modified: number;
}
export interface EditorFileInfo {
  modified: number;
}
export interface EditorBackup {
  kind: string;
  label: string;
  path: string;
  modified: number;
}

// servers
export interface RemoteSpec {
  host: string;
  port?: number | null;
  dir: string;
  instance?: string | null;
}
export interface ServerInstance {
  id: ServerId;
  name: string;
  kind: "local" | "remote";
  dir: string | null;
  port: number;
  queryPort: number;
  wine: string | null;
  policy: string;
  source: "kilnctl" | "launcher" | "remote" | "steam";
  remote: RemoteSpec | null;
  runner?: "kilnctl" | "steam" | "windows" | "remote";
  prefix?: string | null;
  backupDir?: string | null;
  kilnctl?: string | null;
  stopTimeoutS?: number;
}
export interface ServerSpec {
  id: ServerId;
  name: string;
  dir: string;
  port: number;
  reflinkFrom?: string | null;
  policy?: string | null;
  wine?: string | null;
  skipSteamcmd?: boolean;
}
export interface AdminEndpoint {
  format: number;
  protocol: "kap/1";
  host: string;
  port: number;
  pid: number;
  started: string;
  kiln?: string | null;
  admin?: string | null;
  server: { id?: string | null; name?: string | null };
  token?: string | null;
  bridge?: string | null;
}
// server console (server_console_open, kiln://console; kiln_manage::server::console)
export type ConsoleLevel = "info" | "warn" | "error" | "debug" | "meta";
export interface ConsoleLine {
  text: string;
  level: ConsoleLevel;
}
/** capture: the output of a launcher (or `kilnctl start`) run; gamelog: started elsewhere, its game log; none: no output yet. */
export type ConsoleSource = "capture" | "gamelog" | "none";
export interface ConsoleOpen {
  subscription: string;
  serverId: ServerId;
  source: ConsoleSource;
  /** the file shown */
  path: string | null;
  /** the run's last lines, oldest first */
  lines: ConsoleLine[];
  /** older lines of the run were left out */
  truncated: boolean;
}
export interface ServerStatus {
  id: ServerId;
  state: "stopped" | "starting" | "running" | "stopping" | "unknown";
  pid: number | null;
  uptimeS: number | null;
  players: number | null;
  kiln: string | null;
  /** Kiln in the server folder, running or not: the installed runtime version ("unknown" for a Kiln put there by
   * hand); null when it has none, and for a remote server. */
  installed?: string | null;
  boot: BootJson | null;
  endpoint: AdminEndpoint | null;
}
export interface ServerSettings {
  path: string;
  values: Json;
  /** The server settings schema (shape in pages/servers/serverApi.ts). */
  schema?: unknown;
  running?: boolean;
}
export interface ServerMod {
  id: string;
  version: string | null;
  folder: string;
  enabled: boolean;
  source: LibraryRef | null;
  clients: Clients | null;
  state: ModState | "unloaded" | null;
  kind?: "kiln" | "native" | null;
}
export interface ServerBackup {
  name: string;
  created: string;
  bytes: number;
  source: "kilnctl" | "kiln";
}
export interface ServerQuery {
  name: string;
  players: number;
  maxPlayers: number;
  version: string | null;
  pingMs: number | null;
}
export interface ModpackManifest {
  format: number;
  name: string;
  version: string;
  created?: string;
  server: { id: ServerId; name?: string | null };
  kiln?: string;
  mods: { kind: ModKind; id: string; version: string; name?: string; path: string; hash: string; clients?: Clients; side?: Side }[];
}
export interface BuiltModpack {
  file: string;
  sha256: string;
  size: number;
  manifest: ModpackManifest;
}
export interface ModpackPublishState {
  config: ModpackRef | null;
  built: BuiltModpack | null;
  required: RequiredMod[];
}

// admin (KAP types and hooks: ./admin.ts)
export type AdminTransport = "auto" | "tcp" | "file" | "ssh";
export interface AdminSession {
  session: string;
  serverId: ServerId;
  transport: AdminTransport;
  ops: string[];
  topics: string[];
  kiln: string | null;
  server: { id?: string | null; name?: string | null };
}
export type AdminEvent =
  | { kind: "push"; topic: string; seq: number; data: Json }
  | { kind: "closed"; reason: string; retry?: boolean }
  | { kind: "error"; message: string }
  | { kind: "ready"; transport: AdminTransport; ops: string[]; topics: string[] };

// modpacks (client)
export type ModpackSource = { url: string; sha256: string } | { path: string; sha256?: string | null };
export interface ModpackPreview {
  manifest: ModpackManifest | null;
  server: { id?: string | null; name?: string | null; address?: string | null; kiln?: string | null };
  url: string | null;
  https: boolean;
  size: number | null;
  missing: RequiredMod[];
  present: RequiredMod[];
  nativeCode: boolean;
}
export interface ModpackInstallRequest {
  source: ModpackSource;
  consent: boolean;
  allowHttp?: boolean;
  activate?: boolean | null;
}
export interface ModpackInstallResult {
  profile: Profile;
  imported: LibraryRef[];
  backup: SaveBackup | null;
  activation: Activation | null;
}

// ---------------------------------------------------------------- commands (contracts.md section 9)

// app.rs
export const appInfo = () => call<AppInfo>("app_info");
export const appSettingsGet = () => call<LauncherSettings>("app_settings_get");
export const appSettingsSet = (settings: LauncherSettings) => call<LauncherSettings>("app_settings_set", { settings });
export const appOpenPath = (path: string) => call<null>("app_open_path", { path });
export const appOpenUrl = (url: string) => call<null>("app_open_url", { url });
export const taskList = () => call<Task[]>("task_list");
export const taskGet = (taskId: TaskId) => call<Task>("task_get", { taskId });
export const taskCancel = (taskId: TaskId) => call<null>("task_cancel", { taskId });
/** Local game assets for the save editor and the admin map (`kiln-asset` protocol roots). */
export interface AssetsStatus {
  root: string;
  editor: {
    present: boolean;
    bytes: number;
    /** gameart additions (optional for older backends and the mock): "game" = extracted, "copy" = from a folder. */
    source?: "game" | "copy" | null;
    /** Game data build the art was extracted from ("1287369"). */
    build?: string | null;
    /** The client "Extract from game" reads, and its data build. */
    gameId?: string | null;
    gameBuild?: string | null;
    /** true: matches the installed game; false: the game changed since (offer re-extraction); null: unknown. */
    current?: boolean | null;
    canExtract?: boolean;
    /** Running extraction task. */
    task?: TaskId | null;
    /** Files per group of the last extraction, and when it ran. */
    counts?: Record<string, number>;
    extracted?: string | null;
    /** Items of Kiln mods (`modded/`): `current` false = the mods changed, the overlay is rebuilt on its own. */
    modded?: { items: number; icons: number; current: boolean; version: string | null; generated: string | null; mods: string[] } | null;
  };
  /** gameart additions as for the editor; `markers`: map/game/markers.json (from the game) or map/data/markers.json
   * (a local copy) present; `counts.markers`/`markerCategories`/`placeNames`/`markerLanguages`: the last extraction. */
  admin: {
    map: boolean; catalog: boolean; bytes: number;
    source?: "game" | "copy" | null; build?: string | null; gameBuild?: string | null; current?: boolean | null;
    canExtract?: boolean; markers?: boolean; task?: TaskId | null;
    counts?: Record<string, number>; extracted?: string | null;
  };
}
export const appAssetsStatus = () => call<AssetsStatus>("app_assets_status");
/** Task result: AssetsStatus. `from` null: extracts the art from the installed client (`admin`: relief, tiles, markers
 * in every game language, marker icons, fonts; markers, finder data and catalogs copied from
 * `$KILN_ADMIN_EXTRAS` when set); a folder: copies it from
 * there (dev fallback). Never downloads. While one runs, a second call returns the
 * running task. */
export const appAssetsImport = (kind: "editor" | "admin", from: string | null = null) => call<TaskId>("app_assets_import", { kind, from });

// update.rs: the launcher's self-update (frostdev.io, then GitHub; signed). Changes arrive as kiln://update.
// The Nexus Mods build has the same commands without a self-update: phase "disabled", install/restart reject with
// `unsupported`, nothing is fetched (and the UI calls none of them there: api/distribution.ts).
export type UpdateChannel = "stable" | "dev";
export type UpdatePhase = "idle" | "checking" | "upToDate" | "available" | "skipped" | "failed" | "downloading" | "ready" | "disabled";
export type UpdateInstallKind = "appimage" | "nsis" | "msi" | "deb" | "rpm" | "unbundled";
export interface UpdateFailure { source: string; step: "manifest" | "download" | "signature" | "hash"; message: string }
export interface UpdateStatus {
  /** This launcher's version. */
  current: string;
  /** "nexus": the build has no self-update (phase "disabled", `page` is the Kiln page on Nexus Mods). */
  distribution?: Distribution;
  channel: UpdateChannel;
  /** Automatic checks are on. */
  auto: boolean;
  /** inPlace false: the launcher cannot replace itself (deb, rpm, a development build); `reason` says why. */
  install: { kind: UpdateInstallKind; inPlace: boolean; reason: string | null };
  /** The download page. */
  page: string;
  phase: UpdatePhase;
  checked: string | null;
  available: { version: string; notes: string | null; date: string | null; source: string; page: string; runtime: string | null } | null;
  /** Sources that failed in the last check or download, also when the next one then worked. */
  failures: UpdateFailure[];
  progress: { received: number; total: number | null; source: string } | null;
  /** installed: in place, a restart runs it (AppImage); false: "Restart now" runs the installer (Windows). */
  ready: { version: string; source: string; installed: boolean } | null;
  skipped: string | null;
  /** This start follows an update the launcher installed (until updateAck). */
  justUpdated: { from: string; to: string; at: string; source: string; notes: string | null } | null;
  taskId: TaskId | null;
  error: KilnError | null;
}
/** Guard the command boundary as well as the UI: even a stale callback or a directly mounted hook cannot invoke
 *  an updater command in the Nexus build. Failure to read the distribution also fails closed. */
async function selfUpdateCall<T>(cmd: string, args: Record<string, unknown> = {}): Promise<T> {
  const info = await appInfo();
  if ((info.distribution ?? "direct") !== "direct") throw { code: "unsupported", message: "Self-update is disabled in this build" } satisfies KilnError;
  return call<T>(cmd, args);
}
export const updateStatus = () => selfUpdateCall<UpdateStatus>("update_status");
export const updateCheck = () => selfUpdateCall<UpdateStatus>("update_check");
/** Task result: UpdateStatus. */
export const updateInstall = () => selfUpdateCall<TaskId>("update_install");
/** Restarts into the update that is ready (Windows: runs its installer). Resolves only if that failed. */
export const updateRestart = () => selfUpdateCall<null>("update_restart");
/** null: un-skips. */
export const updateSkip = (version: string | null) => selfUpdateCall<UpdateStatus>("update_skip", { version });
export const updateAck = () => selfUpdateCall<UpdateStatus>("update_ack");

// game.rs
export const gameList = () => call<GameInstall[]>("game_list");
export const gameDetect = () => call<GameInstall[]>("game_detect");
export const gameAdd = (path: string) => call<GameInstall>("game_add", { path });
export const gameRemove = (gameId: GameId) => call<null>("game_remove", { gameId });
export const gameStatus = (gameId: GameId) => call<GameStatus>("game_status", { gameId });

// runtime.rs
export const runtimeList = () => call<RuntimeBundle[]>("runtime_list");
export const runtimeImport = (path: string) => call<RuntimeBundle>("runtime_import", { path });
export const runtimeStatus = (gameId: GameId) => call<InstallState>("runtime_status", { gameId });
/** Task result: InstallReceipt. */
export const runtimeInstall = (gameId: GameId, version: string, options: InstallOptions = {}) =>
  call<TaskId>("runtime_install", { gameId, version, options });
/** Task result: null. */
export const runtimeUninstall = (gameId: GameId) => call<TaskId>("runtime_uninstall", { gameId });
/** Task result: InstallReceipt. */
export const runtimeRollback = (gameId: GameId, version: string) => call<TaskId>("runtime_rollback", { gameId, version });
export const runtimeClassify = (path: string) => call<ProxyClass>("runtime_classify", { path });

// profile.rs
export const profileList = () => call<ProfileSummary[]>("profile_list");
export const profileGet = (profileId: ProfileId) => call<Profile>("profile_get", { profileId });
export const profileCreate = (name: string, copyFrom: ProfileId | null = null) => call<Profile>("profile_create", { name, copyFrom });
export const profileSave = (profile: Profile) => call<Profile>("profile_save", { profile });
export const profileDelete = (profileId: ProfileId) => call<null>("profile_delete", { profileId });
export const profileResolve = (profileId: ProfileId, side: RunSide) => call<ResolvePreview>("profile_resolve", { profileId, side });
export const profileActivate = (profileId: ProfileId, gameId: GameId) => call<Activation>("profile_activate", { profileId, gameId });
export const profileDeactivate = (gameId: GameId) => call<null>("profile_deactivate", { gameId });
export const profileActive = (gameId: GameId) => call<Activation | null>("profile_active", { gameId });
/** Undoes a library_add (its changes); the game's profile.json follows where the profile is active. */
export const profileRevert = (profileId: ProfileId, changes: ProfileChange[]) => call<Profile>("profile_revert", { profileId, changes });
/** The active profile's activation; with no profile at all, makes "My mods" and activates it; null when profiles exist
 *  but none is active in this game. */
export const profileEnsureDefault = (gameId: GameId) => call<Activation | null>("profile_ensure_default", { gameId });

// engines.rs: other engines' mods (docs/kiln/engines.md)
/** kiln_manage::engine_mods::EngineSettings. `format` "extended.mod.json" (ShroudForge: JSON Schema with x-kiln-* hints),
 *  "shroudtopia.json" (schema derived from the values), or "none" (`note` says why). */
export interface EngineSettings {
  kind: ModKind;
  /** The key the settings live under (Shroudtopia: the mod's key in shroudtopia.json `mods`). */
  id: string;
  format: "extended.mod.json" | "shroudtopia.json" | "none" | string;
  file: string | null;
  schema: Json;
  values: Json;
  note?: string | null;
}
/** One engine plugin's state file `kiln/state/<engine>.json` (null: not written yet). */
export interface EngineStatus {
  kind: ModKind;
  plugin: string;
  state: Json | null;
  file: string;
}
/** EML's backup of a vanilla `.kfc`; `patched`: the `.kfc` differs from it. */
export interface EmlBackup {
  file: string;
  backup: string;
  patched: boolean;
}
export const engineSettingsGet = (kind: ModKind, id: string, profileId: ProfileId, gameId: GameId | null = null) =>
  call<EngineSettings>("engine_settings_get", { kind, id, profileId, gameId });
/** One top-level key; rejects with `unsupported` for kinds without settings, `not_found` for a ShroudForge mod not
 *  placed yet (activate the profile first). */
export const engineSettingsSet = (kind: ModKind, id: string, profileId: ProfileId, gameId: GameId | null, key: string, value: Json) =>
  call<EngineSettings>("engine_settings_set", { kind, id, profileId, gameId, key, value });
export const engineStatus = (gameId: GameId) => call<EngineStatus[]>("engine_status", { gameId });
export const emlBackup = (gameId: GameId) => call<EmlBackup[]>("eml_backup", { gameId });
/** Copies each `.kfc.bak` over its patched `.kfc`; the files restored. Rejects with game_running while the game runs. */
export const emlRestore = (gameId: GameId) => call<string[]>("eml_restore", { gameId });

// library.rs
export const libraryList = (kind: ModKind | null = null) => call<LibraryEntry[]>("library_list", { kind });
export const libraryDetect = (path: string) => call<Detected>("library_detect", { path });
/** Task result: ImportReport. */
export const libraryImport = (paths: string[]) => call<TaskId>("library_import", { paths });
/** Task result: AddReport. Import + add to the game's active profile ("My mods" for a first-time player) + write the
 *  game's profile.json, in one task. Rejects at once (unsupported) when Kiln is not in the game. */
export const libraryAdd = (gameId: GameId, paths: string[]) => call<TaskId>("library_add", { gameId, paths });
export const libraryRemove = (entry: LibraryRef, force = false) => call<null>("library_remove", { entry, force });
/** One entry (with profileId: checked against the profile's other mods), or a whole profile (entry null). */
export const libraryLint = (entry: LibraryRef | null, profileId: ProfileId | null = null) => call<LintReport>("library_lint", { entry, profileId });

// config.rs
export const configList = (target: ConfigTarget) => call<ConfigFileInfo[]>("config_list", { target });
export const configRead = (target: ConfigTarget, file: string) => call<ConfigDocument>("config_read", { target, file });
export const configSet = (target: ConfigTarget, file: string, path: string, value: Json) =>
  call<ConfigDocument>("config_set", { target, file, path, value });
export const configWrite = (target: ConfigTarget, file: string, text: string) => call<ConfigDocument>("config_write", { target, file, text });
export const configReset = (target: ConfigTarget, file: string, path: string | null = null) =>
  call<ConfigDocument>("config_reset", { target, file, path });
export const configNativeState = (target: ConfigTarget) => call<NativeUserState>("config_native_state", { target });
export const configNativeSet = (target: ConfigTarget, modId: string, enabled: boolean | null) =>
  call<NativeUserState>("config_native_set", { target, modId, enabled });
/** Kiln's own `<game>/kiln/kiln.json` (policy, log level, plugins, native policy, mods fault settings); the schema is
 *  the launcher's built-in snapshot (src/data/kiln-json.snapshot.json). */
export const configKilnRead = (gameId: GameId) => call<ConfigDocument>("config_kiln_read", { gameId });
export const configKilnSet = (gameId: GameId, path: string, value: Json) => call<ConfigDocument>("config_kiln_set", { gameId, path, value });
export const configKilnReset = (gameId: GameId, path: string) => call<ConfigDocument>("config_kiln_reset", { gameId, path });

// launch.rs
export const launchPlay = (gameId: GameId, request: LaunchRequest) => call<LaunchResult>("launch_play", { gameId, request });
export const launchState = (gameId: GameId) => call<BootState>("launch_state", { gameId });

// logs.rs
export const logsList = (target: LogTarget) => call<LogFile[]>("logs_list", { target });
export const logsRead = (target: LogTarget, file: string, offset: number | null = null, maxBytes: number | null = null) =>
  call<LogChunk>("logs_read", { target, file, offset, maxBytes });
/** Returns a subscription id; lines arrive as kiln://log events. */
export const logsFollow = (target: LogTarget, file: string) => call<string>("logs_follow", { target, file });
export const logsUnfollow = (subscription: string) => call<null>("logs_unfollow", { subscription });
export const logsCrashReports = (target: LogTarget) => call<CrashReport[]>("logs_crash_reports", { target });

// saves.rs
export const savesList = (gameId: GameId) => call<SaveSlot[]>("saves_list", { gameId });
export const savesBackups = (gameId: GameId) => call<SaveBackup[]>("saves_backups", { gameId });
export const savesBackup = (gameId: GameId, note: string | null = null) => call<SaveBackup>("saves_backup", { gameId, note });
export const savesRestore = (gameId: GameId, backupId: string) => call<null>("saves_restore", { gameId, backupId });
export const savesDelete = (gameId: GameId, backupId: string) => call<null>("saves_delete", { gameId, backupId });

// editor.rs (the vendored Save Editor's commands)
const bytes = (data: Uint8Array | number[]) => (data instanceof Uint8Array ? Array.from(data) : data);
export const editorListSaves = () => call<EditorSaveFile[]>("editor_list_saves");
export const editorFileInfo = (path: string) => call<EditorFileInfo>("editor_file_info", { path });
export const editorReadFile = (path: string) => call<ArrayBuffer>("editor_read_file", { path });
export const editorZstdDecompress = (data: Uint8Array | number[]) => call<ArrayBuffer>("editor_zstd_decompress", { data: bytes(data) });
export const editorZstdCompress = (data: Uint8Array | number[]) => call<ArrayBuffer>("editor_zstd_compress", { data: bytes(data) });
export const editorGameRunning = () => call<boolean>("editor_game_running");
export const editorBackupFile = (path: string) => call<string>("editor_backup_file", { path });
export const editorWriteSave = (path: string, data: Uint8Array | number[]) => call<null>("editor_write_save", { path, data: bytes(data) });
export const editorListBackups = (target: string) => call<EditorBackup[]>("editor_list_backups", { target });
export const editorRestoreBackup = (src: string, target: string) => call<null>("editor_restore_backup", { src, target });
export const editorOpenBackups = () => call<null>("editor_open_backups");

// server.rs
export const serverList = () => call<ServerInstance[]>("server_list");
export const serverGet = (serverId: ServerId) => call<ServerInstance>("server_get", { serverId });
/** Task result: ServerInstance. */
export const serverCreate = (spec: ServerSpec) => call<TaskId>("server_create", { spec });
export const serverImport = (dir: string, name: string) => call<ServerInstance>("server_import", { dir, name });
export const serverRemove = (serverId: ServerId, deleteFiles = false) => call<null>("server_remove", { serverId, deleteFiles });
export const serverStatus = (serverId: ServerId) => call<ServerStatus>("server_status", { serverId });
/** Task result: ServerStatus. */
export const serverStart = (serverId: ServerId) => call<TaskId>("server_start", { serverId });
/** Task result: ServerStatus. */
export const serverStop = (serverId: ServerId) => call<TaskId>("server_stop", { serverId });
/** Task result: ServerStatus. */
export const serverRestart = (serverId: ServerId) => call<TaskId>("server_restart", { serverId });
/** Task result: string | null (the new build id). */
export const serverUpdate = (serverId: ServerId) => call<TaskId>("server_update", { serverId });
/** Task result: InstallReceipt. */
export const serverDeploy = (serverId: ServerId, version: string) => call<TaskId>("server_deploy", { serverId, version });
export const serverSettingsGet = (serverId: ServerId) => call<ServerSettings>("server_settings_get", { serverId });
export const serverSettingsSet = (serverId: ServerId, values: Json) => call<ServerSettings>("server_settings_set", { serverId, values });
export const serverMods = (serverId: ServerId) => call<ServerMod[]>("server_mods", { serverId });
/** Task result: ServerMod[]. */
export const serverModsSync = (serverId: ServerId, mods: LibraryRef[]) => call<TaskId>("server_mods_sync", { serverId, mods });
export const serverModEnable = (serverId: ServerId, folder: string, enabled: boolean) =>
  call<null>("server_mod_enable", { serverId, folder, enabled });
export const serverBackups = (serverId: ServerId) => call<ServerBackup[]>("server_backups", { serverId });
/** Task result: ServerBackup. */
export const serverBackupCreate = (serverId: ServerId) => call<TaskId>("server_backup_create", { serverId });
/** Task result: null. */
export const serverBackupRestore = (serverId: ServerId, name: string) => call<TaskId>("server_backup_restore", { serverId, name });
export const serverQuery = (serverId: ServerId) => call<ServerQuery>("server_query", { serverId });
export const serverModpackStatus = (serverId: ServerId) => call<ModpackPublishState>("server_modpack_status", { serverId });
/** Task result: BuiltModpack. */
export const serverModpackBuild = (serverId: ServerId, name: string, version: string) =>
  call<TaskId>("server_modpack_build", { serverId, name, version });
export const serverModpackPublish = (serverId: ServerId, url: string) => call<ModpackPublishState>("server_modpack_publish", { serverId, url });
/** The server's last output lines (its capture file, else its game log) and a subscription for new ones (`kiln://console`). */
export const serverConsoleOpen = (serverId: ServerId, maxLines: number | null = null) => call<ConsoleOpen>("server_console_open", { serverId, maxLines });
export const serverConsoleClose = (subscription: string) => call<null>("server_console_close", { subscription });

// admin.rs (hooks in ./admin.ts)
export const adminConnect = (serverId: ServerId, transport: AdminTransport = "auto") => call<AdminSession>("admin_connect", { serverId, transport });
export const adminDisconnect = (session: string) => call<null>("admin_disconnect", { session });
export const adminSessions = () => call<AdminSession[]>("admin_sessions");
/** The op's `data`; rejects with op_failed (message = the op's error) when it answers ok=false. */
export const adminCall = <T = Json>(session: string, op: string, args: Record<string, Json> = {}) => call<T>("admin_call", { session, op, args });
export const adminSubscribe = (session: string, topic: string) => call<null>("admin_subscribe", { session, topic });
export const adminUnsubscribe = (session: string, topic: string) => call<null>("admin_unsubscribe", { session, topic });

// modpack.rs
export const modpackPending = (gameId: GameId) => call<LastServer | null>("modpack_pending", { gameId });
/** source null: the modpack from state/last-server.json. */
export const modpackInspect = (gameId: GameId, source: ModpackSource | null = null) => call<ModpackPreview>("modpack_inspect", { gameId, source });
/** Task result: ModpackInstallResult. `consent` must be the user's explicit yes. */
export const modpackInstall = (gameId: GameId, request: ModpackInstallRequest) => call<TaskId>("modpack_install", { gameId, request });
export const modpackDismiss = (gameId: GameId, serverId: ServerId) => call<null>("modpack_dismiss", { gameId, serverId });

// deck.rs: the Steam Deck UI (docs/kiln/launcher/deck.md; src/deck/)
export type UiMode = "auto" | "deck" | "desktop";
export interface DeckInfo {
  /** A Steam Deck, SteamOS or gamescope was seen (any signal). */
  detected: boolean;
  /** "SteamDeck=1", "SteamOS=1", "XDG_CURRENT_DESKTOP=gamescope", "GAMESCOPE_WAYLAND_DISPLAY", "dmi:Jupiter", "os-release:steamos". */
  signals: string[];
  /** Inside a gamescope session (Game Mode). */
  gamescope: boolean;
  /** The Deck model from DMI: "jupiter" (LCD), "galileo" (OLED). */
  device: string | null;
  /** `--deck` / `--desktop` (or KILN_LAUNCHER_UI) for this start: wins over the setting and the detection. */
  forced: "deck" | "desktop" | null;
}
export interface DeckPlace { name: string; path: string; kind: "downloads" | "home" | "desktop" | "card" | "games" }
export interface DeckFoundMod {
  path: string;
  /** The file or folder name. */
  file: string;
  folder: boolean;
  size: number;
  modified: string | null;
  /** library_detect's answer (kind kiln, native, plugin, emf, bare-dll, modpack, or unknown with items). */
  detected: Detected;
}
export interface DeckFolder {
  dir: string;
  name: string;
  parent: string | null;
  /** Sub-folders that are not mods, by name. */
  folders: { name: string; path: string }[];
  /** What the library recognises here, newest first. */
  mods: DeckFoundMod[];
  /** Other files left out. */
  other: number;
  truncated: boolean;
  /** enshrouded.exe is here. */
  hasGame: boolean;
  places: DeckPlace[];
}
export interface GamepadStatus {
  /** The native reader exists here (Linux). */
  native: boolean;
  running: boolean;
  devices: { device: string; name: string; vendor: number; product: number }[];
  /** Gamepads that could not be opened (permissions). */
  denied: number;
}
export type PadButton = "a" | "b" | "x" | "y" | "lb" | "rb" | "lt" | "rt" | "select" | "start" | "guide" | "ls" | "rs" | "up" | "down" | "left" | "right";
export type GamepadEvent =
  | { kind: "button"; button: PadButton; pressed: boolean; repeat: boolean; device: string }
  | { kind: "axis"; axis: "rx" | "ry"; value: number; device: string }
  | { kind: "connected"; device: string; name: string; vendor: number; product: number }
  | { kind: "disconnected"; device: string; name: string };
/** One folder for Add mods (null: Downloads): the mods the library recognises, sub-folders, places. */
export const deckBrowse = (dir: string | null = null) => call<DeckFolder>("deck_browse", { dir });
/** Starts (on) or stops the native gamepad reader; events arrive as kiln://gamepad. */
export const deckGamepad = (on: boolean) => call<GamepadStatus>("deck_gamepad", { on });
export const deckFullscreen = (on: boolean) => call<boolean>("deck_fullscreen", { on });
export const deckQuit = () => call<null>("deck_quit");

/** Every command name (the Rust side registers the same list; mock.ts answers or rejects each). */
export const COMMANDS = [
  "app_info", "app_settings_get", "app_settings_set", "app_open_path", "app_open_url", "task_list", "task_get", "task_cancel",
  "game_list", "game_detect", "game_add", "game_remove", "game_status",
  "runtime_list", "runtime_import", "runtime_status", "runtime_install", "runtime_uninstall", "runtime_rollback", "runtime_classify",
  "profile_list", "profile_get", "profile_create", "profile_save", "profile_delete", "profile_resolve", "profile_activate",
  "profile_deactivate", "profile_active",
  "library_list", "library_detect", "library_import", "library_remove", "library_lint",
  "config_list", "config_read", "config_set", "config_write", "config_reset", "config_native_state", "config_native_set",
  "launch_play", "launch_state",
  "logs_list", "logs_read", "logs_follow", "logs_unfollow", "logs_crash_reports",
  "saves_list", "saves_backups", "saves_backup", "saves_restore", "saves_delete",
  "editor_list_saves", "editor_file_info", "editor_read_file", "editor_zstd_decompress", "editor_zstd_compress",
  "editor_game_running", "editor_backup_file", "editor_write_save", "editor_list_backups", "editor_restore_backup",
  "editor_open_backups",
  "server_list", "server_get", "server_create", "server_import", "server_remove", "server_status", "server_start", "server_stop",
  "server_restart", "server_update", "server_deploy", "server_settings_get", "server_settings_set", "server_mods",
  "server_mods_sync", "server_mod_enable", "server_backups", "server_backup_create", "server_backup_restore", "server_query",
  "server_modpack_status", "server_modpack_build", "server_modpack_publish",
  "admin_connect", "admin_disconnect", "admin_sessions", "admin_call", "admin_subscribe", "admin_unsubscribe",
  "modpack_pending", "modpack_inspect", "modpack_install", "modpack_dismiss",
  "config_kiln_read", "config_kiln_set", "config_kiln_reset",
  "app_assets_status", "app_assets_import",
  "server_console_open", "server_console_close",
  "update_status", "update_check", "update_install", "update_restart", "update_skip", "update_ack",
  "library_add", "profile_revert", "profile_ensure_default",
  "deck_browse", "deck_gamepad", "deck_fullscreen", "deck_quit",
  "nexus_account", "nexus_key_set", "nexus_key_clear", "nexus_search", "nexus_mod", "nexus_installed", "nexus_download", "nexus_links", "nexus_link_install", "nexus_link_dismiss", "nexus_handler_status", "nexus_handler_set",
  "engine_settings_get", "engine_settings_set", "engine_status", "eml_backup", "eml_restore",
] as const;
export type CommandName = (typeof COMMANDS)[number];

// ---------------------------------------------------------------- events (contracts.md section 10)

export interface KilnEvents {
  "kiln://task": Task;
  "kiln://game": { gameId: GameId; kind: "started" | "stopped" | "boot" | "mods" | "native" | "status"; pid: number | null; status: GameStatus | null };
  "kiln://log": { subscription: string; target: LogTarget; file: string; lines: string[]; next: number };
  "kiln://last-server": { gameId: GameId; lastServer: LastServer };
  "kiln://server": { serverId: ServerId; status: ServerStatus };
  "kiln://admin": { session: string; serverId: ServerId; event: AdminEvent };
  /** `reset`: `lines` replace everything shown (a start, a stop, a start outside the launcher). */
  "kiln://console": { subscription: string; serverId: ServerId; reset: boolean; source: ConsoleSource; path: string | null; truncated: boolean; lines: ConsoleLine[] };
  "kiln://update": UpdateStatus;
  /** The Deck UI's native gamepad reader (src-tauri/src/deck/gamepad.rs). */
  "kiln://gamepad": GamepadEvent;
  /** An Enshrouded Mod Manager Download (nxm://) link arrived: `count` wait in nexus_links (api/nexus.ts). */
  "kiln://nxm": { count: number };
}
export type KilnEventName = keyof KilnEvents;

/** Subscribes to a launcher event; resolves to the unsubscribe function. */
export async function onKilnEvent<E extends KilnEventName>(name: E, handler: (payload: KilnEvents[E]) => void): Promise<() => void> {
  if (isTauri()) return tauriListen<KilnEvents[E]>(name, (e) => handler(e.payload));
  return mockListen(name, handler as (payload: unknown) => void);
}
