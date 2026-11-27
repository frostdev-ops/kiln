// Typed wrappers for every Tauri command of the Kiln Launcher.
// Contract: docs/kiln/launcher/contracts.md sections 9-11. Command names and argument shapes are stable; change them
// together with the contract. Pages and stores import from here, never call `invoke` directly.
// Types mirror crates/kiln-manage (serde camelCase) and launcher/src-tauri/src/commands/*.rs.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { listen as tauriListen } from "@tauri-apps/api/event";
import { t } from "../i18n";
import { canFallBack, noteFallback } from "./fallback";
import { loadMock } from "./mockLoad";
import { oneActivation, profileSwitched } from "./oneActivation";
import { toKilnError, type Json, type KilnError, type PlacementCleanup, type RetainedPlacement, type SchemaSnapshot, type ServerId, type ValidationError } from "./shared";

// The basics the admin pages' package (@kiln/admin-ui) shares with this layer live in ./shared.ts (Tauri-free).
export { isKilnError, toKilnError } from "./shared";
export type { ErrorCode, Json, KilnError, PlacementCleanup, RetainedPlacement, SchemaSnapshot, ServerId, SnapshotEntry, ValidationError } from "./shared";

// ---------------------------------------------------------------- plumbing

/** The browser mock: a chunk of its own in a build (./mockLoad.ts). */
export { loadMock };

/** True inside the Tauri webview; false in a plain browser (the mock layer answers then). */
export const isTauri = (): boolean => typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

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
export type TaskId = string;
/** kiln_manage::manifest::ModKind: Kiln's own four, then other engines' mods, each imported by the Library and run by
 *  Kiln's plugin for that engine (XHL companions require external XHL-Core; Ember's are held but don't run yet: no published mod ABI). Labels and helpers:
 *  ./kinds.ts. */
export type ModKind = "kiln" | "native" | "plugin" | "emf"
  | "eml" | "xhl" | "shroudforge" | "set" | "greeneye" | "flame" | "shroudtopia"
  | "enshwick" | "shroudlink" | "ember" | "dllmod";
export type Side = "server" | "client" | "both";
export type RunSide = "server" | "client";
export type Clients = "none" | "kiln" | "mod";
export type LogTarget = { game: GameId } | { server: ServerId };
export type ConfigTarget = { profile: ProfileId } | { server: ServerId } | { game: GameId };

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
  /** Linux AppImage only: the app menu entry Kiln writes so taskbars and docks show its icon (desktop.rs). */
  desktop?: DesktopInfo;
  /** (dev.29.18) Linux only: WebKit's sandbox in this start and whether the next start turns it off (sandbox.rs). */
  sandbox?: SandboxInfo;
  /** (dev.29.18) A Windows install's own files gone from the launcher's folder at this start (its exe, `uninstall.exe`:
   *  an antivirus took them; own_files.rs). Absent while none is, and from older backends. */
  ownFilesMissing?: string[];
  /** (dev.29) The previews on for this start (settings.json `preview`, KILN_LAUNCHER_PREVIEW). None uses it in
   *  0.2.0-dev.29.18 (the upscaler files are out of preview). Absent: none. */
  preview?: string[];
}
/** WebKit's process sandbox on Linux (src-tauri/src/sandbox.rs). */
export interface SandboxInfo {
  /** This start's choice: on (the launcher leaves WebKit as it is) or off (it sets WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS). */
  want: "on" | "off";
  /** Why: nothing asked; the marker (Settings, or a message's button at an earlier start); the restart after such a
   *  button; KILN_LAUNCHER_WEBKIT_SANDBOX; --no-webkit-sandbox / --webkit-sandbox. */
  why: "default" | "marker" | "restart" | "environment" | "argument";
  /** The marker is there: a start without KILN_LAUNCHER_WEBKIT_SANDBOX or an argument turns the sandbox off. */
  marker: boolean;
  /** Who set the marker, why and when. */
  markerWhy?: string;
  /** What WebKit does in this start: runs its sandbox, has it turned off, or does not use it (its GTK 3 API runs it
   *  only when WEBKIT_FORCE_SANDBOX=1 is set). */
  webkit: "on" | "off" | "unused";
  /** Why, in the backend's words. */
  webkitWhy: string;
  /** The player's own WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS value: it decides, the switch does not. */
  yours?: string;
  /** The probe of the bwrap WebKit runs: "works", "fails", "missing" or "unknown", and its sentence. */
  probe: "works" | "fails" | "missing" | "unknown";
  probeSays: string;
  /** bwrap cannot start here and that says something about this system (not the AppImage's own missing bwrap while the
   *  start does not run the sandbox): Settings shows probeSays. */
  probeFailed?: boolean;
}
/** The AppImage's app menu entry (src-tauri/src/desktop.rs). */
export interface DesktopInfo {
  /** The player's choice: on unless turned off in Settings. */
  on: boolean;
  /** Kiln's entry is there and starts this AppImage. */
  installed: boolean;
  /** A kiln-launcher.desktop Kiln did not write is there; turning the switch on replaces it. */
  foreign: boolean;
  /** The entry's path. */
  file: string;
}
/** How the Linux launcher draws its window (src-tauri/src/render.rs). */
/** `app_render_report` (src-tauri render.rs PageRender): every field optional, each call sends what it has. */
export interface PageRender {
  /** (-webkit-transform-3d): WebKit composites (Stage.tsx flatDrawing is its negation). */
  composited?: boolean;
  /** WebGL's version and renderer, or "none". */
  webgl?: string;
  dpr?: number;
  /** The window's CSS size. */
  width?: number;
  height?: number;
  reducedMotion?: boolean;
  /** The fire burns (WebGL, Animated fire on). */
  fire?: boolean;
  frames?: FrameSample;
}
/** requestAnimationFrame over a stretch of Play (ms): `slow` frames came more than 50 ms after the one before. */
export interface FrameSample { ms: number; frames: number; p50: number; p95: number; max: number; slow: number }

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
  /** X11 (XWayland) in this start: only on a Wayland session with XWayland (render.rs, 0.2.0-dev.30). */
  x11?: X11Info;
  /** Settings' "Force GPU drawing on NVIDIA" and this start's step past Ubuntu WebKit's NVIDIA block (render.rs,
   *  0.2.0-dev.30.7). */
  force?: ForceInfo;
}
export interface ForceInfo {
  /** Effective switch: manual choice or remembered automatic NVIDIA recovery; off on clean starts. */
  on: boolean;
  /** This start: nothing forced, or WEBKIT_FORCE_DMABUF_RENDERER=1 with WEBKIT_DMABUF_RENDERER_FORCE_SHM=1. */
  step: "off" | "shm";
  /** Why: the switch off or on; the start before with it failed (the launcher turns the switch off); the user's own
   *  variables; no DMA-BUF renderer in this start (software mode, its off switch). */
  why: "off" | "on" | "failed" | "recovery" | "recovered" | "unguarded" | "yours" | "unused";
  /** Why the launcher turned the switch off by itself, and when. */
  failedWhy?: string;
  /** This start found Ubuntu WebKit's NVIDIA block behind WebKit drawing without the GPU. */
  blocked: boolean;
}
export interface X11Info {
  /** This start draws through X11 (the launcher set GDK_BACKEND=x11). */
  on: boolean;
  /** Why: GTK's default (Wayland), the user's GDK_BACKEND, software mode, Settings off, the switch's restart, the
   *  marker of a start where X11 helped, X11 tried without help. */
  why: "default" | "yours" | "unavailable" | "software" | "off" | "restart" | "marker" | "tried";
  /** Settings' switch: the launcher may switch to X11 by itself. */
  auto: boolean;
  /** The user's own GDK_BACKEND. */
  yours?: string;
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
  /** Absent or null: the channel of the running build (a dev build follows dev, a test build beta). */
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
  /** "Check for mod updates once a day" (default off; api/updates.ts). */
  modUpdatesDaily?: boolean;
  /** (dev.30) "Count my installs (anonymous)": Crucible's anonymous daily install counts (default on; absent: on). */
  crucibleStats?: boolean;
  /** (dev.29) "Earlier versions to keep": how many earlier versions of a mod an update keeps for a roll back, with
   *  their settings (0 to KEEP_MAX; default 1; absent from an older backend). */
  keepVersions?: number;
  /** The launcher's language (Settings, Language; src/i18n): "system" or absent = the system's, else a tag ("zh-CN"). */
  language?: string | null;
  /** (dev.29) Previews of unfinished features, set by hand in settings.json; kept as it is by every save. */
  preview?: string[];
  /** (dev.30) The folder each kind of file dialog was last used in (ui/dialogFolder.ts); only app_dialog_remember
   *  changes it (a settings save keeps the file's). */
  dialogFolders?: Record<string, string>;
}
/** The most `keepVersions` may be (kiln_manage::kept::KEEP_MAX). */
export const KEEP_MAX = 5;
export type TaskState = "queued" | "running" | "done" | "failed" | "canceled";
export interface Task<R = unknown> {
  id: TaskId;
  kind: string;
  title: string;
  state: TaskState;
  stage?: string | null;
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
  chainedOverrideMissing?: boolean;
  advice: string | null;
  /** Explicit override to try after a real "No Kiln" failure, even on known-native runners. */
  fallbackAdvice?: string | null;
  dinput8NativeByRunner?: boolean;
}
/** A graphics tool in the game folder that runs on its own next to Kiln (kiln_manage::game::GraphicsToolNote). */
export interface GraphicsToolNote {
  engine: EngineId;
  /** The tool's file directly in the game folder ("dxgi.dll", "OptiScaler.asi"). */
  file: string;
  /** What to expect from Kiln's overlay next to it (English, from the launcher's backend). */
  text: string;
  /** Kiln's overlay draws next to it (OptiScaler's own menu is off); false: Kiln's drawing steps aside. */
  kilnOverlay: boolean;
}
/** (dev.29.18) A tool in the game folder next to which Kiln's graphics tools (kiln.json "graphics") step aside
 *  (kiln_manage::game::GraphicsStepAside). */
export interface GraphicsStepAside {
  /** "optiscaler", "streamline" (sl.interposer.dll), "vkfg" (EnshroudedVKFG.ini), "dlsstweaks" (dlsstweaks.ini),
   *  "nvngx" (nvngx.dll or _nvngx.dll: another DLSS loader) or (dev.29.18) "upscaler" (AMD's or Intel's upscaler DLLs
   *  next to the game: another tool's); a kind this launcher does not know reads as another tool. */
  kind: "optiscaler" | "streamline" | "vkfg" | "dlsstweaks" | "nvngx" | "upscaler" | (string & {});
  /** The tool's file relative to the game folder, forward slashes ("dxgi.dll", "Streamline/sl.interposer.dll"). */
  file: string;
  /** Kiln's frame cap steps aside too; false: only Kiln's DLSS settings do. The readout always works. */
  frameCap: boolean;
  /** (dev.29.18) Kiln's upscaler behind the game's FSR 3 option steps aside too. Absent from an older backend: false. */
  upscaler?: boolean;
}

/** (dev.29.18) kiln.json `graphics.upscaler`: what runs behind the game's FSR 3 option. */
export type UpscalerChoice = "game" | "amd" | "xess";
/** kiln.json `graphics.amdUpscaler`. */
export type AmdUpscalerMode = "auto" | "fsr3" | "fsr4";
/** What upscales behind the game's FSR 3 option (kiln_ui.h KILN_UI_UPSCALE_RUN_*; "unknown": a runtime that does not say). */
export type UpscalerRunning = "off" | "notFsr" | "gameFsr3" | "amdFsr4" | "amdFsr31" | "xess" | "unknown";
/** Why the chosen upscaler does not run (kiln_ui.h KILN_UI_UPSCALE_WHY_* in camelCase; docs/kiln/graphics.md); a code
 *  this launcher does not know reads as "unknown". */
export const UPSCALER_REASONS = ["restart", "filesMissing", "filesBad", "loadFailed", "device", "createFailed", "runtimeFailed", "fault", "aside",
  "noSignatures", "inputs", "unavailable", "waiting", "noVulkan"] as const;
export type UpscalerReason = (typeof UPSCALER_REASONS)[number] | "unknown";

