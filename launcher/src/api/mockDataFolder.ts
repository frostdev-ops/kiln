// Mock answers for the data folder commands (data_folder.rs), merged into ./mock.ts's handlers. The start comes from
// the URL, `?df=`:
//   (none)       the default place, nothing to report
//   pointer      a folder the player chose earlier (source "pointer")
//   env          KILN_DATA_DIR is set: the row shows the path and Open, Change is off
//   broken       the pointer names a folder that is gone: the dialog over the whole app; every other command fails
//   unreadable   the same, with a folder that is there but not a Kiln data folder
//   elsewhere    another launcher is moving the data folder: "Kiln is moving its data in another window", Quit
//   moved        the first start after a move: the notice, nothing left behind
//   moved-left   the same, with entries Kiln left on purpose and a file it could not remove
//   moved-removing   the same, while the task removes the old copy
//   moved-unfinished the same, the removal stopped (it goes on at the next start)
//   moved-refused    the same, the removal refused (a game's profile still names the old folder): its reason
//   undone       an unfinished move undone at this start
//   undone-left  the same, part of the partial copy could not be removed (the next start tries again)
//   unclear      a move's record that matches neither folder
//   fail         every move fails half-way
// Folders a test picks (the browser has no folder dialog): `__kilnDataFolderMock.pick(path | null)` queues the next
// answer of the picker (null: canceled); without one the picker answers /mnt/games. What a picked folder gives:
//   /mnt/games                 move (an empty folder: used as it is)
//   /mnt/storage               move into /mnt/storage/Kiln (the folder has other files)
//   /media/player/USB stick    move; Enshrouded gets copies of its mods from there, and a disk without file permissions
//   /mnt/stays                 move; two entries the launcher does not know stay in the old folder too
//   /mnt/old-kiln, /mnt/usb/Kiln   use (already a Kiln data folder)
//   the paths in REFUSALS      refuse, with that sentence; `busy(kind)` refuses every folder (game, server, launcher)
//   anything else              move, used as it is
// Also: `failNext(message)`, `sizeDelay(ms)`, `stepMs(ms)` (each step of the move task), `refusals()`, `state()`, `interruptRemoval(on)` (the move's last phase,
// the removal of the old copy, is stopped: the task still ends as done, as the backend's does after a cancel there).
import type { DataFolder, DataFolderMoved, DataFolderPlan, DataFolderSize, KilnError } from "./commands";

type Args = Record<string, unknown>;
type StartTask = (kind: string, title: string, steps: string[], finish: () => unknown, stepMs?: number) => string;

const HOME = "/home/player";
export const MOCK_DEFAULT_DATA = `${HOME}/.local/share/kiln`;
const GAME = `${HOME}/.local/share/Steam/steamapps/common/Enshrouded`;
const LONG = `/mnt/archive/${"a folder with a long name/".repeat(9)}kiln`;

/** Every refusal the mock knows (`kind`: the crate's RefusalCode): by the folder picked, or (no path) by what runs
 *  (`busy`). */
export const REFUSALS: { kind: string; path: string | null; reason: string }[] = [
  { kind: "game_running", path: null, reason: "Close Enshrouded first: it reads its mods from the data folder while it runs." },
  { kind: "server_running", path: null, reason: "Stop the server Ember Isles first: it reads its mods from the data folder while it runs." },
  { kind: "locked", path: null, reason: "Another Kiln Launcher is open. Close it first." },
  { kind: "same_folder", path: MOCK_DEFAULT_DATA, reason: "This is the data folder already." },
  { kind: "inside_current", path: `${MOCK_DEFAULT_DATA}/library`, reason: "This folder is inside the data folder." },
  { kind: "contains_current", path: `${HOME}/.local/share`, reason: "The data folder is inside this folder. Pick a folder outside it." },
  { kind: "inside_game", path: `${GAME}/mods`, reason: "This folder is inside a game folder. Pick a folder outside it." },
  { kind: "not_writable", path: "/mnt/readonly", reason: "Kiln cannot write to this folder." },
  { kind: "too_small", path: "/mnt/small", reason: "Not enough free space there: 3.4 GB is needed, 1.2 GB is free." },
  { kind: "kiln_subfolder_taken", path: "/mnt/shared", reason: "There is a folder named Kiln in it that is not a Kiln data folder." },
  { kind: "path_too_long", path: LONG, reason: "Paths in the library would be longer than Windows allows. Pick a folder with a shorter path." },
];

const USE = new Set(["/mnt/old-kiln", "/mnt/usb/Kiln"]);
const SIZE: DataFolderSize = { bytes: 3_650_000_000, files: 12_408 };

const LEFT = { from: MOCK_DEFAULT_DATA, to: "/mnt/games/Kiln" };
const STAYED = [
  { name: "my-notes.txt", bytes: 12_400, path: `${MOCK_DEFAULT_DATA}/my-notes.txt` },
  { name: "old-mods", bytes: 1_250_000_000, path: `${MOCK_DEFAULT_DATA}/old-mods` },
];

