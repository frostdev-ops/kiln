// Typed wrappers for every Tauri command of the Kiln Launcher.
// Contract: docs/kiln/launcher/contracts.md sections 9-11. Command names and argument shapes are stable; change them
// together with the contract. Pages and stores import from here, never call `invoke` directly.
// Types mirror crates/kiln-manage (serde camelCase) and launcher/src-tauri/src/commands/*.rs.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { listen as tauriListen } from "@tauri-apps/api/event";
import { canFallBack, noteFallback } from "./fallback";
import { loadMock } from "./mockLoad";

// ---------------------------------------------------------------- plumbing

/** The browser mock: a chunk of its own in a build (./mockLoad.ts). */
export { loadMock };

/** True inside the Tauri webview; false in a plain browser (the mock layer answers then). */
export const isTauri = (): boolean => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

export type ErrorCode =
  | "not_implemented" | "invalid_argument" | "not_found" | "already_exists" | "io" | "parse" | "schema" | "conflict"
  | "game_running" | "steam_updating" | "permission" | "unsupported" | "network" | "hash_mismatch"
  | "consent_required" | "refused" | "timeout" | "canceled" | "busy" | "auth_failed" | "admin_unavailable"
  | "op_failed" | "internal"
  /** The data folder cannot be used: its pointer names a folder that is missing, unreadable or not a Kiln data folder. */
  | "data_folder";

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
    return isTauri() ? await tauriInvoke<T>(cmd, args) : await (await loadMock()).mockInvoke<T>(cmd, args);
  } catch (e) {
    const err = toKilnError(e);
    // Development aid (./fallback.ts): read-only commands the crate does not implement yet answer from the mock.
    if (isTauri() && err.code === "not_implemented" && canFallBack(cmd)) {
      noteFallback(cmd);
      return loadMock().then((m) => m.mockInvoke<T>(cmd, args)).catch((m) => { throw toKilnError(m); });
    }
    throw err;
  }
}

// ---------------------------------------------------------------- shared types

export type GameId = string;
export type ProfileId = string;
export type ServerId = string;
export type TaskId = string;
/** kiln_manage::manifest::ModKind: Kiln's own four, then other engines' mods, each imported by the Library and run by
 *  Kiln's plugin for that engine (XHL companions require external XHL-Core; Ember's are held but don't run yet: no published mod ABI). Labels and helpers:
 *  ./kinds.ts. */