/** (dev.29.18) `<game>/kiln/state/graphics.json` (Kiln.Ui; schema/state-graphics.schema.json, kiln_manage::state::GraphicsState). */
export interface GraphicsStateFile {
  format: 1;
  pid: number;
  written: string;
  kiln?: string | null;
  upscaler: {
    chosen: UpscalerChoice | (string & {});
    amdUpscaler: AmdUpscalerMode | (string & {});
    running: UpscalerRunning | (string & {});
    reason?: string | null;
    reasonNumber?: number;
    version?: string;
    hooked?: boolean;
    fsrSeen?: boolean;
    filesOk?: boolean;
    deviceOk?: boolean;
    fellBack?: boolean;
    amdPinned?: boolean;
    /** The tools it steps aside for (KILN_UI_GFX_ASIDE_* by name: "optiscaler", "upscaler", ...). */
    aside?: string[];
    /** kiln.ui's English detail of the reason (a file's or a Vulkan extension's name). */
    detail?: string;
  };
  /** Per vendor folder (amd, intel): this runtime's version and what of it is in kiln/vendor/. */
  files?: Record<string, { version: string; state: "present" | "missing" | "changed" | (string & {}) }>;
  frameCap?: unknown;
  dlss?: unknown;
}
/** GameStatus.graphicsState: the file, and whether the game that wrote it runs now ("Now"; false: "Last game"). */
export interface GraphicsNow {
  current: boolean;
  state: GraphicsStateFile;
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
  /** (dev.29.17) How this client starts: through Steam, or directly (a folder no Steam library knows). Absent for a server. */
  start?: StartMode;
  /** (dev.29.17) Whether Kiln knows the exe's build; absent when it cannot be told. */
  build?: BuildCheck;
  /** (dev.29.18) Graphics tools next to the game that change what Kiln's in-game overlay does (OptiScaler); absent or
   *  empty: none (kiln_manage::game::graphics_tools). */
  graphicsTools?: GraphicsToolNote[];
  /** (dev.29.18) Tools next to the game that make Kiln's frame cap and DLSS settings step aside (OptiScaler,
   *  Streamline, a DLSS frame generation mod, DLSSTweaks, another DLSS loader), in that order; absent or empty: none
   *  (kiln_manage::game::graphics_step_aside). */
  graphicsStepAside?: GraphicsStepAside[];
  /** (dev.29.18) kiln/state/graphics.json: what Kiln's graphics tools did in the game's last run (`current`: it runs
   *  now). Absent for a server and while there is none. */
  graphicsState?: GraphicsNow;
}
/** How an install starts (kiln_manage::direct). */
export type StartMode = "steam" | "direct";
/** What the player chose for an install's start; a field left out is Kiln's default. */
export interface StartSettings {
  mode?: StartMode;
  /** Linux: umu-run, a Proton folder (with its `proton` script) or a Wine binary; absent: automatic. */
  runner?: string;
  /** Linux: the prefix (Proton: the compatdata folder holding pfx; Wine: the prefix); absent: Kiln's own. */
  prefix?: string;
}
export interface RunnerInfo {
  /** "umu": umu-run (umu-launcher, Proton outside Steam); "proton": a Proton folder; "wine": a wine program. */
  kind: "umu" | "proton" | "wine";
  path: string;
  label: string;
  /** "compat" (compatibility tools: GE-Proton and others), "steam" (Valve's Proton in a Steam library, only when Steam
   *  is installed), "system" (umu-run or wine on PATH), "custom" (the player's path). */
  source: "steam" | "compat" | "system" | "custom";
  /** umu: the Proton folder it runs (PROTONPATH); absent: umu's own. */
  proton?: string;
}
export interface DirectCommand {
  program: string;
  args: string[];
  cwd: string;
  env: [string, string][];
  envRemove: string[];
  runner: RunnerInfo | null;
  prefix: string | null;
}
/** `game_start_get`: an install's start, what can be picked, and the command a direct start runs now. */
export interface StartSetup {
  mode: StartMode;
  settings: StartSettings;
  custom: boolean;
  windows: boolean;
  /** A Steam install was found on this PC: without one, "Through Steam" is not offered for a direct start. */
  steamFound: boolean;
  /** Every umu-run, Proton and Wine found, in the order "automatic" takes them. */
  runners: RunnerInfo[];
  runner: RunnerInfo | null;
  /** What "automatic" picks: umu-run when installed, else the newest Proton found, else Wine. */
  defaultRunner: RunnerInfo | null;
  problem?: string;
  ownPrefix: string | null;
  command: DirectCommand | null;
}
/** Whether Kiln knows the build of the game's exe (sigdb's build records by sha256). */
export interface BuildCheck {
  sha256: string;
  short: string;
  known: boolean;
  from: "game" | "bundle";
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
  | "eml" | "eml-r" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame" | "shroudlink" | "ember" | "dllmod" | "modloaderproxy"
  | "ultimateasiloader" | "aldrich" | "livemap" | "sharedxp" | "reshade" | "chainloader" | "nativefolders" | "optiscaler";
export interface ProxyClass {
  kind: ProxyKind;
  version: string | null;
  abi: number | null;
  sha256: string | null;
  size: number | null;
  detail: string | null;
  /** EML: a modified EML loader, with what its own code does (kiln_manage::engines::EmlExtras::what). */
  modified?: string | null;
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
  /** Receipt files changed since the install, missing ones included (game-relative, "/" separators). */
  modified: string[];
  /** (29.18) The files of `modified` that are not in the game folder at all: deleted, or removed or quarantined by an
   *  antivirus. A reinstall of the installed bundle (Runtime's Repair) puts them back. Absent from older backends. */
  missingFiles?: string[];
  upgradeAvailable: string | null;
  /** Parts absent from the receipt that a reinstall adds: defaults not turned off, plus required dependencies.
   *  Absent from older backends. */
  missingParts?: string[];
  /** (dev.29) The game's Kiln came with a test build of the launcher (its receipt's installer says beta) and this
   *  launcher ships an older one: after a switch back to the release, the next Play installs `to` (the launcher's own)
   *  in place of `from`. Absent from older backends and from kilnctl. */
  switchBack?: { from: string; to: string } | null;
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
  via: "proxy" | "chain" | "emf-chain" | "hosted-shroud-forge" | "native-module";
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
  /** Ids a folder of the player's own serves (in the loader's folder, not Kiln's copy). */
  own?: string[];
  /** Of those, the ones the hosted ShroudForge has switched off: activation does not change them. */
  ownOff?: string[];
  /** Ids whose folder of the player's own the loader that reads it refuses (the text says why and what to do). */
  ownRefused?: string[];
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
  /** It runs. With a `reason`, it runs from the game folder: Kiln places the package there and the game loads it. */
  enabled: boolean;
  /** Off: why it does not load. On: how it runs from the game folder. Null: Kiln loads it, in its phase. */
  reason: string | null;
}
export interface ResolvePreview {
  side: RunSide;
  /** The main menu's load order and why the others wait or stay out (a client's server mods: `withWorld`). */
  kiln: Resolution;
  /** Resolver-planned native compatibility uses the selected game's valid receipt runtime version; unbound or
   *  receipt fallback uses the launcher build. Game-folder placements have their own planning rules. */
  native: NativePlanEntry[];
  /** Includes an explicit native launcher-scoped fallback when a bound game's receipt/version cannot be used. */
  warnings: string[];
  /** Client: the Kiln mods that start with the player's own world (single-player, or friends joining it) instead of in
   *  the menu: side "server" mods and the mods that need one, in the order they load then. Each is also in
   *  `kiln.rejected`, skipped, with the reason "Starts with your own world, ...". Empty on a server and when the game's
   *  kiln.json has `"mods": {"hosted": false}` (then those mods say so). */
  withWorld: string[];
  /** The profile's switched-on EMF mods a dependency keeps from loading, from their mod.json as kiln-emf checks them at
   *  the start (kiln_manage::emf_deps): what is missing, off or out of range, and how to get it. With a game, its
   *  emf/mods folder counts too. Absent from older backends. */
  emf?: EmfDepProblem[];
}
/** One EMF mod that will not load because of a dependency (kiln_manage::emf_deps::EmfDepProblem). */
export interface EmfDepProblem {
  /** The profile entry's id (the row). */
  id: string;
  /** The dependency's id, as EMF reads it (lower case). */
  dep: string;
  /** Its name: its own mod.json's when it is there, else EMF's name for one of its own mods; null: unknown. */
  depName: string | null;
  /** The range as the mod.json writes it ("" for any version). */
  range: string;
  state: "missing" | "off" | "notLoading" | "tooOld" | "tooNew";
  /** The version there (off, notLoading, tooOld, tooNew). */
  found: string | null;
  /** One of EMF's own mods, which come with EMF's release zip (its lua mod above all). */
  withEmf: boolean;
  /** Where to get it: the dependency entry's own link, else EMF's page for one of EMF's own mods. */
  url: string | null;
  /** kiln-emf's own words for it at the start (kiln/state/emf.json `reason`). */
  reason: string;
}

// library
export interface LibraryRef {
  kind: ModKind;
  id: string;
  version: string;
  hash: string;
}
/** What "Remove its settings too" did (kiln_manage::mod_settings::SettingsRemoval): the paths removed (a shared file's
 *  entry as "<file> (mods.<key>)"), the copy in the backups folder (null: nothing to remove), and what stayed, why. */
export interface SettingsRemoval {
  removed: string[];
  backup: string | null;
  notes: string[];
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
  /** `packagedFor`: the side the download packed this copy for (a client/ or server/ folder around it, as Town Portals
   *  2.0.3 ships both copies of one version in one zip). Absent: no such folder. */
  source: { type: "file" | "folder" | "modpack" | "replaced-dll"; from: string; packagedFor?: "client" | "server" } | null;
  /** The id the mod's own manifest gives, when the Library id is not that (an engine mod such as "Hoarders Helper":
   *  the Library id is its Kiln-id slug). Engine plugins report their state rows under this id. */
  manifestId: string | null;
  manifest: Json | null;
  /** Its files were changed in its folder since Kiln imported them (a player edited its mod.json): Play refuses it as
   *  it is until libraryTakeOver makes the changed files the mod's entry. Absent: not changed. */
  changed?: boolean;
  /** (dev.29) An earlier version an update replaced, kept for a roll back (kiln_manage::kept::KeptInfo). Kept versions
   *  come after the others in `versions`, so `versions[0]` is never one unless nothing else is left. */
  kept?: KeptInfo | null;
  /** EML mods: the part of the mod Kiln does not run (kiln_manage::library::ImportRecord::loader_part). Its package
   *  carried a modified EML loader (a dbghelp.dll with code of its own, such as a hotkey) that Kiln does not load;
   *  Kiln runs the mod's EML part only. Absent: the whole mod runs. */
  loaderPart?: string | null;
}
export interface KeptInfo {
  /** When it was kept (RFC 3339 UTC). */
  kept: string;
  /** The version that took its place then. */
  replacedBy?: string | null;
  /** The settings files kept with it (relative to the game folder) and their bytes. */
  settings: string[];
  settingsSize: number;
  /** The profiles whose change kept it: only they roll back to it (absent or empty: any). */
  profiles?: string[];
}
/** A version of one mod (kiln_manage::kept::VersionRef). */
export interface VersionRef { version: string; hash: string }
/** library_rollback (kiln_manage::kept::RollbackReport): what the roll back changed in the profile (`changes`: what
 *  profile_revert puts back), whether the version rolled away from is kept now, the settings files that go back into
 *  the mod's game-folder copy at its next placement, and sentences for the player. */
export interface RollbackReport {
  profileId: string;
  profileName: string;
  kind: ModKind;
  id: string;
  name: string;
  from: VersionRef;
  to: VersionRef;
  changes: ProfileChange[];
  kept: boolean;
  settings: string[];
  notices: string[];
}
/** library_take_over (kiln_manage::library::TakenOver): the entry as it was, the entry its changed files are now
 *  (`from` again when nothing was changed), the mod's name as its manifest gives it now, and a note when the folder
 *  changed again meanwhile. */
export interface TakenOver { from: LibraryRef; to: LibraryRef; name: string;
  /** The folder changed again while it was taken over: the newer files stayed in it (it reads as changed again). */
  note?: string | null }
export interface LibraryEntry {
  kind: ModKind;
  id: string;
  name: string;
  versions: LibraryVersion[];
}
export type DetectKind = "kiln" | "native" | "plugin" | "emf" | "modpack" | "runtime" | "bare-dll" | "unknown"
  // made for other engines, or not mods: imported only once Kiln supports them; `reason` says why not yet
  | "eml" | "xhl" | "shroudforge" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame" | "shroudlink" | "ember" | "dllmod"
  | "proxy" | "data" | "save" | "reshade" | "cheat-table" | "asi";
/** kiln_manage::engines::Engine */
export type EngineId = "kiln" | "emf" | "shroudforge" | "eml" | "eml-r" | "set" | "enshwick" | "greeneye" | "shroudtopia" | "flame"
  | "shroudlink" | "ember" | "dllmod" | "mod-loader-proxy" | "ultimate-asi-loader" | "aldrich" | "live-map" | "shared-xp" | "reshade"
  | "chain-loader" | "native-folders" | "optiscaler";
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
export interface ConfigFileInfo {
  file: string;
  mod: string | null;
  kind: "kiln" | "emf" | "native" | "other";
  hasSchema: boolean;
  modVersion: string | null;
  modified: string | null;
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
  /** (dev.29.17) How the game was started; `uri` is empty for a direct start. */
  via?: StartMode;
}
// kiln_manage::sfi_launch
/** What a start will do to the ShroudForge family, before Play, and the token that confirms it. */
export interface Preflight {
  gameId: GameId;
  request: LaunchRequest;
  /** One confirm (or cancel) of this start, in this launcher process. */
  token: string;
  expires: string;
  /** What the player reads and acknowledges before the game starts; empty: no dialog, confirm at once. */
  warnings: StartWarning[];
  warningsSha256: string;
  /** Shown, never acknowledged (info findings, the cost note). */
  advisories?: StartWarning[];
  /** profile::sfi::status of the start's profile file. */
  shroudforge?: Json;
  /** The plan could not be written (write-failed): every Play tries again and reads the game's archive again. Another
   *  refusal is tried again only when the game, the mods or Kiln change, or the profile is activated. */
  reinspectsEveryPlay: boolean;
  /** Mode profile: the activation when the active profile was written again for this start. */
  activation?: Activation;
}
export interface StartWarning {
  /** previously-active-off: a save-sensitive package active at an earlier start is off now; base-origin
   *  (advisories only): the archive base the start's plan was made from, in its exact label. */
  kind: "start-refused" | "package-skipped" | "plan-warning" | "cost" | "previously-active-off" | "base-origin";
  code: string;
  message: string;
  severity: "info" | "warning" | "save-warning" | "infrastructure-error";
  causedBy?: string[];
  /** package-skipped: the package. */
  package?: {
    id: string; version: string; root: string; saveSensitive: boolean; data: boolean; lua: boolean; dlls: string[];
    reasons: { code: string; message: string; causedBy?: string[]; severity: string }[];
  };
  /** start-refused: the ShroudForge mods the profile switches on, none of which runs; keybinds-unsupported (advisories
   *  only): the package whose keyboard shortcuts do not work in kiln mode yet. */
  packages?: string[];
  /** previously-active-off, or a package-skipped one that was active before: kiln/state/sfi.json's row. */
  previous?: { id: string; version?: string; name?: string; basis: string; fromLaunchId?: string };
}
export type ChainStatus = "loaded" | "failed" | "missing" | "refused" | "duplicate" | "skipped";
export type RefusalDetail = "self" | "system" | "kiln" | "shroudforge" | "not-dll" | "outside" | "too-many";
export interface BootJson {
  format: number;
  kiln: string;
  abi?: number;
  pid: number;
  /** When the game started (the run's first moment), before `written`. */
  started?: string;
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
export interface StoppedStart {
  kind: "roster" | "boot-mod" | "content"; reason: string; mods: string[]; names: string[]; advice: string; utc: string; exitCode: number; pid: number;
}
export interface BootState {
  stoppedStart?: StoppedStart | null;
  boot: BootJson | null;
  mods: ModsJson | null;
  native: NativeJson | null;
  stale: boolean;
  /** kiln.log's error lines for the run that wrote boot.json (null: kiln.log is another run's or missing). */
  log?: { errors: number; first: string[] } | null;
}
/** What kiln-eml is doing before the game's window exists (kiln/state/eml.json, phase running): `launch_prestart`. */
export interface PreStart {
  /** starting | checking | prepare | mods | writing | saving; startup: a kiln-mode start's startup pass (`step` says where) */
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
  /** Stage startup: checking | mods | verify | write | save, its number and how many steps there are. */
  step?: string | null;
  stepNumber?: number;
  steps?: number;
  /** The sentence for the launch status ("Preparing the game data for EML mods: 12.3 GiB of 48.6 GiB. ..."), in English:
   *  shell/startProgress.ts `preStartLine` says a startup pass in the launcher's language. */
  message: string;
}
/** One step of a start (`launch_progress`, kiln_manage::start_progress): the launcher's own (`cook`, `profile`,
 *  `steam`) or the runtime's (kiln/state/progress.json). Times in milliseconds since the start began. */
export interface StartStep {
  id: string;
  label: string;
  detail?: string | null;
  done?: number | null;
  total?: number | null;
  /** When it began (null: not yet). */
  ms?: number | null;
  tookMs?: number | null;
  /** What it usually takes on this PC (null: no basis yet). */
  usualMs?: number | null;
  state: "done" | "running" | "next" | "skipped";
  launcher: boolean;
}
export interface StartEstimate {
  /** null: a step still to come has no basis (no number is shown; the bar counts steps). */
  leftMs: number | null;
  /** How many earlier starts the number comes from. */
  starts: number;
  /** From starts with the same Kiln and the same mods. */
  same: boolean;
  /** The running step has taken 1.5 times its usual time. */
  slow: { id: string; usualMs: number } | null;
  /** 0..1, below 1 until ready. */
  fraction: number;
  byTime: boolean;
  stepsDone: number;
  stepsPlanned: number;
  usual: (number | null)[];
}
export type StartPhase = "launcher" | "waiting" | "booting" | "loading" | "ready" | "idle" | "failed" | "quiet" | "ended" | "legacy";
/** What a start is doing, from Play to the main menu (`launch_progress`). */
export interface StartTimeline {
  phase: StartPhase;
  /** idle and failed: the runtime's sentence. */
  why?: string | null;
  kiln?: string | null;
  started: string;
  atMs: number;
  /** Milliseconds since progress.json was written (null: none of this start). */
  quietMs?: number | null;
  steps: StartStep[];
  running?: number | null;
  estimate: StartEstimate;
  /** The launcher's own steps are in it. */
  launcher: boolean;
  /** Steam was asked a minute ago or more and the game's process does not exist yet: no number. */
  steamSlow?: boolean;
  /** (0.2.0-dev.31.13) How the launcher's start was started (launch_begin's mode); absent: not said. A Vanilla start is
   *  followed too until its game is there, and shown without a bar. */
  mode?: LaunchMode | null;
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
  /** The server was this game's own hosted world (nothing it advertises is missing; modpack_pending never offers it).
   *  Absent in a file an older Kiln wrote: then the backend compares the server id with the game's own. */
  hosted?: boolean;
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
  lastStartS?: number | null;
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
  /** (dev.30.3) A Kiln mod whose kiln.mod.json says "steamMessages": true: it talks to other players over Steam peer
   *  messages. Absent: it does not (or an older backend). */
  steamMessages?: boolean;
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
  // identity (0.2.0-dev.29.18): what the server advertises for an engine mod when that is not hash (kiln-manage modpack.rs)
  mods: { kind: ModKind; id: string; version: string; name?: string; path: string; hash: string; identity?: string; clients?: Clients; side?: Side }[];
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

// admin: the Tauri commands' payloads (KAP types, the hub and its transport seam: ./admin.ts, where AdminTransport is the
// seam's interface and AdminTransportKind this union; the transport over these commands: ./adminTauri.ts)
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
/** Opens a folder, or shows a file selected in its folder. A relative `path` (a found mod's) is under `base`, the game
 *  folder: the backend joins and normalises it for Explorer (never `${base}/${path}` here). */
export const appOpenPath = (path: string, base?: string) => call<null>("app_open_path", { path, base });
export const appOpenUrl = (url: string) => call<null>("app_open_url", { url });
/** "Open in Discord": a discord.com link in the Discord app when this PC has it, else (or in Game Mode) in the browser;
 *  `fallback` is the web link to open instead of `url` then (default: `url`). Answers where it opened. */
export const appOpenDiscord = (url: string, fallback?: string) => call<"app" | "web">("app_open_discord", { url, fallback: fallback ?? null });
export const taskList = () => call<Task[]>("task_list");
export const taskGet = (taskId: TaskId) => call<Task>("task_get", { taskId });
export const taskCancel = (taskId: TaskId) => call<null>("task_cancel", { taskId });
/** The first screen is up (once per start: shell/started.ts). */
export const appStarted = () => call<null>("app_started");
/** The page took the window's close request (shell/closeGuard.ts): `asking` the player, or closing. Without it the
 *  backend closes the window by itself a few seconds later (src-tauri/src/webproc.rs). */
export const appCloseSeen = (asking: boolean) => call<null>("app_close_seen", { asking });
/** Whether closing the window now would lose work (shell/closeGuard.ts, at its start and at every change): a slow page
 *  with unsaved work gets longer to answer a close (src-tauri/src/webproc.rs). */
export const appCloseUnsaved = (unsaved: boolean) => call<null>("app_close_unsaved", { unsaved });
/** Visibility alone does not prove painting. Two visible animation frames can confirm a renderer startup. */
export const appRenderFrame = (painted: boolean, visible: boolean) => call<string | null>("app_render_frame", { painted, visible });
/** What the page measured about its drawing (shell/renderReport.ts), into launcher.log: at most two lines a start. */
export const appRenderReport = (report: PageRender) => call<null>("app_render_report", { report });
/** Settings, "Draw the launcher without the GPU": sets or clears the marker for the next start (Linux). */
export const appRenderSet = (software: boolean) => call<RenderInfo>("app_render_set", { software });
/** Settings, "Switch to X11 when drawing is slow on Wayland": on lets the launcher switch by itself, off keeps Wayland (Linux). */
export const appRenderX11Set = (on: boolean) => call<RenderInfo>("app_render_x11_set", { on });
export const appRenderForceSet = (on: boolean) => call<RenderInfo>("app_render_force_set", { on });
/** Settings, "WebKit's sandbox": on removes the marker, off writes it, for the next start (Linux). */
export const appSandboxSet = (on: boolean) => call<SandboxInfo>("app_sandbox_set", { on });
/** (dev.30) The folder a file dialog of `kind` opens in, or "<folder>/<fileName>" (src-tauri/src/dialog_folder.rs; ui/dialogFolder.ts):
 *  `preferred` when it exists, else the kind's last folder, else Downloads, else home; never an AppImage's mount. null: none fits. */
export const appDialogDefault = (kind: string, fileName: string | null, preferred: string | null) =>
  call<string | null>("app_dialog_default", { kind, fileName, preferred });
/** Keeps the folder a dialog of `kind` ended in (`path` itself when `folder`, else its parent). */
export const appDialogRemember = (kind: string, path: string, folder: boolean) => call<null>("app_dialog_remember", { kind, path, folder });
/** Settings, "Kiln Launcher in the app menu" (Linux AppImage): writes or removes the entry and its icons. */
export const appDesktopSet = (on: boolean) => call<DesktopInfo>("app_desktop_set", { on });
/** Translation files a player put in the data folder's `locales/` (docs/kiln/translating.md): `<tag>.json` each, read
 *  at the start and on Settings' reload. `dir` is the folder (it need not exist). */
export interface LocaleFiles {
  dir: string;
  files: { tag: string; text: string }[];
  errors: { file: string; message: string }[];
}
export const appLocales = () => call<LocaleFiles>("app_locales");
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
/** "dev" is the public release line while there is no stable release ("Release" in the UI); "beta" (dev.29) are test
 *  builds of the next release (api/channel.ts). */
export type UpdateChannel = "stable" | "dev" | "beta";
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
  /** (0.2.0-dev.30.5) The Kiln runtime this launcher ships, once the backend read it: the only one that goes into the
   *  game by itself after an update (shell/updateApply.ts). Null before that, in a build without one, and in the Nexus
   *  build; absent from older backends. */
  shippedRuntime?: string | null;
  taskId: TaskId | null;
  error: KilnError | null;
  /** (dev.29) The channel this build belongs to: a test build's is "beta" (baked in when it was built), else the
   *  channel of its version. Absent from an older backend: the version's. */
  buildChannel?: UpdateChannel;
  /** (dev.29) This test build's beta number (null: not a test build). */
  beta?: number | null;
  /** (dev.29) The channel switch the player started (update_switch) while it downloads or waits for the restart. */
  switching?: UpdateChannel | null;
  restoreRequested?: boolean;
  beforeBetaWarning?: string[];
  /** (dev.29) The copy of settings and profiles taken before the first start of a test build: a switch back can put
   *  it back (update_switch `restore`). */
  beforeBeta?: BeforeBeta | null;
}
/** selfupdate/snapshot.rs: what a "before beta" copy holds. */
export interface BeforeBeta {
  /** When it was taken (RFC 3339). */
  taken: string;
  /** The launcher that wrote the files last before the test build (its version, "" when not known). */
  from: string;
  /** The test build that took it. */
  by: string;
  /** Files in it (settings.json, each profile's meta.json, the Library's kiln-kept.json). */
  files: string[];
}
/** Guard the command boundary as well as the UI: even a stale callback or a directly mounted hook cannot invoke
 *  an updater command in the Nexus build. Failure to read the distribution also fails closed. */
async function selfUpdateCall<T>(cmd: string, args: Record<string, unknown> = {}): Promise<T> {
  const info = await appInfo();
  if ((info.distribution ?? "direct") !== "direct") throw { code: "unsupported", message: t("api.commands.selfUpdateDisabled") } satisfies KilnError;
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
/** (dev.29) A channel switch the player asked for: downloads and installs the newest build of `channel` (on the way
 *  back from a test build also an older one), then "Restart now" (updateRestart) runs it. `restore`: the restart puts
 *  the "before beta" copy back first (status.beforeBeta). Task result: UpdateStatus. */
export const updateSwitch = (channel: UpdateChannel, restore = false) => selfUpdateCall<TaskId>("update_switch", { channel, restore });

// game.rs
export const gameList = () => call<GameInstall[]>("game_list");
export const gameDetect = () => call<GameInstall[]>("game_detect");
export const gameAdd = (path: string) => call<GameInstall>("game_add", { path });
export const gameRemove = (gameId: GameId) => call<null>("game_remove", { gameId });
export const gameStatus = (gameId: GameId) => call<GameStatus>("game_status", { gameId });
export const gameStartGet = (gameId: GameId) => call<StartSetup>("game_start_get", { gameId });
export const gameStartSet = (gameId: GameId, settings: StartSettings) => call<GameInstall>("game_start_set", { gameId, settings });

// runtime.rs
export const runtimeList = () => call<RuntimeBundle[]>("runtime_list");
export const runtimeImport = (path: string) => call<RuntimeBundle>("runtime_import", { path });
export const runtimeStatus = (gameId: GameId) => call<InstallState>("runtime_status", { gameId });
/** Task result: InstallReceipt. */
export const runtimeInstall = (gameId: GameId, version: string, options: InstallOptions = {}) =>
  call<TaskId>("runtime_install", { gameId, version, options });
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
/** `gameId`: the identified game's hosted setting, EMF folder and receipt runtime for native compatibility.
 *  null: explicitly unbound/launcher-scoped, never another game's runtime. An unknown supplied id errors. */
export const profileResolve = (profileId: ProfileId, side: RunSide, gameId: GameId | null = null) =>
  call<ResolvePreview>("profile_resolve", { profileId, side, gameId });
/** One activation per game at a time (./oneActivation.ts): a second one while the first runs is refused (`busy`). */
export const profileActivate = (profileId: ProfileId, gameId: GameId) =>
  oneActivation(gameId, () => call<Activation>("profile_activate", { profileId, gameId })).then((r) => { profileSwitched(gameId, profileId); return r; });
export const profileDeactivate = (gameId: GameId) =>
  oneActivation(gameId, () => call<PlacementCleanup>("profile_deactivate", { gameId })).then((r) => { profileSwitched(gameId, null); return r; });
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
export const profileEnsureDefault = (gameId: GameId) => oneActivation(gameId, () => call<Activation | null>("profile_ensure_default", { gameId }));

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
  /** ShroudForge's startup pass failed at the last start (sf_startup.rs): on the shroudforge row whoever ran it, on the
   *  eml row as a pointer while ShroudForge runs the EML mods. */
  startupFailed?: SfStartupNote | null;
  /** ShroudForge's startup pass failed in one mod and ran again without it (sf_startup.rs `skipped`): on the shroudforge
   *  row, a warning (the other mods of the pass loaded). */
  startupSkipped?: SfSkipNote | null;
}
/** ShroudForge's own record of a failed startup pass: the mods it writes into the game data did not load. `text` is the
 *  sentence for the row (no full stop): what happened, which mods, what to change (`cure`). */
export interface SfStartupNote {
  error: string;
  at?: string;
  ownLoader: boolean;
  version?: string;
  build?: string;
  mods: string[];
  cure?: string;
  /** Kiln's ShroudForge tried the pass once more without the mod it failed in (first), and that failed too. */
  skipped?: SfSkippedMod[];
  retryError?: string;
  /** ShroudForge refused its clean copy of the game data: it carries an uninstalled mod's changes (Steam's manifest lists
   *  other bytes, or it holds a known mod's program). `text` then leads with the plain words and what the player does. */
  staleGameData?: boolean;
  text: string;
}
/** A mod ShroudForge's startup pass ran without: the one it failed in, then any left out because they need it. */
export interface SfSkippedMod {
  id: string;
  name?: string;
  version?: string;
  error: string;
}
/** ShroudForge's startup pass ran without a mod (it failed in it); the other mods loaded. `text`: the sentence for the
 *  row (no full stop), with the mod, its error and what to do. */
export interface SfSkipNote {
  skipped: SfSkippedMod[];
  loaded: number;
  at?: string;
  ownLoader: boolean;
  version?: string;
  build?: string;
  text: string;
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
/** A player's own ShroudForge folder that serves a profile entry (engine_mods::OwnFolder): Kiln neither switches,
 *  updates nor removes it until the player hands it over (engineAdoptFolder). */
export interface OwnFolder {
  kind: ModKind;
  id: string;
  version: string;
  /** The player's folder in the game. */
  path: string;
  /** The folder Kiln's copies of this mod have, when the player's is called something else: taking it over renames it. */
  renameTo?: string | null;
  /** Absent: its files are the Library entry's, and Kiln can take it over. Else why not, naming the first file that differs. */
  refusal?: string | null;
}
/** What engineAdoptFolder did (engine_mods::Adopted). */
export interface Adopted {
  kind: ModKind;
  id: string;
  version: string;
  /** The folder, Kiln's placement now. */
  path: string;
  renamedFrom?: string | null;
  /** It was Kiln's already: nothing changed. */
  already?: boolean;
  /** Settings whose values are the player's: they stay as they are. */
  settingsKept?: string[];
}
/** The profile's ShroudForge entries the player's own folders serve in this game; empty unless the profile is the one
 *  the game uses. Reads only. */
export const engineOwnFolders = (profileId: ProfileId, gameId: GameId) => call<OwnFolder[]>("engine_own_folders", { profileId, gameId });
/** "Let Kiln manage this folder": only on the player's request. Refuses (`refused`) with the first file that differs
 *  from the Library entry's, while the game runs (`game_running`) and for a profile the game does not use (`conflict`). */
export const engineAdoptFolder = (kind: ModKind, id: string, profileId: ProfileId, gameId: GameId) =>
  call<Adopted>("engine_adopt_folder", { kind, id, profileId, gameId });
export const emlBackup = (gameId: GameId) => call<EmlBackup[]>("eml_backup", { gameId });
/** One file "Restore the game's data" moves: its path in the game folder, size and SHA-256. */
export interface DataRestoreEntry { from: string; size: number; sha256: string }
/** What would be moved: the leftover containers past the game's own count, the shared backup pair, its seal and
 *  Kiln's change cache. `fingerprint` covers the state and every file; apply takes it, so only what was shown moves. */
export interface DataRestorePlan { fingerprint: string; files: DataRestoreEntry[] }
/** A move that was made: the folder under kiln/eml/displaced/ and the files in it. */
export interface DataRestoreMoved { folder: string; files: DataRestoreEntry[] }
/** The plan for this game, or a refusal with its reason (nothing unproven, the game runs, ShroudForge is the writer,
 *  the game's index changed since the last start, nothing left over). Finishes a move that was interrupted. */
export const emlDataPlan = (gameId: GameId) => call<DataRestorePlan>("eml_data_plan", { gameId });
/** Moves the plan's files aside. `verified`: the player confirmed Steam's file check. Never deletes or overwrites. */
export const emlDataApply = (gameId: GameId, fingerprint: string, verified: boolean) => call<DataRestoreMoved>("eml_data_apply", { gameId, fingerprint, verified });
/** Moves a folder's files back; refused when one of the names exists again. */
export const emlDataUndo = (gameId: GameId, folder: string) => call<DataRestoreMoved>("eml_data_undo", { gameId, folder });
/** The newest move that was not undone (kept across launcher starts, so Undo stays), or null. */
export const emlDataLatest = (gameId: GameId) => call<DataRestoreMoved | null>("eml_data_latest", { gameId });
// kiln_manage::restore_data::base_origin, commands/sfi.rs
/** The BASE "Restore the game's data" takes a kiln-mode game back to. recorded: the exact label kiln-eml's receipt
 *  records, keyed to the backups in the game folder; unrecorded: backups with no recorded origin; unknown: Kiln cannot
 *  tell (`reason`). Never "vanilla". */
export interface DataRestoreBase {
  state: "recorded" | "unrecorded" | "unknown";
  token: string | null;
  label: string | null;
  baselineSha256: string | null;
  reason: string | null;
}
/** null: not kiln mode, or Kiln wrote none of the game's data. Hashes the backups: ask once, not on a poll. */
export const emlDataBase = (gameId: GameId) => call<DataRestoreBase | null>("eml_data_base", { gameId });
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
  /** dev.29: "rule:<id>" when a `before` rule keeps it loading first (in the proxy) whatever its late switch says; the
   *  switch is the rule's while the rule applies (`late` is false). Absent from an older backend. */
  earlyBy?: string | null;
  /** EMF's loader: it starts EMF itself, which Kiln's EMF support has to see when it starts, so Kiln always loads it
   *  first and the late switch is not offered. */
  loader: boolean;
  /** shroudlink's client addon (compat::is_gated_addon): it has to find the game's functions before Kiln hooks them,
   *  so it is never switched late; one already late (an older Kiln allowed it) can be switched back. */
  gated?: boolean;
}
/** before (dev.29): the rule's subject loads before its target (the target late, the subject early). */
export type RuleAction = "warn" | "late" | "disable" | "before";
export interface CompatRuleResult {
  id: string;
  source: "kiln" | "user";
  action: RuleAction;
  message: string;
  fired: boolean;
  applied: boolean;
  /** The chain entries it moves late or switches off (before: the ones that load after). */
  targets: string[];
  /** before: the chain entries it keeps loading first. */
  early?: string[];
  note?: string;
  /** before: both sides are there but the order cannot be made (a DLL that never loads late, a cycle, another rule):
   *  nothing was moved. Shown as a warning. */
  warning?: string;
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

// ---- cooking mods (kiln_manage::cook) ----
/** Why a mod is cooked before the game starts (cook::needs_cook; "filesGone": a file its last cook made is gone or was
 *  changed, by the cook's receipt in the game). */
export type CookReason = "notCooked" | "modChanged" | "gameChanged" | "lastFailed" | "filesGone" | "asked";
/** The last cook of a mod for a game (`<game>/kiln/state/cooked.json`). */
export interface CookedMod {
  identity: string;
  gameBuild: string;
  at: string;
  ok: boolean;
  files?: boolean;
  /** A failed cook: the last lines the tools said. */
  error?: string | null;
}
/** A mod of the game's profile that has something to cook. */
export interface CookJob {
  id: string;
  name: string;
  /** The folder the game loads the mod from: where its `assets/cooked` is made. */
  folder: string;
  identity: string;
  /** Absent: cooked for this game build, nothing to do. */
  reason?: CookReason | null;
  last?: CookedMod | null;
}
/** How one mod's cook ended. */
export interface ModCook {
  id: string;
  name: string;
  reason: CookReason;
  ok: boolean;
  /** Not cooked: the tools' last lines, or the one sentence that says why no tool ran. */
  error?: string | null;
}
export interface CookRun {
  mods: ModCook[];
  /** The active profile written again before the cook (it changed since its activation): its notices, as Play's. */
  activation?: Activation | null;
  /** Play's cook only: the game data Mod.OnBoot reads, made or found after the cook (kiln_manage::boot_snapshot). */
  bootSnapshot?: BootSnapshot;
}
/** What making the boot game-data snapshot came to: made | reused | skipped (no mod needs it) | refused | failed |
 *  unavailable. */
export interface BootSnapshot {
  status: "made" | "reused" | "skipped" | "refused" | "failed" | "unavailable";
  key?: string;
  path?: string;
  counts?: Record<string, number>;
  /** Kinds the game's archive could not give: every OnBoot read of them finds nothing. */
  emptyKinds: string[];
  skipped?: number;
  archive?: string;
  bytes?: number;
  seconds?: number;
  reason?: string;
}
/** The mods of the game's profile with something to cook, each with whether it needs a cook now. Reads only. */
export const cookPlan = (gameId: GameId) => call<CookJob[]>("cook_plan", { gameId });
/** A task (result CookRun): applies the active profile when it changed, then cooks the mods that need it (each from
 *  nothing), or with `modId` that one mod whatever its record says. Holds the game's lock (no start meanwhile); its
 *  Cancel stops the tool that runs. Refuses while the game runs ("Cook again" also right after a start). A mod's
 *  failed cook is in the result, never the task's error. */
export const cookRun = (gameId: GameId, modId: string | null = null) => call<TaskId>("cook_run", { gameId, modId });
/** Play once with another profile: a task (result BootSnapshot) that makes the boot game-data snapshot when an enabled
 *  mod of that single start asks for the boot phase, as Play's cook_run does after the cook (`skipped` otherwise). Cooks
 *  nothing. Refuses while the game runs; a producer that fails is in the result, never the task's error. */
export const bootSnapshotRun = (gameId: GameId, profileId: ProfileId) => call<TaskId>("boot_snapshot_run", { gameId, profileId });

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
  oneActivation(gameId, () => call<AddReport>("library_add_to_profile", { gameId, profileId, entries }));
/** Rejects with game_running while a game runs, or a server whose profile file points into the library, even with force (it may be reading the mod).
 *  `shown`: the libraryUsers answer a confirmation named. Removal goes past exactly those; any other user is a conflict, whatever `force` says. */
export const libraryRemove = (entry: LibraryRef, force = false, shown?: string[], settings = false) =>
  call<SettingsRemoval | null>("library_remove", { entry, force, shown: shown ?? null, settings });
/** "Remove its settings too" for a mod no longer in the Library (kiln_manage::mod_settings::remove): conflict while the
 *  Library still holds a version of it, game_running while a game runs. */
export const libraryRemoveSettings = (kind: ModKind, id: string) => call<SettingsRemoval>("library_remove_settings", { kind, id });
/** Clean up: one look at the running processes and the profiles for the whole batch. Rejects with game_running (nothing
 *  removed) while a game runs, or a server may read one of the versions; otherwise reports the versions not removed. */
export const libraryRemoveMany = (entries: LibraryRef[], force = false) => call<RemoveReport>("library_remove_many", { entries, force });
/** The versions Clean up may remove: in no profile, not in any known game's active profile file, and not in a
 *  play-once file a start can still read. */
export const libraryUnused = () => call<LibraryRef[]>("library_unused");
/** What library_remove refuses (conflict) over without force: profile ids, and "<profile> (active in <game>)" /
 *  "(play-once in <game>)" for each game whose profile file points at the version. */
export const libraryUsers = (entry: LibraryRef) => call<string[]>("library_users", { entry });
/** "Use the changed files": a Library entry the player changed in its folder becomes the mod's new entry, every profile
 *  that listed it lists that one, and the games where such a profile is active get it written again. Rejects with
 *  game_running while a game, or a server that may read the entry, runs. */
export const libraryTakeOver = (entry: LibraryRef) => call<TakenOver>("library_take_over", { entry });
/** "Replace DLL…": a new Library build of a native mod with one DLL swapped; the build before it is kept. A refusal
 *  with `detail.replaceDllConfirmation` is a question (another file name, another kind of DLL): ask, then call again
 *  with `confirmed`. */
export const libraryReplaceDll = (profileId: string, entry: LibraryRef, path: string, confirmed = false) =>
  call<{ entry: LibraryRef; notices: string[] }>("library_replace_dll", { profileId, entry, path, confirmed });

/** "Roll back to <version>": the kept version `to` (null: the newest kept one) replaces the mod's entry in the profile. */
export const libraryRollback = (gameId: GameId | null, profileId: string, kind: ModKind, id: string, to: VersionRef | null) =>
  call<RollbackReport>("library_rollback", { gameId, profileId, kind, id, to });
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
// Settings' ShroudForge card; kiln_manage::sfi_backend, commands/sfi_backend.rs
/** kiln.json `shroudforge.backend`: Kiln's bundled ShroudForge (the default), the player's own, or Kiln's interpreter. */
export type SfBackend = "hosted" | "installed" | "kiln";
/** What kiln.json says: nothing (the default), a choice, the older useInstalled switch, or a value no reader takes. */
export type SfSetting = { kind: "default" } | { kind: "explicit"; backend: SfBackend } | { kind: "inherited"; backend: SfBackend }
  | { kind: "invalid"; value: string };
/** What a start gets from kiln.json and the launcher's environment (sfi_backend::Resolution). */
export interface SfResolution {
  requested: SfBackend | null;
  /** null: refused, every ShroudForge backend stands aside that start. */
  selected: SfBackend | null;
  source: "default" | "setting" | "useInstalled" | "envRuntime";
  refusal: "settingInvalid" | "interpreterHeld" | null;
  /** One line in the core's words. */
  reason: string;
  /** KILN_SHROUDFORGE=0: main's note. */
  envOffNote: string | null;
}
export interface SfBackendCard {
  gameId: GameId;
  setting: SfSetting;
  next: SfResolution;
  /** Why Kiln's interpreter cannot be chosen now; absent: it can. */
  interpreterHeld?: string;
  /** With kiln selected for the next start: what it meets in the game folder. */
  interpreterRoute?: "ready" | "env-off" | "missing" | "own-loader" | "eml-r";
  ownLoader?: string;
  /** kiln.json cannot be read: why (nothing is written until it is fixed). */
  unreadable?: string;
  /** The kiln.json bytes this card was read from; a choice is written only over them. */
  revision: string;
  /** kiln/state/launch.json: the last start as the core froze it. */
  last?: { backend: string | null; reasonClass: string | null; reason: string | null; pid: number | null };
}
export interface SfBackendPreview {
  gameId: GameId;
  backend: SfBackend | null;
  revision: string;
  /** The next start with the choice written; absent: it cannot be written (`refused`). */
  next?: SfResolution;
  refused?: string;
  /** kiln.json would stay as it is. */
  unchanged: boolean;
}
export const sfiBackendRead = (gameId: GameId) => call<SfBackendCard>("sfi_backend_read", { gameId });
/** Writes nothing. `backend` null: back to the default. */
export const sfiBackendPreview = (gameId: GameId, backend: SfBackend | null) => call<SfBackendPreview>("sfi_backend_preview", { gameId, backend });
/** Rejects with `conflict` when kiln.json changed since `revision`, `game_running` while the game runs, `refused` for kiln
 *  while it is held; nothing is written then. */
export const sfiBackendSet = (gameId: GameId, backend: SfBackend | null, revision: string) =>
  call<SfBackendCard>("sfi_backend_set", { gameId, backend, revision });

// launch.rs
/** Through the activation guard (./oneActivation.ts): the start applies the active profile again (refresh_active). */
export const launchPlay = (gameId: GameId, request: LaunchRequest) => oneActivation(gameId, () => call<LaunchResult>("launch_play", { gameId, request }), { play: true });
export const launchState = (gameId: GameId) => call<BootState>("launch_state", { gameId });
export const launchPrestart = (gameId: GameId) => call<PreStart | null>("launch_prestart", { gameId });
/** Play was pressed (before the cook): the launcher times the start's own steps from now. */
export const launchBegin = (gameId: GameId, mode: LaunchMode) => call<void>("launch_begin", { gameId, mode });
/** What the start is doing now (null: nothing to show). */
export const launchProgress = (gameId: GameId) => call<StartTimeline | null>("launch_progress", { gameId });
/** Play ended before the game existed: the start the launcher was timing is over. */
export const launchEnd = (gameId: GameId) => call<void>("launch_end", { gameId });
// the check before Play and its dialog
/** The start run up to its check before Play (the profile written again or the play-once one written; nothing opened, no
 *  flag, no backup): the warnings to show (none: no dialog) and the token launchConfirm takes from this window only.
 *  launchPlay and launchConfirm reject a start with warnings nobody acknowledged as `consent_required` with the
 *  preflight to show in `detail.preflight`. */
export const launchPrepare = (gameId: GameId, request: LaunchRequest) =>
  oneActivation(gameId, () => call<Preflight>("launch_prepare", { gameId, request }), { play: true });
export const launchConfirm = (gameId: GameId, token: string, request: LaunchRequest) =>
  oneActivation(gameId, () => call<LaunchResult>("launch_confirm", { gameId, token, request }), { play: true });
/** The dialog was closed: the token goes. */
export const launchCancel = (gameId: GameId, token: string) => call<boolean>("launch_cancel", { gameId, token });

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
/** kiln_manage::report::FileGroup: how "What's in this report" groups the zip's files. */
export type ReportFileGroup = "kiln" | "mods" | "settings" | "listings";
/** kiln_manage::report::ContentsFile: one file of a support report. */
export interface ReportContentsFile {
  /** The path in the zip. */
  path: string;
  group: ReportFileGroup;
  /** What it holds (reportContents.about.<about>). */
  about: string;
  /** Bytes before compression. */
  size: number;
  /** Other mods' and tools' files only: the player may leave it out. */
  excludable: boolean;
  /** Left out: not in the zip. */
  excluded: boolean;
  /** How often each replacement stands in it: `{ home: 169, player: 3 }`. */
  replaced: Record<string, number>;
}
/** kiln_manage::report::Contents: "What's in this report". */
export interface ReportContents {
  files: ReportContentsFile[];
  /** All files in the zip together, before compression. */
  size: number;
  /** summary.txt ("View"). */
  summary: string;
  /** What a report never holds (reportContents.never.<id>; `text` is English). */
  never: { id: string; text: string }[];
}
/** What the report would hold, built as report_save builds it; nothing is written. `exclude`: other mods' files left out. */
export const reportPreview = (target: LogTarget | null, exclude: string[] = []) => call<ReportContents>("report_preview", { target, exclude });
/** A chosen path, the native-dialog fallback, or the dialog-free Downloads action. */
export type ReportSaveDestination = "chosen" | "automatic" | "downloads";
/** Writes the support report zip (logs, state files, profile, listings, summary.txt; names and paths replaced) to
 *  `path`, with filesystem fallbacks. `automatic`: the dialog failed (Kiln's state folder, Downloads, home);
 *  `downloads`: no dialog (Downloads, home). Both use only the filename from `path`. The result is the actual path.
 *  `target` null: the system and the launcher only. `exclude`: other mods' files the player left out. */
export const reportSave = (target: LogTarget | null, path: string, exclude: string[] = [], destination: ReportSaveDestination = "chosen") => call<ReportSaved>("report_save", { target, path, exclude, destination });
/** A few lines for a forum post, redacted like the zip. */
export const reportSummary = (target: LogTarget | null) => call<string>("report_summary", { target });
/** What the window did with a support report that report_save never hears of (the Save dialog cancelled, an error
 *  shown), into launcher.log. Never throws: a note that cannot be written is dropped. */
export type ReportNote = "opened" | "dialogCancelled" | "dialogChosen" | "dialogFailed" | "saveToDownloads" | "failed";
export const reportNote = (note: ReportNote, detail?: string) => call<null>("report_note", { note, detail: detail ?? null }).catch(() => null);

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
export interface EditorStorageSnapshot { character: string; gameId: string; text: string | null; local: boolean }
/** `storage` is the planned `mods["kiln-items"].storage` value only; the backend writes it into the exact sidecar bytes. */
export interface EditorStorageReturn { path: string; gameId: string; expectedSave: number[]; expectedSidecar: string; data: number[]; storage: string; oldWitness: string; newWitness: string }
export const editorStorageRead = (path: string, gameId: string | null = null) => call<EditorStorageSnapshot | null>("editor_storage_read", { path, gameId });
export const editorReturnStorage = (request: EditorStorageReturn) => call<{ backup: string }>("editor_return_storage", { request });

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
/** An open web panel (kiln_manage::admin::web_panel::WebPanelInfo, 0.2.0-dev.31.12): the server's own loopback origin,
 *  reached through the launcher's gateway (a TCP connection on this machine, or ssh -W to a remote server).
 *  (0.2.0-dev.31.19) Or, `via: "hosted"`, the panel's own address on the internet (Kilnwarden's hosted address or an own
 *  address, from its launcher-endpoint.json `public`), which the browser reaches by itself: no gateway. */
export interface WebPanelInfo {
  serverId: ServerId;
  /** `https://127.x.y.z:port` (`http://` for a panel without TLS); `https://<name>` for `via: "hosted"`. */
  origin: string;
  /** The pinned certificate's SHA-256 (hex), null without TLS. */
  fingerprint: string | null;
  via: "local" | "ssh" | "hosted";
  /** False when this system could not give the server its own loopback address (cookies shared on 127.0.0.1). */
  isolated: boolean;
  /** The gateway closes this long after the panel's last tab (0 for `via: "hosted"`). */
  idleCloseS: number;
  connections: number;
  /** (0.2.0-dev.31.19) The address on the internet this open went to (`via: "hosted"`), else null. */
  public: string | null;
  /** (0.2.0-dev.31.19) The panel's file named an address on the internet but the launcher's check of it failed, so it
   *  opened on this computer: "unreachable" (no answer in time) or "refused" (another certificate, no owner yet, the
   *  launcher's sign-in off, an error). Null otherwise. */
  fallback: "unreachable" | "refused" | null;
}
/** Opens the server's web panel in the browser, signed in as the owner; the panel as the window shows it.
 *  (0.2.0-dev.31.19) "auto": at the panel's own address on the internet when it has one that passes the check, else on
 *  this computer; "local": on this computer ("Open on this computer instead"). (0.2.0-dev.31.20) "auto" is "local"
 *  while the server's "Always open the web panel on this computer" is on (`WebPanelSettings.alwaysLocal`: no check at
 *  all). */
export const adminOpenWebPanel = (serverId: ServerId, route: "auto" | "local" = "auto") => call<WebPanelInfo>("admin_open_web_panel", { serverId, route });
/** (0.2.0-dev.31.20) A server's web panel settings on the launcher's side (kiln_manage::admin::web_panel::settings,
 *  the launcher data folder's web-panel-settings.json; local and remote servers alike). */
export interface WebPanelSettings {
  /** "Always open the web panel on this computer": Open web panel never checks or uses the panel's own address on the
   *  internet (Kilnwarden's launcher-endpoint.json `public`) and always opens it through this computer's gateway. */
  alwaysLocal: boolean;
}
/** (0.2.0-dev.31.20) The server's web panel settings. */
export const adminWebPanelSettings = (serverId: ServerId) => call<WebPanelSettings>("admin_web_panel_settings", { serverId });
/** (0.2.0-dev.31.20) Saves the server's web panel settings; what they are now. */
export const adminWebPanelSettingsSet = (serverId: ServerId, settings: WebPanelSettings) =>
  call<WebPanelSettings>("admin_web_panel_settings_set", { serverId, settings });
/** Closes the server's web panel gateway: whether one was open. */
export const adminCloseWebPanel = (serverId: ServerId) => call<boolean>("admin_close_web_panel", { serverId });
/** The web panels open now. */
export const adminWebPanels = () => call<WebPanelInfo[]>("admin_web_panels");
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
  "snapshot_status", "snapshot_continue",
  "app_info", "app_settings_get", "app_settings_set", "app_open_path", "app_open_url", "app_open_discord", "task_list", "task_get", "task_cancel",
  "game_list", "game_detect", "game_add", "game_remove", "game_status", "game_start_get", "game_start_set",
  "runtime_list", "runtime_import", "runtime_status", "runtime_install", "runtime_uninstall", "runtime_rollback", "runtime_classify",
  "profile_list", "profile_get", "profile_create", "profile_save", "profile_delete", "profile_resolve", "profile_activate",
  "profile_deactivate", "profile_active", "activation_notices", "notices_unseen", "notices_shown",
  "library_list", "library_detect", "library_import", "library_remove", "library_remove_many", "library_remove_settings", "library_unused", "library_users", "library_lint", "library_take_over", "library_rollback", "library_replace_dll",
  "config_list", "config_read", "config_set", "config_write", "config_reset", "config_native_state", "config_native_set",
  "launch_play", "launch_state", "launch_prestart", "launch_begin", "launch_progress", "launch_end",
  "logs_list", "logs_read", "logs_follow", "logs_unfollow", "logs_crash_reports",
  "registry_report_read",
  "registry_rollback",
  "saves_list", "saves_backups", "saves_backup", "saves_restore", "saves_delete",
  "editor_list_saves", "editor_file_info", "editor_read_file", "editor_zstd_decompress", "editor_zstd_compress",
  "editor_game_running", "editor_backup_file", "editor_write_save", "editor_list_backups", "editor_restore_backup", "editor_open_backups",
  "editor_storage_read", "editor_return_storage",
  "server_list", "server_get", "server_create", "server_import", "server_remove", "server_status", "server_start", "server_stop",
  "server_restart", "server_update", "server_deploy", "server_settings_get", "server_settings_set", "server_mods",
  "server_mods_sync", "server_mod_enable", "server_backups", "server_backup_create", "server_backup_restore", "server_query",
  "server_modpack_status", "server_modpack_build", "server_modpack_publish",
  "admin_connect", "admin_open_web_panel", "admin_close_web_panel", "admin_web_panels", "admin_web_panel_settings", "admin_web_panel_settings_set", "admin_disconnect", "admin_sessions", "admin_call", "admin_subscribe", "admin_unsubscribe",
  "modpack_pending", "modpack_inspect", "modpack_install", "modpack_dismiss",
  "config_kiln_read", "config_kiln_set", "config_kiln_reset",
  // Settings' ShroudForge card
  "sfi_backend_read", "sfi_backend_preview", "sfi_backend_set",
  "app_assets_status", "app_assets_import",
  "server_console_open", "server_console_close",
  "update_status", "update_check", "update_install", "update_switch", "update_restart", "update_skip", "update_ack",
  "library_add", "library_add_to_profile", "profile_revert", "profile_ensure_default",
  "deck_browse", "deck_gamepad", "deck_fullscreen", "deck_quit",
  "nexus_account", "nexus_sign_in", "nexus_sign_out", "nexus_search", "nexus_mod", "nexus_installed", "nexus_download", "nexus_links", "nexus_link_install", "nexus_link_dismiss", "nexus_handler_status", "nexus_handler_set",
  "engine_settings_get", "engine_settings_set", "engine_status", "engine_own_folders", "engine_adopt_folder", "eml_backup", "eml_restore", "eml_data_plan", "eml_data_apply", "eml_data_undo", "eml_data_latest",
  "chain_view", "chain_set_late", "shroudlink_notice",
  "found_scan", "found_add",
  "cook_plan", "cook_run", "boot_snapshot_run",
  "report_save", "report_summary", "report_preview", "report_note",
  "data_folder_get", "data_folder_size", "data_folder_check", "data_folder_move", "data_folder_use", "data_folder_reset",
  "data_folder_notice_ack", "data_folder_restart",
  "app_started", "app_close_seen", "app_close_unsaved", "app_render_frame", "app_render_set", "app_render_x11_set", "app_render_force_set", "app_render_report",
  "updates_check",
  "app_desktop_set",
  "app_locales",
  "app_sandbox_set",
  "app_dialog_default", "app_dialog_remember",
  "vendor_status", "vendor_license", "vendor_download", "vendor_remove",
  "community_settings_get", "community_settings_set", "community_status", "community_feed", "community_check_member", "community_signout", "community_signin_start", "community_signin_poll", "community_signin_cancel",
  "community_posts", "community_post_seen", "community_post_solved", "community_post_delete", "community_presence_facts", "community_report_prepare", "community_report_send",
  "tester_status", "tester_join", "tester_leave", "tester_result_set", "tester_results_send", "community_steam_keyboard",
  "pack_inspect", "pack_install", "pack_instance", "pack_instances", "pack_lacking", "pack_update_plan", "pack_update", "pack_undo", "pack_uninstall", "pack_build", "pack_verify", "pack_diff", "pack_secret_scan", "pack_peek_format", "pack_local_files", "pack_local_write", "pack_local_save", "pack_check", "crucible_status", "crucible_open", "crucible_install", "crucible_updates", "crucible_update_plan", "crucible_update", "crucible_repair", "crucible_share", "crucible_shared", "crucible_trust", "crucible_trust_set", "crucible_links", "crucible_resume", "crucible_clipboard",
  "studio_list", "studio_create", "studio_get", "studio_save", "studio_delete", "studio_credits_set", "studio_capture", "studio_capture_set", "studio_order", "studio_layer_files", "studio_layer_read", "studio_layer_write", "studio_layer_delete", "studio_layer_set", "studio_layer_lint", "studio_pick", "studio_fields", "studio_categories", "studio_catalog_status", "studio_catalog_ensure", "studio_analyze", "studio_test", "studio_test_report", "studio_test_end", "studio_build", "studio_publish_check", "studio_publish", "studio_set_visibility", "studio_server_pack",
  // the check before Play and the restore's BASE
  "launch_prepare", "launch_confirm", "launch_cancel", "eml_data_base",
] as const;
export type CommandName = (typeof COMMANDS)[number];

// ---------------------------------------------------------------- events (contracts.md section 10)

export interface KilnEvents {
  "kiln://task": Task;
  "kiln://game": { gameId: GameId; kind: "started" | "stopped" | "boot" | "mods" | "native" | "status" | "progress"; pid: number | null; status: GameStatus | null };
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
  /** The result of a mod update check (updates_check; api/updates.ts). */
  "kiln://mod-updates": import("./updates").CheckReport;
  /** A profile was written again without profile_activate and its notices wait: take them with activation_notices. */
  "kiln://activation": { gameId: GameId };
  /** The Discord features (community_*): something the Community page shows changed (`changed`: a sign-in, a post's
   *  state, the settings), or Kiln support replied (`reply`: the post, its title and link, who replied). */
  "kiln://community": { kind: "changed" | "reply"; post?: string; title?: string; url?: string; by?: string };
  /** A kiln://crucible/... link arrived: `count` wait in crucible_links (src/crucible/). */
  "kiln://crucible-link": { count: number };
}
export type KilnEventName = keyof KilnEvents;

/** Subscribes to a launcher event; resolves to the unsubscribe function. */
export async function onKilnEvent<E extends KilnEventName>(name: E, handler: (payload: KilnEvents[E]) => void): Promise<() => void> {
  if (isTauri()) return tauriListen<KilnEvents[E]>(name, (e) => handler(e.payload));
  return (await loadMock()).mockListen(name, handler as (payload: unknown) => void);
}

export interface SnapshotStartup { phase: "preparing" | "blocked" | "ready"; reasons: string[]; canContinue?: boolean }
export const snapshotStatus = () => call<SnapshotStartup>("snapshot_status");
export const snapshotContinue = () => call<null>("snapshot_continue");

export interface ShroudlinkNotice { mods: string[]; addons: LibraryRef[] }
export const shroudlinkNotice = (profile: Profile, gameId: GameId) => call<ShroudlinkNotice | null>("shroudlink_notice", { profile, gameId });

// ---------------------------------------------------------------- community: the optional Discord features
// docs/kiln/launcher/discord.md. The Rust side (src-tauri/src/commands/community.rs, kiln_manage::community) talks to
// the relay on frostdev.io and, for Rich Presence, to the Discord app on this PC; the browser mock answers in
// ./mockCommunity.ts (`?discord=` flags). The UI (src/community/) talks to Discord only through these.

/** `community.json`: the Community page shown (Settings), Rich Presence, reply notifications. */
/** `presencePack`: "Show my Crucible pack on my Discord profile" (off by default; older backends leave it out). */
export interface CommunitySettings { format: number; shown: boolean; presence: boolean; presencePack?: boolean; notify: boolean; relay?: string | null }
export type CommunitySettingsPatch = Partial<Pick<CommunitySettings, "shown" | "presence" | "presencePack" | "notify">>;
export interface CommunityAccount {
  /** Discord user id (a snowflake). */
  id: string;
  name: string;
  /** In the Kiln Discord. */
  member: boolean;
  tester: boolean;
}
export type PostStatus = "waiting" | "replied" | "solved" | "closed";
/** Who sees a report's zip: the Kiln team only (default), everyone in #support, nobody (not sent). */
export type ZipAudience = "team" | "public" | "none";
export interface SupportPost {
  id: string;
  title: string;
  /** https://discord.com/channels/<guild>/<thread> */
  url: string;
  created: string;
  status: PostStatus;
  replies: number;
  /** Replies the player has not seen in the launcher. */
  unread: number;
  lastReply: { name: string; team: boolean; at: string } | null;
  tags: string[];
  zip: ZipAudience;
}
export interface PresenceStatus {
  on: boolean;
  /** The Discord app's socket is there (looked for only while the switch is on). */
  discordRunning: boolean;
  /** Shown on Discord now: the game runs with Kiln. */
  showing: boolean;
}
/** What the page and the header show, from what the launcher already has (no request). */
export interface CommunityStatus {
  shown: boolean;
  account: CommunityAccount | null;
  online: boolean;
  relay: "ok" | "down";
  presence: PresenceStatus;
  notify: boolean;
  unread: number;
  /** When the posts were last read (offline: the time they show as of). */
  postsAsOf: string | null;
}
export interface FeedRelease { title: string; date: string; line: string; url: string }
export interface FeedEvent { title: string; start: string; end?: string | null; where?: string | null; url: string }
export interface CommunityFeed { releases: FeedRelease[]; events: FeedEvent[] }
export type SignInMode = "browser" | "qr";
export interface SignInStart {
  id: string;
  /** The short code the phone page shows too (QR pairing); empty in browser mode. */
  code: string;
  /** What the browser or the phone opens. */
  url: string;
  expiresAt: string;
}
export type SignInState =
  | { state: "waiting" }
  | { state: "approved"; account: CommunityAccount }
  | { state: "denied" | "expired" | "canceled" };
/** What the browser tab says after the desktop round trip. */
export interface SignInTexts { done: string; failed: string }
export type ProblemKind = "noload" | "mod" | "crash" | "launcher" | "server" | "other";
/** Where a report was started. */
export type SendFrom = "logs" | "banner" | "crash" | "community" | "deck";
export interface ReportDraft {
  title: string;
  kind: ProblemKind;
  /** Deck: when it happens (picked from a list). */
  when?: string | null;
  text: string;
  zip: ZipAudience;
}
export type FindingLevel = "problem" | "warning" | "info";
export interface PostFinding { id: string; level: FindingLevel; text: string }
export type KnownAction = "mods" | "runtime" | "logs" | "settings";
export interface KnownProblem {
  id: string;
  title: string;
  fix: string;
  /** A line to copy (a launch option). */
  copy?: string | null;
  /** The fix's own button. */
  action?: KnownAction | null;
  /** The button's words from the list; absent: the launcher's own for the action. */
  label?: string | null;
}
/** A support report prepared for sending: exactly what the check step shows and what Send sends. */
export interface ReportPreview {
  /** Send names this: the zip and the post go as prepared. */
  prepared: string;
  findings: PostFinding[];
  versions: [string, string][];
  /** The system's #support tag ("Linux", "Windows", "Steam Deck"). */
  tags: string[];
  /** Each kind's tag ("Mods", "Crash", ...; null: none): the post's first tag follows the kind the player picks. */
  kindTags: Record<ProblemKind, string | null>;
  /** The zip's size; `limit` what the relay takes. */
  size: number;
  limit: number;
  files: { path: string; size: number }[];
  known: KnownProblem | null;
  /** "What's in this report": every file with its group and replacements, those left out, what is never in it. */
  contents: ReportContents;
}
/** Rich Presence's words in the launcher's language (en.json community.presence.*). */
/** Rich Presence's lines; the last three for "Show my Crucible pack" ("{pack} ({count} mods)", "Get this modpack"). */
export interface PresenceTexts { details: string; stateOne: string; stateOther: string; getKiln: string; joinDiscord: string; packStateOne: string; packStateOther: string; getPack: string }
export type Capability = "fsr4" | "xess" | "framegen" | "deck";
export interface Hardware {
  gpu: string;
  driver: string;
  vram: string;
  os: string;
  runner: string;
  caps: Capability[];
  /** What this hardware can test that the team cannot (FSR 4, XeSS, frame generation, Steam Deck). */
  covers: string[];
}
export type StepResult = "worked" | "failed" | "skipped";
export interface TestStep {
  id: string;
  title: string;
  /** Plain steps, one per line. */
  how: string[];
  /** Who it is for; null: everyone. */
  for: { label: string; needs: Capability[] } | null;
  result: StepResult | null;
  note: string;
}
export interface TestPlan {
  build: string;
  /** The #testing post of this build. */
  url: string;
  until: string;
  /** Only the steps for this hardware. */
  steps: TestStep[];
  /** Steps for other hardware (not shown). */
  hidden: number;
  /** Results sent for this build, and whether the team read them. */
  sent: { at: string; read: { by: string; at: string } | null; replied: boolean } | null;
}
export interface TesterStatus {
  tester: boolean;
  /** The launcher follows test builds. */
  beta: boolean;
  /** What Volunteer would share (read on this PC; shown before anything is sent). */
  hardware: Hardware;
  plan: TestPlan | null;
}

export const communitySettingsGet = () => call<CommunitySettings>("community_settings_get");
export const communitySettingsSet = (patch: CommunitySettingsPatch) => call<CommunitySettings>("community_settings_set", { patch });
export const communityStatus = () => call<CommunityStatus>("community_status");
/** News and events (public; also refreshes the known-problems copy). Only when the Community page opens. */
export const communityFeed = () => call<CommunityFeed>("community_feed");
/** The player's posts, fresh from the relay. */
export const communityPosts = () => call<SupportPost[]>("community_posts");
export const communityPostSeen = (id: string) => call<null>("community_post_seen", { id });
export const communityPostSolved = (id: string) => call<null>("community_post_solved", { id });
export const communityPostDelete = (id: string) => call<null>("community_post_delete", { id });
/** `mode` browser: the desktop opens `url` in the browser; qr: the Deck shows it as a QR code with `code`. */
export const communitySignInStart = (mode: SignInMode, join: boolean, texts?: SignInTexts) => call<SignInStart>("community_signin_start", { mode, join, texts });
export const communitySignInPoll = (id: string) => call<SignInState>("community_signin_poll", { id });
export const communitySignInCancel = (id: string) => call<null>("community_signin_cancel", { id });
/** `forget`: frostdev.io deletes what it keeps about the player too; `posts`: and their support posts on Discord. */
export const communitySignOut = (forget: boolean, posts = false) => call<null>("community_signout", { forget, posts });
/** The account and membership again (the page opens, "I've joined"). */
export const communityCheckMember = () => call<CommunityAccount | null>("community_check_member");
/** The reply notification's words in the launcher's language. */
export interface NotifyTexts { title: string; detail: string; open: string }
/** What only the window knows: the mods on (the Play page's count; null when Rich Presence is off) and the words of
 *  Rich Presence and of the reply notification. */
export const communityPresenceFacts = (mods: number | null, texts: PresenceTexts, notify?: NotifyTexts) => call<null>("community_presence_facts", { mods, texts, notify });
/** `exclude`: other mods' files the player left out ("What's in this report"); each change prepares a new report. */
export const communityReportPrepare = (target: LogTarget | null, from: SendFrom, exclude: string[] = []) => call<ReportPreview>("community_report_prepare", { target, from, exclude });
export const communityReportSend = (prepared: string, draft: ReportDraft) => call<SupportPost>("community_report_send", { prepared, draft });
export const testerStatus = () => call<TesterStatus>("tester_status");
/** Sends the hardware tester_status showed and asks for the Tester role. */
export const testerJoin = (beta: boolean) => call<null>("tester_join", { beta });
export const testerLeave = () => call<null>("tester_leave");
/** `result` or `note` left out: that one stays as it is (a note typed after a pick never undoes the pick); "clear":
 *  back to Not tried. */
export const testerResultSet = (step: string, result?: StepResult | "clear", note?: string) => call<TestPlan | null>("tester_result_set", { step, result, note });
/** `prepared`: the support report the checklist showed (communityReportPrepare), for the team only when `withReport`. */
export const testerResultsSend = (withReport: boolean, prepared?: string) => call<TestPlan | null>("tester_results_send", { withReport, prepared });
/** Deck: asks Steam for its on-screen keyboard (steam://open/keyboard) for a field that takes words; nothing elsewhere. */
export const communitySteamKeyboard = () => call<null>("community_steam_keyboard");

// ---------------------------------------------------------------- Crucible packs (dev.30)
// The pack format (kiln_manage::modpack::pack, docs/kiln/launcher/crucible.md) and the Crucible client
// (crates/kiln-manage/src/crucible/; commands/{pack,crucible}.rs). Contract: dev30 crucible-client CONTRACT.md. The UI
// lives in src/crucible/; the mock in ./mockCrucible.ts.

export type PackNeed = "required" | "optional" | "unsupported";
export interface PackEnv { client: PackNeed; server: PackNeed }
export interface PackStoredFile { sha256: string; size: number; url?: string }
export interface PackCredit { author: string; source: string; match?: string }
export interface PackMod {
  kind: ModKind; id: string; version: string; name?: string; path: string; hash: string;
  dep: "required" | "optional"; default?: boolean; env?: PackEnv; file?: PackStoredFile; credit?: PackCredit;
}
export interface PackLayer { id: string; version: string; path: string; hash: string; file?: PackStoredFile }
export type PackSide = "both" | "client" | "server";
export type PackMergeMode = "merge-patch" | "ini" | "replace";
export interface PackOverride { path: string; mode: PackMergeMode; side: PackSide; sha256: string; size?: number }
export interface CrucibleRef { pack?: string; version?: string; slug?: string; code?: string }
export interface PackIndex {
  format: 2; kind: "pack"; id: string; name: string; version: string; created?: string; authors?: string[];
  description?: string; game?: { builds: number[] }; kiln?: string; api?: string; crucible?: CrucibleRef; mods: PackMod[];
  layer?: PackLayer; order: { kiln: string[]; late: string[] }; dropins?: ModKind[]; overrides?: PackOverride[];
  server?: { settings?: unknown; notes?: string }; changes?: string;
}
/** profiles/<pid>/pack/instance.json: a profile made from a pack. */
export interface PackInstance {
  format: 1; pack: string; name: string; version: string; sha256: string; url?: string;
  crucible?: CrucibleRef; optional: Record<string, boolean>; installed: string; updated?: string; previous?: string;
}
/** `name`: (UI ask, FROM-UI.md) the index's mod name: a mod the update adds is not in the Library yet. */
export interface PackPlanMod { kind: ModKind; id: string; version: string; enabled: boolean; name?: string }
export interface PackPlanUpdate { kind: ModKind; id: string; from: string; to: string; name?: string }
export interface PackKeyConflict { key: string; base: unknown; pack: unknown; mine: unknown }
/** `renamedFrom`: the pack renamed this settings file; the player's values move from the old name. */
export interface PackFileMerge { path: string; side: PackSide; mode: PackMergeMode; taken: string[]; kept: string[]; conflicts: PackKeyConflict[]; renamedFrom?: string }
/** A data field both the new pack layer and the player layer set: the player's value still wins (it loads later). */
export interface PackDataOverlap {
  /** The id for the update's `drop` ("Drop my change"). */
  key: string; section: string; target: string; field: string;
  pack: unknown; base?: unknown; mine: unknown; file: string;
  /** The pack changed this field since the installed version. */
  changed: boolean;
}
/** The player layer (local.<pid>): kept by the update, still loading last. */
export interface PackLocalPlan { id: string; files: number; fields: number; overlaps: PackDataOverlap[] }
export interface PackUpdatePlan {
  profile: string; pack: string; from: string; to: string; changes?: string;
  mods: { added: PackPlanMod[]; updated: PackPlanUpdate[]; removed: PackPlanMod[]; yours: PackPlanMod[]; keptSwitches: string[] };
  layer: { from?: string; to?: string; changed: boolean }; files: PackFileMerge[]; conflicts: number;
  local?: PackLocalPlan | null;
}
/** One finding of the pack analysis (kilnctl pack check). */
export interface PackCheckFinding { level: "error" | "warning" | "note"; code: string; message: string; mod?: string; file?: string }
/** pack_check: the analysis Crucible runs on uploads, run here on a .kilnpack file (kind "kiln.pack-check"). */
export interface PackCheckReport { ok: boolean; complete: boolean; summary: { errors: number; warnings: number; notes: number }; findings: PackCheckFinding[] }
/** A data file of the player layer (`content/<name>.json`). */
export interface PackLocalFile { path: string; text: string }
/** pack_inspect: a .kilnpack file before installing it. */
export interface PackPreview {
  index: PackIndex; sha256: string; size: number; missing: LibraryRef[]; present: LibraryRef[]; code: string[];
  optional: Record<string, boolean>; instance?: string | null;
  /** What that instance's profile loads from its installed version and the Library lacks (absent without an instance). */
  repair?: PackLacking | null;
  /** This file is the instance's installed version: crucibleRepair can take the missing mods from it. */
  installedVersion?: boolean;
}
/** What a pack profile loads from its installed version and the Library lacks ("Download missing mods"): an optional mod
 *  switched off, a mod the player took out or swapped and a server-only mod are not missing. */
export interface PackLacking {
  /** The installed version. */
  version: string;
  /** The mods (and the layer, "pack.<id>") to get back. */
  missing: LibraryRef[];
  /** Their names, in the order of `missing`. */
  names: string[];
  /** Bytes to download (from Crucible). */
  download: number;
}
/** What "Download missing mods" did (crucible_repair's task result). The profile, the instance and its version stay. */
export interface PackRepairResult {
  profile: ProfileId; version: string; imported: LibraryRef[];
  /** Bytes downloaded (0 from a file or the cache). */
  downloaded: number;
  /** Still missing (a Library entry edited in place). */
  left: LibraryRef[];
}
/** Local paths only. */
export type PackSource = { path: string; sha256?: string };
export interface PackInstallRequest { source: PackSource; consent: boolean; name?: string; optional?: Record<string, boolean>; activate?: boolean }
export interface PackInstallResult {
  profile: Profile; instance: PackInstance; imported: LibraryRef[]; overrides: string[];
  backup?: SaveBackup | null; activation?: Activation | null;
}
export interface PackBuildRequest { profile: ProfileId; version: string; id?: string; name?: string; description?: string; changes?: string }
export interface BuiltPack { file: string; sha256: string; size: number; index: PackIndex; notes: string[] }

export interface CrucibleStatus {
  /** false: hide every Crucible surface (no signing key baked into this build yet). */
  enabled: boolean;
  /** A dev override (another API/CDN/key) is in use: a small "Crucible (dev)" tag. */
  dev: boolean;
  api: string;
  /** The Discord sign-in (community session) is there. */
  signedIn: boolean;
  account: { id: string; name: string } | null;
}
export interface CrucibleUser { id: string; name: string; avatar?: string | null }
export interface CruciblePack {
  id: string; slug: string; code: string; url: string; title: string; summary: string; icon: string | null;
  owner: CrucibleUser | null; visibility: "link" | "public"; upvotes: number; comments: number; installs: number; mods: number; builds: number[];
}
export interface CrucibleVersion {
  id: string; number: number; version: string; changes: string | null; size: number; published: string | null;
  status: "uploading" | "ready" | "withdrawn";
  /** Crucible's check of this version (its checks.mjs report as GET /codes and /versions show it; passed through by
   *  kiln_manage's CrucibleVersion.checks). Absent from an older service. */
  checks?: CrucibleChecks | null;
}
/** Crucible's pack check (the sandboxed worker's `kilnctl pack check`, or only the quick index checks while it runs).
 *  `state`: queued/running/waiting = the worker's report is still to come; done = the full report; failed = the
 *  worker could not check it; off = this Crucible has no worker (the quick checks are all there is). */
export interface CrucibleChecks {
  state: "queued" | "running" | "waiting" | "done" | "failed" | "off";
  full?: boolean; ok?: boolean;
  errors?: string[]; warnings?: string[]; notes?: string[];
  summary?: { errors: number; warnings: number; notes: number };
}
export interface CruciblePreview {
  pack: CruciblePack;
  version: CrucibleVersion;
  /** The verified (Ed25519) signed index. */
  index: PackIndex;
  /** Bytes of every file of the pack. */
  size: number;
  /** Bytes still to download (what the Library lacks). */
  download: number;
  /** Client mods + layer the Library lacks (downloaded). */
  missing: LibraryRef[];
  /** Already in the Library (kept, not downloaded). */
  present: LibraryRef[];
  /** Mod ids that carry code. */
  code: string[];
  /** Optional mod id -> default switch. */
  optional: Record<string, boolean>;
  /** Profile id of an installed instance of this pack (the preview offers Update instead). */
  instance: string | null;
  /** What that instance's profile loads from its installed version and the Library lacks (absent without an instance;
   *  `missing` and `download` above are of the version shown, for a new install). */
  repair?: PackLacking | null;
  /** A yellow note when !fits. */
  game: { builds: number[]; current: number | null; fits: boolean };
  /** Red, blocks the install, when !ok. */
  kiln: { needs: string | null; ok: boolean };
  /** The player ticked "Always trust packs from <owner>" before. */
  trusted: boolean;
}
/** crucible_open's `busy` error detail while the creator's upload is not finished (no preview yet): "still uploading"
 *  + Wait (crucible_open again every ~10 s). */
export interface CrucibleUploading { uploading: { version: string; progress: number }; pack: CruciblePack }
export interface CrucibleInstallRequest {
  /** The version id (pv_...) from the preview. */
  target: string;
  consent: boolean; trustAlways?: boolean; optional?: Record<string, boolean>; name?: string; play?: boolean;
}
export interface CrucibleInstallResult {
  profile: Profile; instance: PackInstance; imported: LibraryRef[]; overrides: string[];
  /** The profile active before (the toast's Undo: profile_activate(previous)). */
  previous: string | null;
  /** Bytes downloaded. */
  downloaded: number;
}
export interface CrucibleUpdate {
  profile: string; pack: string; name: string; from: { version: string; number: number | null };
  to: { versionId: string; version: string; number: number };
  /** (UI ask, FROM-UI.md) The pack's owner, and the counts of the signed indexes' diff: the Play banner's
   *  "3 -> 4 by Emberfox: 1 added, 2 updated, 1 removed" without a download. Absent: the banner says less. */
  by?: string | null;
  counts?: { added: number; updated: number; removed: number };
}
export interface CrucibleUpdatePlan { versionId: string; download: number; plan: PackUpdatePlan }
export type PackChoice = "pack" | "mine";
export interface CrucibleUpdateRequest {
  profile: string; versionId: string; consent: boolean; optional?: Record<string, boolean>;
  /** "<path>#<key>" (as the plan names them) -> pack or mine. */
  resolve?: Record<string, PackChoice>; all?: PackChoice;
  /** Player-layer fields to let go ("Drop my change"): `plan.local.overlaps[].key`. */
  drop?: string[];
}
/** `backups`: the save backups taken first (one per game the instance is active on). */
export interface CrucibleUpdateResult { plan: PackUpdatePlan; profile: Profile; instance: PackInstance; imported: LibraryRef[]; backups?: SaveBackup[] }
/** "Come back after installing Kiln": the first start with this data folder, and a pack code the download carried. */
/** links::Resume. firstStart is also true at the first start after an update from a launcher without Crucible: only
 *  packWaiting (a Get Kiln download whose name lost its code, at the first start) lets the start read the clipboard. */
export interface CrucibleResume { firstStart: boolean; code: string | null; packWaiting: boolean }
/** Same profile again = a new version of its pack. */
export interface CrucibleShareRequest { profile: string; title?: string }
export interface CrucibleShareResult {
  pack: CruciblePack; version: CrucibleVersion; files: number;
  /** Bytes sent. */
  uploaded: number;
  /** Bytes Crucible already had. */
  skipped: number;
  /** Always false from Rust: the UI copies the link itself. */
  copied: boolean;
}
export interface CrucibleShared {
  profile: string; pack: CruciblePack; version: CrucibleVersion;
  /** null: the profile matches what was shared. */
  changed: { added: string[]; updated: string[]; removed: string[] } | null;
  /** Account-owned shares still require sign-in to update; native owner capabilities never cross IPC. */
  requiresSignIn: boolean;
}
export interface CrucibleTrust { id: string; name: string; since: string }

export const crucibleStatus = () => call<CrucibleStatus>("crucible_status");
/** A code ("H7QK-3MZP", any case, with or without the dash), a share link, a kiln:// link or a version id (pv_...). */
export const crucibleOpen = (target: string, gameId?: GameId | null) => call<CruciblePreview>("crucible_open", { target, gameId: gameId ?? null });
export const crucibleInstall = (gameId: GameId, request: CrucibleInstallRequest) => call<TaskId>("crucible_install", { gameId, request });
export const crucibleUpdates = (gameId: GameId, force = false) => call<CrucibleUpdate[]>("crucible_updates", { gameId, force });
/** Downloads what it needs: may take seconds. */
export const crucibleUpdatePlan = (profileId: ProfileId) => call<CrucibleUpdatePlan>("crucible_update_plan", { profileId });
/** Error `conflict` with the PackUpdatePlan in `detail` while a conflict has no choice. */
export const crucibleUpdate = (gameId: GameId, request: CrucibleUpdateRequest) => call<TaskId>("crucible_update", { gameId, request });
/** "Download missing mods" of a pack profile (a task; result PackRepairResult): from Crucible, or for a pack installed
 *  from a file from `path`, the file of its installed version. Writes only the Library: nothing is loaded or started,
 *  allowed while the game runs. */
export const crucibleRepair = (gameId: GameId | null, request: { profile: ProfileId; path?: string }) => call<TaskId>("crucible_repair", { gameId, request });
/** Error `auth_failed` when not signed in. */
export const crucibleShare = (request: CrucibleShareRequest, gameId?: GameId | null) => call<TaskId>("crucible_share", { request, gameId: gameId ?? null });
export const crucibleShared = (profileId: ProfileId) => call<CrucibleShared | null>("crucible_shared", { profileId });
export const crucibleTrust = () => call<CrucibleTrust[]>("crucible_trust");
export const crucibleTrustSet = (id: string, name: string, trusted: boolean) => call<null>("crucible_trust_set", { id, name, trusted });
/** kiln:// links that arrived (drained); "kiln://crucible-link" {count} says one did. */
export const crucibleLinks = () => call<string[]>("crucible_links");
/** Asked once at start: a code the download carried, else whether a pack page's download left a pack waiting (then
 *  the UI looks at the clipboard once, through crucibleClipboard). */
export const crucibleResume = () => call<CrucibleResume>("crucible_resume");
/** The clipboard's text, read by the backend (never navigator.clipboard: WebView2 would ask the player), only when it
 *  may be a Crucible code or link; null otherwise. Never log it. */
export const crucibleClipboard = () => call<string | null>("crucible_clipboard");
/** The player layer of an instance: its data files; write (null removes) and save (into the Library, last). */
export const packLocalFiles = (profileId: ProfileId) => call<PackLocalFile[]>("pack_local_files", { profileId });
export const packLocalWrite = (profileId: ProfileId, path: string, text: string | null) => call<null>("pack_local_write", { profileId, path, text });
export const packLocalSave = (profileId: ProfileId) => call<Profile>("pack_local_save", { profileId });
/** The pack analysis of a .kilnpack on this PC (may take seconds for a large pack). */
export const packCheck = (path: string) => call<PackCheckReport>("pack_check", { path });
export const packInstance = (profileId: ProfileId) => call<PackInstance | null>("pack_instance", { profileId });
export const packInstances = () => call<[ProfileId, PackInstance][]>("pack_instances");
/** What a pack profile loads from its installed version and the Library lacks (nothing downloaded; reads the
 *  Library's records and its cheap changed check, no full hash of the pack's mods). */
export const packLacking = (profileId: ProfileId) => call<PackLacking>("pack_lacking", { profileId });
export const packUndo = (profileId: ProfileId) => call<Profile>("pack_undo", { profileId });
export const packUninstall = (profileId: ProfileId) => call<{ profile: Profile; unused: LibraryRef[] }>("pack_uninstall", { profileId });
/** "Save as file": a .kilnpack of a profile into a folder. */
export const packBuild = (request: PackBuildRequest, outDir: string) => call<TaskId>("pack_build", { request, outDir });
export const packInspect = (source: PackSource) => call<PackPreview>("pack_inspect", { source });
/** 1: a server modpack (the consent flow), 2: a Crucible pack (the pack preview). */
export const packPeekFormat = (path: string) => call<1 | 2>("pack_peek_format", { path });
export const packInstall = (gameId: GameId, request: PackInstallRequest) => call<TaskId>("pack_install", { gameId, request });

// ---------------------------------------------------------------- Pack Studio (dev.30)
// The creator's tool (DESIGN 3; kiln_manage::modpack::studio, crucible::publish; commands/studio.rs; docs
// launcher/pack-studio.md). A pack source is <data>/studio/<id>/: its profile's mods, order and settings, the chosen
// settings keys, the pack layer (content/*.json), credits, what was published. The UI lives in src/studio/; the mock
// in ./mockStudio.ts.
export type StudioVisibility = "link" | "public";
export interface StudioModChoice { dep: "required" | "optional"; default?: boolean; env?: PackEnv }
export interface StudioOverrideSpec { path: string; side: PackSide; keys: string[] }
export interface StudioPublishedVersion { version: string; id: string; published: string; visibility: StudioVisibility; file?: string; pending?: boolean }
export interface StudioPublished { pack: string; slug?: string; code?: string; url?: string; visibility: StudioVisibility; versions: StudioPublishedVersion[] }
/** kiln-pack.json. */
export interface PackSourceDoc {
  format: number; kind: "pack-source"; id: string; name: string; version: string; profile: ProfileId;
  summary?: string | null; description?: string | null; authors?: string[]; tags?: string[]; changes?: string | null;
  mods: Record<string, StudioModChoice>; overrides: StudioOverrideSpec[]; serverSettings?: { gameSettings?: Record<string, Json> } | null;
  visibility: StudioVisibility; gameBuilds?: number[]; published?: StudioPublished | null; testProfile?: ProfileId | null; testStarted?: string | null;
  created: string; updated: string;
}
export interface StudioMod {
  kind: ModKind; id: string; name: string; version: string; hash: string; enabled: boolean; side?: "client" | "server" | "both";
  dep: "required" | "optional"; default?: boolean; env: PackEnv; credit?: PackCredit;
}
export interface Studio {
  dir: string; source: PackSourceDoc; mods: StudioMod[]; credits: Record<string, PackCredit>; profileName?: string; layerFiles: number;
  /** Changes with anything a build reads (source, credits, layer files, profile): the analysis is made again. */
  revision: string;
}
export interface StudioSummary { id: string; name: string; version: string; profile: ProfileId; mods: number; updated: string; published?: StudioPublished }
export interface StudioNewPack { name: string; from?: ProfileId | null; id?: string }
export interface StudioCaptureKey { key: string; value: Json; default?: Json; chosen: boolean; personal: boolean; secret?: string }
export interface StudioCaptureFile {
  path: string; mod?: string; mode: PackMergeMode; defaults: boolean; keys: StudioCaptureKey[]; whole: boolean; side: PackSide; size: number; refused?: string;
}
export interface StudioCaptureChoice { path: string; side: PackSide; keys: string[]; whole?: boolean }
export interface StudioOrderRow { id: string; name: string; loads: boolean; reason?: string; layer: boolean }
export interface StudioOrder { kiln: StudioOrderRow[]; late: string[]; warnings: string[] }
export interface StudioLayerFile { path: string; size: number }
/** `hidden`: one of the game's old, test or unused entries (kiln_manage::catalog::is_hidden_name), listed only when asked. */
export interface StudioPickHit { section: string; name: string; id?: string; displayName?: string; from?: string; hidden?: boolean }
/** A recipe input category of the game (kiln_manage::catalog::ItemCategory): recipe inputs {"category": id} take any of
 *  its items; the game names none, so the editor words it by its items. `recipes`: the game's recipes that take it. */
export interface StudioItemCategory {
  id: string; name?: string;
  items: { name: string; displayName?: string; hidden?: boolean }[];
  recipes: { name: string; displayName?: string }[];
}
/** A catalog section's field (kiln_manage::catalog::FieldInfo). */
/** Pack Studio's game-data catalog upkeep (kiln_manage::catalog::auto): made by Kiln when it is missing or not current. */
export type StudioCatalogState = "idle" | "building" | "ready" | "failed" | "noGame";
export interface StudioCatalogStatus { state: StudioCatalogState; exists: boolean; path?: string; built?: boolean; seconds?: number; error?: KilnError }
export interface CatalogFieldInfo { path: string; type: string; writable?: boolean; enum?: string[]; enumNumbers?: number[]; kind?: string }
/** kiln_manage::modpack::check::CheckReport (kilnctl pack check --json). */
export interface PackCheckFinding { level: "error" | "warning" | "note"; code: string; message: string; mod?: string; file?: string }
export interface PackCreditRow {
  kind: ModKind; id: string; version: string; kdh1: string; author: string | null; source: string | null;
  match: "kiln" | "crucible" | "github" | "nexus" | "manifest" | "manual" | string;
  confidence: "certain" | "likely" | "conflict" | "unverified" | "creator" | "unconfirmed" | "none" | string;
  nexusModId?: number; flag?: string;
}
export interface PackDataSetter { mod: string; file: string; selector: string; value: Json }
export interface PackDataConflict { section: string; field: string; earlier: PackDataSetter; later: PackDataSetter; conditional: boolean }
export interface PackCheckReport {
  format: number; kind: string; tool: string; version: string; ok: boolean; complete: boolean;
  pack: { id: string; name: string; version: string; sha256?: string; size?: number } | null;
  summary: { errors: number; warnings: number; notes: number };
  findings: PackCheckFinding[];
  mods: { kind: ModKind; id: string; version: string; hash: string; name?: string; authors?: string[]; url?: string; code: { managed: string[]; native: string[]; scripts: string[]; touches: string[] }; blocked: boolean }[];
  conflicts: PackDataConflict[];
  credits: PackCreditRow[];
  catalog: boolean;
}
export interface StudioTestReport { profile?: ProfileId; ran: boolean; conflicts: string[]; problems: string[] }
export interface StudioPublishRequest {
  version: string; changes?: string | null; visibility: StudioVisibility; attest: boolean; gameBuilds?: number[];
  /** Publish as a new pack although the source names one (deleted or taken down on Crucible). */
  fresh?: boolean;
}
export interface StudioVisibilityResult { pack: CruciblePack; public: boolean; needs: string[] }
export interface StudioPublishResult {
  pack: CruciblePack; version: CrucibleVersion; files: number; uploaded: number; skipped: number;
  /** The pack is public now. */
  public: boolean;
  /** What Crucible still wants before it can be public. */
  needs: string[];
  file: string;
}
export interface PackServerInstallResult { mods: unknown[]; imported: LibraryRef[]; overrides: string[]; settings: boolean; leftOut: string[]; modpack?: BuiltModpack | null }

export const studioList = () => call<StudioSummary[]>("studio_list");
export const studioCreate = (request: StudioNewPack) => call<Studio>("studio_create", { request });
export const studioGet = (packId: string) => call<Studio>("studio_get", { packId });
/** Saves the source (id, profile, published and test fields stay as stored). */
export const studioSave = (packId: string, source: PackSourceDoc) => call<Studio>("studio_save", { packId, source });
export const studioDelete = (packId: string, gameId?: GameId | null) => call<null>("studio_delete", { packId, gameId: gameId ?? null });
export const studioCreditsSet = (packId: string, credits: Record<string, PackCredit>) => call<Studio>("studio_credits_set", { packId, credits });
/** The profile's settings files and the keys that differ from the mods' defaults (with a game: copied back first). */
export const studioCapture = (packId: string, gameId?: GameId | null) => call<StudioCaptureFile[]>("studio_capture", { packId, gameId: gameId ?? null });
export const studioCaptureSet = (packId: string, choices: StudioCaptureChoice[]) => call<Studio>("studio_capture_set", { packId, choices });
export const studioOrder = (packId: string) => call<StudioOrder>("studio_order", { packId });
export const studioLayerFiles = (packId: string) => call<StudioLayerFile[]>("studio_layer_files", { packId });
export const studioLayerRead = (packId: string, path: string) => call<string>("studio_layer_read", { packId, path });
/** Writes content/<name>.json and answers the layer's lint. */
export const studioLayerWrite = (packId: string, path: string, text: string) => call<LintReport>("studio_layer_write", { packId, path, text });
export const studioLayerDelete = (packId: string, path: string) => call<LintReport>("studio_layer_delete", { packId, path });
/** "Set in the pack": {section: {selector: {field: value}}} in content/pack.json (null removes). */
export const studioLayerSet = (packId: string, section: string, selector: string, field: string, value: Json) =>
  call<LintReport>("studio_layer_set", { packId, section, selector, field, value });
export const studioLayerLint = (packId: string) => call<LintReport>("studio_layer_lint", { packId });
/** Game entries (the catalog) and what the pack's mods add, for the data editors' pickers. */
export const studioPick = (packId: string, section: string, query: string, limit = 50, hidden = false) => call<StudioPickHit[]>("studio_pick", { packId, section, query, limit, hidden });
/** The game's recipe input categories (the recipe editor's "Any of" ingredient); empty without a catalog. */
export const studioCategories = () => call<StudioItemCategory[]>("studio_categories");
export const studioFields = (section: string) => call<CatalogFieldInfo[]>("studio_fields", { section });
/** Where the catalog's upkeep is (polled while it builds). */
export const studioCatalogStatus = () => call<StudioCatalogStatus>("studio_catalog_status");
/** Pack Studio opening and "Build the game catalog": a check in the background, the catalog made when missing or not current. */
export const studioCatalogEnsure = () => call<StudioCatalogStatus>("studio_catalog_ensure");
/** The pack analysis (a task; result PackCheckReport): checks, code scan, data conflicts, prefilled credits. */
export const studioAnalyze = (packId: string) => call<TaskId>("studio_analyze", { packId });
/** A task (result PackInstallResult): "<name> (test)" built, installed and activated; then Play. */
export const studioTest = (packId: string, gameId?: GameId | null) => call<TaskId>("studio_test", { packId, gameId: gameId ?? null });
export const studioTestReport = (packId: string, gameId?: GameId | null) => call<StudioTestReport>("studio_test_report", { packId, gameId: gameId ?? null });
export const studioTestEnd = (packId: string, gameId?: GameId | null) => call<null>("studio_test_end", { packId, gameId: gameId ?? null });
/** "Save as file" (a task; result BuiltPack). */
export const studioBuild = (packId: string, outDir: string) => call<TaskId>("studio_build", { packId, outDir });
/** What publishing would still need (empty: ready); sends nothing. */
export const studioPublishCheck = (packId: string, request: StudioPublishRequest) => call<string[]>("studio_publish_check", { packId, request });
/** A task (result StudioPublishResult); auth_failed when not signed in. */
export const studioPublish = (packId: string, request: StudioPublishRequest, gameId?: GameId | null) =>
  call<TaskId>("studio_publish", { packId, request, gameId: gameId ?? null });
/** "Make public" / "Link only" for a published pack (no new version); Crucible's refusal comes back as needs. */
export const studioSetVisibility = (packId: string, visibility: StudioVisibility, attest: boolean) =>
  call<StudioVisibilityResult>("studio_set_visibility", { packId, visibility, attest });
/** A server pack from a published version, on one of the launcher's servers (a task; result PackServerInstallResult). */
export const studioServerPack = (packId: string, version: string, serverId: ServerId) => call<TaskId>("studio_server_pack", { packId, version, serverId });