function start(): DataFolder {
  const q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("df") : null;
  const base: DataFolder = { path: MOCK_DEFAULT_DATA, defaultPath: MOCK_DEFAULT_DATA, source: "default", problem: null, notice: null, elsewhere: false };
  const moved = { ...base, path: "/mnt/games/Kiln", source: "pointer" as const };
  const notice = (n: Partial<NonNullable<DataFolder["notice"]>>) => ({ ...moved, notice: { kind: "moved" as const, ...LEFT, left: [], stayed: [], kept: [], removing: false,
    unfinished: false, folderRemoved: true, reason: null, ...n } });
  switch (q) {
    case "pointer": return moved;
    case "env": return { ...base, path: "/srv/kiln-data", source: "env" };
    case "elsewhere": return { ...base, elsewhere: true };
    case "broken": return { ...base, source: "pointer", problem: { path: "/mnt/usb/Kiln", kind: "missing", reason: "The folder is not there. If it is on a drive that is not connected, connect it and start Kiln again." } };
    case "unreadable": return { ...base, source: "pointer", problem: { path: "/mnt/games/Kiln", kind: "notKiln", reason: "The folder is there but holds no Kiln data." } };
    case "moved": return notice({});
    case "moved-left": {
      const kept = [{ path: `${MOCK_DEFAULT_DATA}/profiles/ember-run/config/kiln.json`, bytes: 2_048, reason: "it changed after the move" }];
      return notice({ stayed: STAYED, kept, left: [...STAYED.map((e) => e.path), ...kept.map((k) => k.path)], folderRemoved: false });
    }
    case "moved-removing": return notice({ removing: true, folderRemoved: false });
    case "moved-unfinished": return notice({ unfinished: true, folderRemoved: false });
    case "moved-refused": return notice({ folderRemoved: false,
      reason: `Enshrouded still uses ${MOCK_DEFAULT_DATA}: activate that profile again, then the old copy goes at the next start.` });
    case "undone-left": return { ...base, notice: { kind: "undone", from: MOCK_DEFAULT_DATA, to: "/mnt/games/Kiln", left: ["/mnt/games/Kiln/backups"], stayed: [],
      kept: [{ path: "/mnt/games/Kiln/backups", bytes: 2_400, reason: "it could not be removed: Permission denied (os error 13)" }], removing: false,
      unfinished: false, folderRemoved: false, reason: "Parts of the partial copy in /mnt/games/Kiln could not be removed: the folder stays marked as an unfinished move." } };
    case "undone": return { ...base, notice: { kind: "undone", from: MOCK_DEFAULT_DATA, to: "/mnt/games/Kiln", left: [], stayed: [], kept: [], removing: false, unfinished: false, folderRemoved: true, reason: null } };
    case "unclear": return { ...moved, notice: { kind: "unclear", ...LEFT, left: [], stayed: [], kept: [], removing: false, unfinished: false, folderRemoved: false,
      reason: `The launcher uses /mnt/games/Kiln, but the move went from ${MOCK_DEFAULT_DATA} to /mnt/other/Kiln: nothing is removed.` } };
    default: return base;
  }
}

const FAIL_URL = typeof location !== "undefined" && new URLSearchParams(location.search).get("df") === "fail";

let folder: DataFolder = start();
const picks: (string | null)[] = [];
let failNext: string | null = FAIL_URL ? "Could not copy library/kiln/new-items: the disk reported an input/output error." : null;
let busy: string | null = null;
let sizeDelay = 900;
let interrupt = false;
let reactivation: string | null = null;
let stepMs = 600;

/** The browser's stand-in for the folder dialog (ui: shell/dataFolder.ts pickFolder). */
export function mockPickFolder(): string | null {
  return picks.length ? picks.shift()! : "/mnt/games";
}

/** The data folder in use (app_info.dataDir). */
export const mockDataDir = () => folder.path;

/** Commands that still answer while the pointer is broken, or another launcher moves the folder; every other one
 *  fails then, as in the backend (src-tauri/src/folder.rs). */
const OPEN_WHEN_BROKEN = new Set(["app_info", "data_folder_get", "data_folder_use", "data_folder_reset", "data_folder_restart", "deck_quit"]);
const OPEN_WHEN_ELSEWHERE = new Set(["app_info", "data_folder_get", "data_folder_restart", "deck_quit"]);
export function dataFolderBlocks(cmd: string): KilnError | null {
  if (folder.problem && !OPEN_WHEN_BROKEN.has(cmd)) return { code: "data_folder", message: folder.problem.reason };
  if (folder.elsewhere && !OPEN_WHEN_ELSEWHERE.has(cmd)) return { code: "busy", message: "Kiln is moving its data in another window." };
  return null;
}

