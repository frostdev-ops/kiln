// Mock answers for the data folder commands (data_folder.rs), merged into ./mock.ts's handlers. The start comes from
// the URL, `?df=`:
//   (none)       the default place, nothing to report
//   pointer      a folder the player chose earlier (source "pointer")
//   env          KILN_DATA_DIR is set: the row shows the path and Open, Change is off
//   broken       the pointer names a folder that is gone: the dialog over the whole app; every other command fails
//   unreadable   the same, with a folder that is there but not a Kiln data folder
//   moved        the first start after a move: the notice, nothing left behind
//   moved-left   the same, with files that stayed in the old folder
//   fail         every move fails half-way
// Folders a test picks (the browser has no folder dialog): `__kilnDataFolderMock.pick(path | null)` queues the next
// answer of the picker (null: canceled); without one the picker answers /mnt/games. What a picked folder gives:
//   /mnt/games                 move (an empty folder: used as it is)
//   /mnt/storage               move into /mnt/storage/Kiln (the folder has other files)
//   /media/player/USB stick    move; Enshrouded gets copies of its mods from there
//   /mnt/stays                 move; two entries the launcher does not know stay in the old folder too
//   /mnt/old-kiln, /mnt/usb/Kiln   use (already a Kiln data folder)
//   the paths in REFUSALS      refuse, with that sentence; `busy(kind)` refuses every folder (game, server, launcher)
//   anything else              move, used as it is
// Also: `failNext(message)`, `sizeDelay(ms)`, `refusals()`, `state()`.
import type { DataFolder, DataFolderMoved, DataFolderPlan, DataFolderSize, KilnError } from "./commands";

type Args = Record<string, unknown>;
type StartTask = (kind: string, title: string, steps: string[], finish: () => unknown, stepMs?: number) => string;

const HOME = "/home/player";
export const MOCK_DEFAULT_DATA = `${HOME}/.local/share/kiln`;
const GAME = `${HOME}/.local/share/Steam/steamapps/common/Enshrouded`;
const LONG = `/mnt/archive/${"a folder with a long name/".repeat(9)}kiln`;

/** Every refusal the mock knows: by the folder picked, or (no path) by what runs (`busy`). */
export const REFUSALS: { kind: string; path: string | null; reason: string }[] = [
  { kind: "game", path: null, reason: "Close Enshrouded first: it reads its mods from the data folder while it runs." },
  { kind: "server", path: null, reason: "Stop the server Ember Isles first: it reads its mods from the data folder while it runs." },
  { kind: "launcher", path: null, reason: "Another Kiln Launcher is open. Close it first." },
  { kind: "same", path: MOCK_DEFAULT_DATA, reason: "This is the data folder already." },
  { kind: "inside", path: `${MOCK_DEFAULT_DATA}/library`, reason: "This folder is inside the data folder." },
  { kind: "around", path: `${HOME}/.local/share`, reason: "The data folder is inside this folder. Pick a folder outside it." },
  { kind: "game-folder", path: `${GAME}/mods`, reason: "This folder is inside a game folder. Pick a folder outside it." },
  { kind: "not-writable", path: "/mnt/readonly", reason: "Kiln cannot write to this folder." },
  { kind: "too-small", path: "/mnt/small", reason: "Not enough free space there: 3.4 GB is needed, 1.2 GB is free." },
  { kind: "not-kiln", path: "/mnt/shared", reason: "There is a folder named Kiln in it that is not a Kiln data folder." },
  { kind: "too-long", path: LONG, reason: "Paths in the library would be longer than Windows allows. Pick a folder with a shorter path." },
];

const USE = new Set(["/mnt/old-kiln", "/mnt/usb/Kiln"]);
const SIZE: DataFolderSize = { bytes: 3_650_000_000, files: 12_408 };