export type ModKind = "kiln" | "native" | "plugin" | "emf"
  | "eml" | "xhl" | "shroudforge" | "set" | "greeneye" | "flame" | "shroudtopia"
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
  /** Compiled Nexus integration capability. Missing from older backends: unavailable. */
  nexusEnabled?: boolean;
  /** KILN_LAUNCHER_NO_WEBGL=1 for this start: the plain Play button, no WebGL fire. */
  noWebgl?: boolean;
  /** Steam Deck / SteamOS / gamescope detection and `--deck` / `--desktop` (src/deck/mode.tsx chooses the UI). */
  deck?: DeckInfo;
  /** "direct" (frostdev.io, GitHub: updates itself) or "nexus" (Nexus Mods: no self-update; the UI shows no update
   *  checks, notices or buttons, only a link to the Kiln page there). Absent (an older backend): direct. */
  distribution?: Distribution;
  /** Linux only: how this start draws the window and whether the next start draws without the GPU (render.rs). */
  render?: RenderInfo;
}
/** How the Linux launcher draws its window (src-tauri/src/render.rs). */
export interface RenderInfo {
  /** This start: with the GPU, or without it (software mode). */
  mode: "gpu" | "software";
  /** Why: nothing asked; the marker (Settings, or the launcher after its page did not start); the restart after that;
   *  KILN_LAUNCHER_RENDER; --software-render / --gpu-render. */
  why: "default" | "marker" | "restart" | "environment" | "argument";
  /** The marker is there: a start without KILN_LAUNCHER_RENDER or an argument draws without the GPU. */
  marker: boolean;
  /** Who set the marker, why and when. */
  markerWhy?: string;
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
  /** "Show notes about mod folders" (default on; absent from an older backend: on). Off: an activation raises none of its
   *  notices about kept folders, kept files and notes on mods, except the first one about a folder that may still run
   *  although its mod is off (shell/notices.ts). */
  modFolderNotes?: boolean;
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
  /** Another data folder's profile is active in the game (an older launcher after a move, KILN_DATA_DIR, another
   *  launcher): said, never shown as loaded; `activeProfile` is null then. `root`: the data folder its paths name. */
  otherFolderProfile?: { id: string; root?: string | null };
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
  /** Every component with its sides and whether it is a default (kiln_manage::bundle::BundlePart). */
  parts: BundlePart[];
}
export interface BundlePart {
  name: string;
  sides: RunSide[];
  default: boolean;
  requires?: string[];
  description?: string;
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
  /** `deselected`: default parts the player turned off. Requirements may install them without clearing the opt-out;
   *  updates add other defaults, including new and newly default parts. Absent in older receipts means nothing turned off. */
  runtime: { version: string; sha256?: string; components: string[]; deselected?: string[] };
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
  restoredShroudforge?: string[];
  warnings?: string[];
  /** runtime_install and runtime_rollback: the profile the launcher remembers for the game, made active again. */
  resumedProfile?: string | null;
  /** ... and its activation (what it kept in the game folder). */
  activation?: Activation | null;
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
  /** Parts absent from the receipt that a reinstall adds: defaults not turned off, plus required dependencies.
   *  Absent from older backends. */
  missingParts?: string[];
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
  /** `dropins` names the engine kinds too (dev.22). Absent (older profiles): all four Kiln kinds on means every kind.
   *  pages/profiles/dropins.ts reads and writes both. */
  dropinsEngines?: boolean;
  dataDir: "shared" | "profile";
  server: BoundServer | null;
  /** Native mod ids whose dllmain DLLs load after Kiln and ShroudForge (chain.ini [late]); absent when none. */
  late?: string[];
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
  /** Other engines' own loaders installed in this game (own_loaders.rs); absent or empty: none. Each `text` is one
   * sentence for the Play and profile pages. */
  ownLoaders?: OwnLoaderNote[];
  /** Files mods wrote into their placed folders (their settings) that activation moved to `kiln/kept-mod-files` or put
   * back from there (engine_mods.rs PlaceReport::kept_files): one sentence per mod. Absent or empty: none. */
  keptFiles?: string[];
  /** Safety repairs applied only to the game-side profile (Library sources and saved profiles are unchanged), and
   * per-mod notes of the placement (a ShroudForge setting a new version refuses, earlier settings that cannot be read). */
  warnings?: string[];
  /** Placed copies the activation left in the game folder although the profile switched them off, removed them or
   * wants another version (their code changed, or Kiln cannot inspect or carry what is in them): the objects
   * profile_deactivate and uninstall return. Their own loader may still run them. Absent or empty: none. */
  retained?: RetainedPlacement[];
  /** The sentences of `warnings` about a player's own folder that may still run although the profile has its mod off
   *  (dev.26): raised even with "Show notes about mod folders" off. Absent or empty: none. */
  safetyNotes?: string[];
}
/** An engine's own loader next to the game (EML's dbghelp.dll, a chained Flame loader...) and what it means for the
 * profile's mods of that engine. While it runs, it runs them only from its own folder. */
export interface OwnLoaderNote {
  kind: ModKind;
  /** The engine the loader file belongs to (ShroudForge's loader runs EML mods too): "eml", "shroudforge", "flame"... */
  engine: string;
  /** Game-relative file: "dbghelp.dll", "kiln/chain/flame-dinput8.dll", "kiln/plugins/kiln-shroudforge.dll". */
  file: string;
  /** proxy: under a proxy name next to the game (on Linux it runs only with a DLL override); chain: kiln/chain.ini;
   * emf-chain: dinput8-chain.ini; hosted-shroud-forge: Kiln hosts ShroudForge for this profile. */
  via: "proxy" | "chain" | "emf-chain" | "hosted-shroud-forge";
  /** Game-relative folder the loader reads ("mods", "emf/mods"). */
  folder: string;
  /** placed: the launcher copies the profile's mods there; plugin: Kiln's plugin does; none: nothing can. */
  delivery: "placed" | "plugin" | "none";
  /** The profile's enabled mods of this kind. */
  enabled: number;
  /** Ids with Kiln's copy in the loader's folder (they run under it). */
  copied: string[];
  /** Ids not in that folder: they do not load while the loader runs. */
  notCopied: string[];
  /** "EML's own loader (dbghelp.dll) is installed: it runs the EML mods in mods/; Kiln copied this profile's 3 EML mod(s) into mods/ for it." */
  text: string;
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
  /** The main menu's load order and why the others wait or stay out (a client's server mods: `withWorld`). */
  kiln: Resolution;
  native: NativePlanEntry[];
  warnings: string[];
  /** Client: the Kiln mods that start with the player's own world (single-player, or friends joining it) instead of in
   *  the menu: side "server" mods and the mods that need one, in the order they load then. Each is also in
   *  `kiln.rejected`, skipped, with the reason "Starts with your own world, ...". Empty on a server and when the game's
   *  kiln.json has `"mods": {"hosted": false}` (then those mods say so). */
  withWorld: string[];
}