function plan(path: string): DataFolderPlan {
  const picked = path.replace(/(?<=.)[\\/]+$/, "");
  const base: DataFolderPlan = { picked, target: picked, action: "move", reason: null, bytes: SIZE.bytes, files: SIZE.files, free: 412_000_000_000,
    needed: 4_100_000_000, sameVolume: false, hardLinks: false, copyMode: [], stays: [], warnings: [] };
  const refuse = (code: string, reason: string): DataFolderPlan => ({ ...base, action: "refuse", reason, code });
  if (busy) return refuse(busy, REFUSALS.find((r) => r.kind === busy)?.reason ?? busy);
  const r = REFUSALS.find((x) => x.path === picked);
  if (r) return refuse(r.kind, r.reason);
  if (USE.has(picked)) return { ...base, action: "use", bytes: 0, files: 0 };
  if (picked === "/mnt/storage" || picked === "/run/media/mmcblk0p1") return { ...base, target: `${picked}/Kiln` };
  if (picked === "/media/player/USB stick") return { ...base, free: 58_000_000_000, copyMode: ["Enshrouded"],
    warnings: [`${picked} does not keep file permissions: from there, Mod Manager Download links from the browser do not reach the launcher, and a Nexus Mods login kept without a keyring is refused.`] };
  if (picked === "/mnt/stays") return { ...base, stays: STAYED };
  if (picked.startsWith(`${HOME}/`)) return { ...base, sameVolume: true, free: 96_000_000_000 };
  return base;
}

export function dataFolderMockHandlers(startTask: StartTask): Record<string, (a: Args) => unknown> {
  const api = {
    pick: (p: string | null) => { picks.push(p); },
    failNext: (message: string | null) => { failNext = message; },
    busy: (kind: string | null) => { busy = kind; },
    sizeDelay: (ms: number) => { sizeDelay = ms; },
    interruptRemoval: (on: boolean) => { interrupt = on; },
    /** A game's profile could not be written for the new place: the move skips its removal, as the backend does. */
    reactivationFails: (game: string | null) => { reactivation = game; },
    stepMs: (ms: number) => { stepMs = ms; },
    refusals: () => REFUSALS,
    state: () => structuredClone(folder),
  };
  if (typeof window !== "undefined") (window as unknown as { __kilnDataFolderMock?: unknown }).__kilnDataFolderMock = api;
  const rejectWith = (code: KilnError["code"], message: string): never => { throw { code, message } satisfies KilnError; };
  return {
    data_folder_get: () => folder,
    data_folder_size: async () => { await new Promise((r) => setTimeout(r, sizeDelay)); return SIZE; },
    data_folder_check: (a) => plan(String(a.path ?? "")),
    data_folder_move: (a) => {
      const p = plan(String(a.path ?? ""));
      if (p.action !== "move") rejectWith("refused", p.reason ?? "This folder is a Kiln data folder already: use it instead.");
      const from = folder.path;
      const fail = failNext;
      failNext = FAIL_URL ? fail : null;
      return startTask("data_folder_move", "Move the data folder",
        ["copying library/kiln/new-items/1.0.0/kiln.mod.json", "copying runtime/kiln-runtime-0.2.0-dev.zip", "checking library/kiln/new-items/1.0.0/kiln.mod.json",
          "switching to the new place", "removing the old copy: library/kiln/new-items/1.0.0/kiln.mod.json"],
        (): DataFolderMoved => {
          if (fail) rejectWith("io", fail);
          // the running launcher keeps the old folder until it restarts (data_folder_restart)
          if (reactivation) {
            return { from, to: p.target, stayed: p.stays, warnings: [`The profile of ${reactivation} could not be written for the new place.`],
              cleanup: { done: false, interrupted: false, folderRemoved: false, kept: [],
                reason: `${reactivation} still uses ${from}: activate that profile again, then the old copy goes at the next start.` } };
          }
          return { from, to: p.target, warnings: [], stayed: p.stays,
            cleanup: { done: !interrupt, interrupted: interrupt, folderRemoved: !p.stays.length && !interrupt, kept: [], reason: null } };
        }, stepMs);
    },
    data_folder_use: (a) => {
      const p = String(a.path ?? "").replace(/(?<=.)[\\/]+$/, "");
      if (!USE.has(p)) rejectWith("refused", "This folder holds no Kiln data.");
      return { ...folder, path: p, source: "pointer", problem: null, notice: null } satisfies DataFolder;
    },
    data_folder_reset: () => {
      if (!folder.problem) rejectWith("refused", "Only for a data folder that cannot be used.");
      return { ...folder, path: folder.defaultPath, source: "default", problem: null } satisfies DataFolder;
    },
    data_folder_notice_ack: () => { folder = { ...folder, notice: null }; return null; },
    // a real restart ends this page; here the call is recorded (`__kilnMock.calls()`) and never answers
    data_folder_restart: () => new Promise(() => {}),
  };
}