function start(): DataFolder {
  const q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("df") : null;
  const base: DataFolder = { path: MOCK_DEFAULT_DATA, defaultPath: MOCK_DEFAULT_DATA, source: "default", problem: null, notice: null };
  switch (q) {
    case "pointer": return { ...base, path: "/mnt/games/Kiln", source: "pointer" };
    case "env": return { ...base, path: "/srv/kiln-data", source: "env" };
    case "broken": return { ...base, source: "pointer", problem: { path: "/mnt/usb/Kiln", reason: "The folder is not there. If it is on a drive that is not connected, connect it and start Kiln again." } };
    case "unreadable": return { ...base, source: "pointer", problem: { path: "/mnt/games/Kiln", reason: "The folder is there but holds no Kiln data." } };
    case "moved": return { ...base, path: "/mnt/games/Kiln", source: "pointer", notice: { from: MOCK_DEFAULT_DATA, to: "/mnt/games/Kiln", left: [] } };
    case "moved-left": return { ...base, path: "/mnt/games/Kiln", source: "pointer", notice: {
      from: MOCK_DEFAULT_DATA, to: "/mnt/games/Kiln", left: [`${MOCK_DEFAULT_DATA}/my-notes.txt`, `${MOCK_DEFAULT_DATA}/old-mods`],
    } };
    default: return base;
  }
}

const FAIL_URL = typeof location !== "undefined" && new URLSearchParams(location.search).get("df") === "fail";

let folder: DataFolder = start();
const picks: (string | null)[] = [];
let failNext: string | null = FAIL_URL ? "Could not copy library/kiln/new-items: the disk reported an input/output error." : null;
let busy: string | null = null;
let sizeDelay = 900;

/** The browser's stand-in for the folder dialog (ui: shell/dataFolder.ts pickFolder). */
export function mockPickFolder(): string | null {
  return picks.length ? picks.shift()! : "/mnt/games";
}

/** The data folder in use (app_info.dataDir). */
export const mockDataDir = () => folder.path;

/** Commands that still answer while the pointer is broken; every other one fails then, as in the backend. */
const OPEN_WHEN_BROKEN = new Set(["data_folder_get", "data_folder_use", "data_folder_reset", "data_folder_restart", "deck_quit"]);
export function dataFolderBlocks(cmd: string): KilnError | null {
  if (!folder.problem || OPEN_WHEN_BROKEN.has(cmd)) return null;
  return { code: "refused", message: `The data folder ${folder.problem.path} cannot be used.` };
}

function plan(path: string): DataFolderPlan {
  const picked = path.replace(/(?<=.)[\\/]+$/, "");
  const base: DataFolderPlan = { picked, target: picked, action: "move", reason: null, bytes: SIZE.bytes, files: SIZE.files, free: 412_000_000_000,
    sameVolume: false, copyMode: [], stays: [] };
  const refuse = (reason: string): DataFolderPlan => ({ ...base, action: "refuse", reason });
  if (busy) return refuse(REFUSALS.find((r) => r.kind === busy)?.reason ?? busy);
  const r = REFUSALS.find((x) => x.path === picked);
  if (r) return refuse(r.reason);
  if (USE.has(picked)) return { ...base, action: "use", bytes: 0, files: 0 };
  if (picked === "/mnt/storage" || picked === "/run/media/mmcblk0p1") return { ...base, target: `${picked}/Kiln` };
  if (picked === "/media/player/USB stick") return { ...base, free: 58_000_000_000, copyMode: ["Enshrouded"] };
  if (picked === "/mnt/stays") return { ...base, stays: ["my-notes.txt", "old-mods"] };
  if (picked.startsWith(`${HOME}/`)) return { ...base, sameVolume: true, free: 96_000_000_000 };
  return base;
}

export function dataFolderMockHandlers(startTask: StartTask): Record<string, (a: Args) => unknown> {
  const api = {
    pick: (p: string | null) => { picks.push(p); },
    failNext: (message: string | null) => { failNext = message; },
    busy: (kind: string | null) => { busy = kind; },
    sizeDelay: (ms: number) => { sizeDelay = ms; },
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
        ["Copying the library", "Copying runtimes and modpacks", "Copying the rest", "Checking the copies", "Switching to the new folder"],
        (): DataFolderMoved => {
          if (fail) rejectWith("io", fail);
          // the running launcher keeps the old folder until it restarts (data_folder_restart)
          return { from, to: p.target, warnings: p.stays.length ? [`${p.stays.join(", ")} stayed in ${from} too: Kiln does not know them.`] : [] };
        }, 600);
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