// library
export interface LibraryRef {
  kind: ModKind;
  id: string;
  version: string;
  hash: string;
}
/** library_remove_many: the versions removed, and each one that was not (conflict without force, not_found, io). */
export interface RemoveReport {
  removed: LibraryRef[];
  failed: { entry: LibraryRef; error: KilnError }[];
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
  /** The id the mod's own manifest gives, when the Library id is not that (an engine mod such as "Hoarders Helper":
   *  the Library id is its Kiln-id slug). Engine plugins report their state rows under this id. */
  manifestId: string | null;
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
  | "eml" | "xhl" | "shroudforge" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame" | "shroudlink" | "ember"
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
  /** Left out, with a reason: what happened and, when the player has to do something, what. No final period. */
  skipped: { path: string; reason: string }[];
  /** Left out because it is not needed (a loader the package carries); each text once. */
  notes: { path: string; text: string }[];
  /** The display name of every entry in `imported` and `existing`, by `<kind>/<id>`. */
  names: Record<string, string>;
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
  /** The game had no active profile (Kiln was reinstalled): `profileId` was active here before, and is again. */
  resumed: boolean;
  /** The mods reached the Library only, and why in plain words; null when they went into a profile. The player can
   *  pick a profile (library_add_to_profile). */
  libraryOnly: string | null;
  /** Modpack zips among the paths: not imported, they go through the consent flow (modpack_install). */
  modpacks: string[];
  /** What the add did in the profile besides adding, in words for the player (quick::merge): another mod's entry
   *  replaced (Kiln reads the two as one mod), a second copy of the same proxy DLL switched off, an XHL-Core build kept.
   *  Shown until closed, with the add (shell/notices.ts notifyAdd). */
  notices: string[];
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
  /** Mode profile: the activation when the active profile had changed and was written again before the start. */
  activation?: Activation;
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
    counts?: Partial<Record<ModKind, number>>;
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
/** What kiln-eml is doing before the game's window exists (kiln/state/eml.json, phase running): `launch_prestart`. */
export interface PreStart {
  /** starting | checking | prepare | mods | writing | saving */
  stage: string;
  label: string;
  doneBytes: number;
  totalBytes: number;
  filesDone: number;
  filesTotal: number;
  /** The file being read now. */
  file?: string | null;
  /** Milliseconds since a byte was last read (while a file is being read). */
  quietMs: number;
  /** The sentence for the launch status ("Preparing the game data for EML mods: 12.3 GiB of 48.6 GiB. ..."). */
  message: string;
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
/** The first screen is up (once per start: shell/started.ts). */
export const appStarted = () => call<null>("app_started");
/** Settings, "Draw the launcher without the GPU": sets or clears the marker for the next start (Linux). */
export const appRenderSet = (software: boolean) => call<RenderInfo>("app_render_set", { software });
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
/** A placed mod folder Kiln left where it is (engine_mods.rs RetainedPlacement): `mayRun`, its own loader may still run
 * it whatever the profile says. */
export interface RetainedPlacement {
  path: string;
  reason: string;
  mayRun: boolean;
  /** The profile has the mod off, or no longer has it (absent: on, or an older backend). */
  off?: boolean;
}
export interface PlacementCleanup {
  removed: string[];
  retained: RetainedPlacement[];
  /** Files the mods wrote into removed copies, now in the game's kiln/kept-mod-files: one sentence per mod. Absent: none. */
  keptFiles?: string[];
}
export interface UninstallResult {
  removed: number;
  restored: string[];
  adopted: string[];
  kept: string[];
  placements: PlacementCleanup;
  warnings: string[];
}
/** Task result: UninstallResult, including retained fixed-folder mods and their recovery instructions. */
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
/** `gameId`: the game the preview is for (its kiln.json `mods.hosted`); null: Kiln's default. */
export const profileResolve = (profileId: ProfileId, side: RunSide, gameId: GameId | null = null) =>
  call<ResolvePreview>("profile_resolve", { profileId, side, gameId });
export const profileActivate = (profileId: ProfileId, gameId: GameId) => call<Activation>("profile_activate", { profileId, gameId });
export const profileDeactivate = (gameId: GameId) => call<PlacementCleanup>("profile_deactivate", { gameId });
export const profileActive = (gameId: GameId) => call<Activation | null>("profile_active", { gameId });
/** The activations of re-applies the window has not shown yet (after a profile edit, a change outside the launcher, the
 *  watcher's start-up pass); each is returned once. Taken when `kiln://activation` arrives and once at start. */
export const activationNotices = () => call<Activation[]>("activation_notices");
/** One sentence of an activation's notices; `safety`: a folder that may still run although its mod is off. */
export interface NoticeLine {
  text: string;
  safety?: boolean;
}
/** The lines to raise now for this game: those not shown before (notices_shown), and with "Show notes about mod
 *  folders" off only the `safety` ones. Asking remembers nothing. */
export const noticesUnseen = (gameId: GameId, lines: NoticeLine[]) => call<string[]>("notices_unseen", { gameId, lines });
/** These lines (their `text`) are on screen now: not raised again for this game (kept with the data folder,
 *  installs/<id>.notices-seen.json). */
export const noticesShown = (gameId: GameId, lines: string[]) => call<null>("notices_shown", { gameId, lines });
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
  /** ShroudForge: `file` is in the player's own folder (one Kiln did not place). Kiln writes only a setting changed
   *  here into it, and never switches, replaces or removes it. Absent: Kiln's copy or the Library's defaults. */
  ownFolder?: boolean;
}
/** SF's own runtime report, not the adapter's hosted state. Only current reports have mod observations. */
export interface SfEffects {
  file: string;
  state: "current" | "stale" | "stopped" | "unavailable" | "invalid";
  updatedAt: number | null;
  reason: string | null;
  mods: { id: string; state: string; detail: string }[];
}
/** One engine plugin's state file `kiln/state/<engine>.json` (null: not written yet). */
export interface EngineStatus {
  kind: ModKind;
  plugin: string;
  state: Json | null;
  file: string;
  /** A present state file could not be read or parsed. */
  error?: string | null;
  /** This engine's own loader, when installed, and what it means for the active profile's mods of the kind. */
  ownLoader?: OwnLoaderNote | null;
  /** Only ShroudForge: per-mod effect/error details, with heartbeat freshness checked by the backend. */
  effects?: SfEffects | null;
  /** External-host presence only, not a successful runtime/gameplay report. */
  prerequisite?: { available: boolean; text: string } | null;
}
/** Shared `.kfc.bak`; `patched` means the `.kfc` differs, not that EML owns it. */
export interface EmlBackup {
  file: string;
  backup: string;
  patched: boolean;
}
export const engineSettingsGet = (kind: ModKind, id: string, profileId: ProfileId, gameId: GameId | null = null) =>
  call<EngineSettings>("engine_settings_get", { kind, id, profileId, gameId });
/** One top-level key of a profile entry; refuses while a game reading these files runs. ShroudForge requires the
 *  profile active in the selected game and a Kiln-owned placed copy; unsupported for kinds without settings. */
export const engineSettingsSet = (kind: ModKind, id: string, profileId: ProfileId, gameId: GameId | null, key: string, value: Json) =>
  call<EngineSettings>("engine_settings_set", { kind, id, profileId, gameId, key, value });
export const engineStatus = (gameId: GameId) => call<EngineStatus[]>("engine_status", { gameId });
export const emlBackup = (gameId: GameId) => call<EmlBackup[]>("eml_backup", { gameId });
/** Legacy command: refuses direct shared-backup restores; updated Kiln's archive writer verifies ownership on launch. */
export const emlRestore = (gameId: GameId) => call<string[]>("eml_restore", { gameId });

// compat.rs (kiln_manage::compat): closed DLL mods on a profile's native page
export type ChainSection = "chain" | "profile";
export interface ChainViewEntry {
  /** As in chain.ini ('\', relative to <game>/kiln/ unless absolute). */
  path: string;
  file: string;
  section: ChainSection;
  /** [chain]: the engine word its markers name. */
  engine: string | null;
  /** [profile]: the native mod's id. */
  modId: string | null;
  late: boolean;
  /** "player" ([chain], game-wide), "profile", or "rule:<id>" (a rule of this profile, or one that wrote the chain.ini
   *  [late] line, which is then not the player's to switch off). */
  lateBy: string | null;
  /** "rule:<id>" when a compat rule switches it off. */
  offBy: string | null;
  /** EMF's loader: it starts EMF itself, which Kiln's EMF support has to see when it starts, so Kiln always loads it
   *  first and the late switch is not offered. */
  loader: boolean;
  /** shroudlink's client addon (compat::is_gated_addon): it has to find the game's functions before Kiln hooks them,
   *  so it is never switched late; one already late (an older Kiln allowed it) can be switched back. */
  gated?: boolean;
}
export type RuleAction = "warn" | "late" | "disable";
export interface CompatRuleResult {
  id: string;
  source: "kiln" | "user";
  action: RuleAction;
  message: string;
  fired: boolean;
  applied: boolean;
  targets: string[];
  note?: string;
}
export type OverlapKind = "foreign-before" | "foreign-over" | "refused" | "other";
export interface ChainOverlap {
  /** Empty for a target without a sigdb name and for a refused hook (kiln-core writes null; the launcher reads it as ""). */
  symbol: string;
  rva: string;
  /** refused: the hosted engine's name for the hook ("game-thread"), which labels a row without a symbol. */
  hook?: string | null;
  kind: OverlapKind;
  foreign: { module: string; offset: string; own?: boolean } | null;
  owners: string[];
  /** The chain entry that loads the foreign module (null: another loader loaded it). */
  entry: string | null;
  section: ChainSection | null;
  library: { kind: ModKind; id: string; name: string } | null;
  late: boolean;
  /** "late": one click loads `entry` late; "none": only the hint. */
  fix: "late" | "none";
  hint: string;
}
export interface ChainView {
  entries: ChainViewEntry[];
  rules: CompatRuleResult[];
  ruleErrors: string[];
  /** null: no hooks.json yet (the game has not started with this Kiln). */
  session: { generated: string | null; phase: string | null; kiln: string | null; worldChecked: boolean } | null;
  overlaps: ChainOverlap[];
  active: boolean;
  /** Active, and chain.ini already says what the plan says. */
  current: boolean;
  error: string | null;
}
export const chainView = (profileId: ProfileId, gameId: GameId) => call<ChainView>("chain_view", { profileId, gameId });
/** Loads one chain entry after Kiln and ShroudForge (or not): a [profile] entry changes the profile, a [chain] entry
 *  the game's chain.ini for every profile. Answers the fresh view. */
export const chainSetLate = (profileId: ProfileId, gameId: GameId, path: string, late: boolean) =>
  call<ChainView>("chain_set_late", { profileId, gameId, path, late });

// found.rs (kiln_manage::found): mods in the game folder that Kiln did not put there
/** loads: at the next start with this profile (`via`); off: not with this profile (`reason`, `fix`); inProfile: the
 *  profile has the same mod and its copy wins; cannotRun: nothing Kiln can run. */
export type FoundState = "loads" | "off" | "inProfile" | "cannotRun";
export interface FoundMod {
  kind: ModKind | null;
  name: string;
  id: string | null;
  version: string | null;
  /** Relative to the game folder, with "/". */
  path: string;
  state: FoundState;
  via?: "kiln" | "ownLoader";
  /** One plain sentence. */
  reason: string;
  /** dropins: switch on the profile's drop-in folder of `kind`; add: found_add. */
  fix?: "dropins" | "add";
  profileMod?: string;
}
export interface OwnLoader { engine: string; name: string; kinds: ModKind[]; file: string }
export interface FoundReport {
  gameId: GameId;
  profileId: ProfileId;
  kilnInstalled: boolean;
  mods: FoundMod[];
  loaders: OwnLoader[];
  unreadable: { path: string; reason: string }[];
  truncated: boolean;
}
export const foundScan = (gameId: GameId, profileId: ProfileId) => call<FoundReport>("found_scan", { gameId, profileId });
/** Task result: AddReport (`import.skipped` says why the Library refused it). */
export const foundAdd = (gameId: GameId, profileId: ProfileId, path: string) => call<TaskId>("found_add", { gameId, profileId, path });

// library.rs
export const libraryList = (kind: ModKind | null = null) => call<LibraryEntry[]>("library_list", { kind });
export const libraryDetect = (path: string) => call<Detected>("library_detect", { path });
/** Task result: ImportReport. */
export const libraryImport = (paths: string[]) => call<TaskId>("library_import", { paths });
/** Task result: AddReport. Import + add to the game's active profile ("My mods" for a first-time player) + write the
 *  game's profile.json, in one task. Rejects at once (unsupported) when Kiln is not in the game. */
export const libraryAdd = (gameId: GameId, paths: string[]) => call<TaskId>("library_add", { gameId, paths });
/** Puts library versions into the profile the player picked (after an add that said `libraryOnly`); the result is
 *  library_add's with an empty `import`, so Undo is profile_revert. */
export const libraryAddToProfile = (gameId: GameId, profileId: ProfileId, entries: LibraryRef[]) =>
  call<AddReport>("library_add_to_profile", { gameId, profileId, entries });
/** Rejects with game_running while a game runs, or a server whose profile file points into the library, even with force (it may be reading the mod).
 *  `shown`: the libraryUsers answer a confirmation named. Removal goes past exactly those; any other user is a conflict, whatever `force` says. */
export const libraryRemove = (entry: LibraryRef, force = false, shown?: string[]) => call<null>("library_remove", { entry, force, shown: shown ?? null });
/** Clean up: one look at the running processes and the profiles for the whole batch. Rejects with game_running (nothing
 *  removed) while a game runs, or a server may read one of the versions; otherwise reports the versions not removed. */
export const libraryRemoveMany = (entries: LibraryRef[], force = false) => call<RemoveReport>("library_remove_many", { entries, force });
/** The versions Clean up may remove: in no profile, not in any known game's active profile file, and not in a
 *  play-once file a start can still read. */
export const libraryUnused = () => call<LibraryRef[]>("library_unused");
/** What library_remove refuses (conflict) over without force: profile ids, and "<profile> (active in <game>)" /
 *  "(play-once in <game>)" for each game whose profile file points at the version. */
export const libraryUsers = (entry: LibraryRef) => call<string[]>("library_users", { entry });
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
export const launchPrestart = (gameId: GameId) => call<PreStart | null>("launch_prestart", { gameId });

// logs.rs
export const logsList = (target: LogTarget) => call<LogFile[]>("logs_list", { target });
export const logsRead = (target: LogTarget, file: string, offset: number | null = null, maxBytes: number | null = null) =>
  call<LogChunk>("logs_read", { target, file, offset, maxBytes });
/** Returns a subscription id; lines arrive as kiln://log events. */
export const logsFollow = (target: LogTarget, file: string) => call<string>("logs_follow", { target, file });
export const logsUnfollow = (subscription: string) => call<null>("logs_unfollow", { subscription });
export const logsCrashReports = (target: LogTarget) => call<CrashReport[]>("logs_crash_reports", { target });
/** kiln_manage::report::Finding: one check that explains a common failure. */
export interface ReportFinding {
  id: string;
  level: "problem" | "warning" | "info";
  text: string;
}
/** report_save's answer (launcher type). */
export interface ReportSaved {
  path: string;
  size: number;
  findings: ReportFinding[];
  /** Sections that could not be read; the rest is in the zip. */
  errors: string[];
}
/** Writes the support report zip (logs, state files, profile, listings, summary.txt; names and paths replaced) to
 *  `path`. `target` null: the system and the launcher only. */
export const reportSave = (target: LogTarget | null, path: string) => call<ReportSaved>("report_save", { target, path });
/** A few lines for a forum post, redacted like the zip. */
export const reportSummary = (target: LogTarget | null) => call<string>("report_summary", { target });

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

// data_folder.rs: where the launcher keeps its data (one folder, moved as one; Settings' "Data folder" row). The
// data folder in use is fixed for the life of the process: after a move, a "use" or a reset the launcher restarts
// (data_folder_restart), and that start removes the old copy and reports it (`notice`).
/** Where the data folder comes from: `env` ($KILN_DATA_DIR; the launcher cannot change it), `pointer` (the file at the
 *  default place that names a folder the player chose), `default`. */
export type DataFolderSource = "env" | "pointer" | "default";
export interface DataFolder {
  /** The data folder in use (the default place when the pointer is broken). */
  path: string;
  /** The default place for this PC. */
  defaultPath: string;
  source: DataFolderSource;
  /** The pointer names a folder that cannot be used: every other command fails (code `data_folder`) until the player
   *  picks a folder (data_folder_use), starts fresh (data_folder_reset) or quits. `kind`: the crate's ProblemKind. */
  problem: { path: string; reason: string; kind?: string } | null;
  /** The start after a move (or one that undid an unfinished move): until data_folder_notice_ack. */
  notice: DataFolderNotice | null;
  /** Another launcher holds the folder's lock exclusively (it is moving the data): only Quit. Absent: false. */
  elsewhere?: boolean;
  /** An unfinished move's partial copy is being removed now (the folder is held): only its dialog, with the task's
   *  progress and Cancel. `task`: the task "Remove the unfinished copy" (null for the moment before it starts). */
  undoing?: { task: string | null; folder: string } | null;
  /** Why the folder cannot be moved now (an earlier move is not finished): the Settings row says it instead of offering
   *  the folder dialog. */
  pending?: string | null;
}
/** An entry of the old folder, with its size and where it is. */
export interface DataFolderEntry {
  name: string;
  bytes: number;
  path: string;
}
export interface DataFolderNotice {
  /** moved: a move finished; undone: a move that did not finish was undone at this start; unclear: the move's record
   *  matches neither folder, nothing was removed (`reason` says where things are). Absent: moved. */
  kind?: "moved" | "undone" | "unclear";
  from: string;
  to: string;
  /** Every path still in the old folder (`stayed` and `kept`). */
  left: string[];
  /** Entries the launcher does not know: copied, and left in the old folder on purpose (Kiln did not remove them). */
  stayed?: DataFolderEntry[];
  /** What could not be removed, and why (a file that changed since the move, a game's profile naming the old folder). */
  kept?: { path: string; bytes: number; reason: string }[];
  /** The removal of the old copy runs now (the task "Remove the old data folder"). */
  removing?: boolean;
  /** The removal stopped before the end (canceled, or the launcher closed): it goes on at the next start. */
  unfinished?: boolean;
  /** The old folder itself is gone. */
  folderRemoved?: boolean;
  /** Why nothing was removed, and where the folder is. */
  reason?: string | null;
}
export interface DataFolderSize {
  bytes: number;
  files: number;
}
/** What data_folder_check found for a folder the player picked. */
export interface DataFolderPlan {
  picked: string;
  /** The folder the data would be in (`<picked>/Kiln` when the picked folder has other files in it). */
  target: string;
  /** move: copy the data there; use: it already is a Kiln data folder (no merge); refuse: `reason` says why not. */
  action: "move" | "use" | "refuse";
  /** One sentence; set for "refuse" (the crate's). */
  reason: string | null;
  /** The refusal's stable code (kiln_manage::data_folder::RefusalCode: "game_running", "too_small", ...). */
  code?: string | null;
  /** What moves. */
  bytes: number;
  files: number;
  /** What the target needs free. */
  needed?: number;
  /** Free space at the target (null: unknown). */
  free: number | null;
  /** On the current folder's volume (null: unknown). */
  sameVolume: boolean | null;
  /** Hard links from the current folder work there (a move needs no space then). */
  hardLinks?: boolean;
  /** Games (by name) whose mods would be copied into their game folder from there, and are not today. */
  copyMode: string[];
  /** Entries the launcher does not know: copied, and also left in the old folder (Kiln does not remove them). */
  stays: DataFolderEntry[];
  /** What will not work from there, one sentence each (the crate's). */
  warnings?: string[];
}
/** data_folder_move's task result. */
export interface DataFolderMoved {
  from: string;
  to: string;
  warnings: string[];
  /** Entries copied and left in the old folder. */
  stayed?: DataFolderEntry[];
  /** The removal of the old copy, the move's last phase: `interrupted` (a cancel then) leaves the rest for the next
   *  start; the move itself is done. */
  cleanup?: { done: boolean; interrupted: boolean; folderRemoved: boolean; kept: { path: string; bytes: number; reason: string }[]; reason?: string | null } | null;
}
export const dataFolderGet = () => call<DataFolder>("data_folder_get");
export const dataFolderSize = () => call<DataFolderSize>("data_folder_size");
export const dataFolderCheck = (path: string) => call<DataFolderPlan>("data_folder_check", { path });
/** Task result: DataFolderMoved. `path`: the folder the player picked (the backend checks it again). */
export const dataFolderMove = (path: string) => call<TaskId>("data_folder_move", { path });
/** Switches to a folder that already is a Kiln data folder (the current one stays where it is). */
export const dataFolderUse = (path: string) => call<DataFolder>("data_folder_use", { path });
/** Starts fresh at the default place; only for a broken pointer. */
export const dataFolderReset = () => call<DataFolder>("data_folder_reset");
export const dataFolderNoticeAck = () => call<null>("data_folder_notice_ack");
/** Restarts the launcher; the promise does not resolve (it rejects if the restart could not start). */
export const dataFolderRestart = () => call<null>("data_folder_restart");

/** Every command name (the Rust side registers the same list; mock.ts answers or rejects each). */
export const COMMANDS = [
  "app_info", "app_settings_get", "app_settings_set", "app_open_path", "app_open_url", "task_list", "task_get", "task_cancel",
  "game_list", "game_detect", "game_add", "game_remove", "game_status",
  "runtime_list", "runtime_import", "runtime_status", "runtime_install", "runtime_uninstall", "runtime_rollback", "runtime_classify",
  "profile_list", "profile_get", "profile_create", "profile_save", "profile_delete", "profile_resolve", "profile_activate",
  "profile_deactivate", "profile_active", "activation_notices", "notices_unseen", "notices_shown",
  "library_list", "library_detect", "library_import", "library_remove", "library_remove_many", "library_unused", "library_users", "library_lint",
  "config_list", "config_read", "config_set", "config_write", "config_reset", "config_native_state", "config_native_set",
  "launch_play", "launch_state", "launch_prestart",
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
  "library_add", "library_add_to_profile", "profile_revert", "profile_ensure_default",
  "deck_browse", "deck_gamepad", "deck_fullscreen", "deck_quit",
  "nexus_account", "nexus_sign_in", "nexus_sign_out", "nexus_search", "nexus_mod", "nexus_installed", "nexus_download", "nexus_links", "nexus_link_install", "nexus_link_dismiss", "nexus_handler_status", "nexus_handler_set",
  "engine_settings_get", "engine_settings_set", "engine_status", "eml_backup", "eml_restore",
  "chain_view", "chain_set_late",
  "found_scan", "found_add",
  "report_save", "report_summary",
  "data_folder_get", "data_folder_size", "data_folder_check", "data_folder_move", "data_folder_use", "data_folder_reset",
  "data_folder_notice_ack", "data_folder_restart",
  "app_started", "app_render_set",
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
  /** A profile was written again without profile_activate and its notices wait: take them with activation_notices. */
  "kiln://activation": { gameId: GameId };
}
export type KilnEventName = keyof KilnEvents;

/** Subscribes to a launcher event; resolves to the unsubscribe function. */
export async function onKilnEvent<E extends KilnEventName>(name: E, handler: (payload: KilnEvents[E]) => void): Promise<() => void> {
  if (isTauri()) return tauriListen<KilnEvents[E]>(name, (e) => handler(e.payload));
  return (await loadMock()).mockListen(name, handler as (payload: unknown) => void);
}
