// Mock answers for the Deck commands (browser mock mode; src/api/mock.ts registers them). A fake home with a Downloads
// folder of mods, an SD card and Steam's game folder; detection reads the URL:
//   ?deck=steamdeck   app_info.deck as Game Mode on a Steam Deck sets it (SteamDeck=1, gamescope, DMI Jupiter)
//   ?deck=forced      as `--deck`;  ?deck=desktop: as `--desktop`
// Gamepad events come from tests and the console: `__kilnMock.emit("kiln://gamepad", {kind: "button", ...})`.
import type { AppInfo, DeckFolder, DeckFoundMod, DeckInfo, DeckPlace, Detected, GamepadStatus } from "../api/commands";

type Handler = (args: Record<string, unknown>) => unknown;

const HOME = "/home/player";
const DL = `${HOME}/Downloads`;
const CARD = "/run/media/mmcblk0p1";
const GAMES = `${HOME}/.local/share/Steam/steamapps/common`;

interface FakeFile { name: string; size?: number; ago: number }
/** Folders: sub-folders and files (minutes ago they changed). */
const FS: Record<string, { dirs: string[]; files: FakeFile[]; game?: boolean }> = {
  [HOME]: { dirs: ["Desktop", "Downloads", "Music"], files: [{ name: "notes.txt", ago: 9000 }] },
  [DL]: {
    dirs: ["Screenshots", "unpacked"],
    files: [
      { name: "lantern-glow-1.2.0.zip", size: 182_000, ago: 12 },
      { name: "server-ember-isles-modpack.zip", size: 3_400_000, ago: 60 * 5 },
      { name: "fast-travel-native-0.4.0.zip", size: 96_000, ago: 60 * 26 },
      { name: "emf-fishing-1.0.0.zip", size: 240_000, ago: 60 * 50 },
      { name: "auto-sort-plugin.dll", size: 410_000, ago: 60 * 24 * 3 },
      { name: "more-hotbars-2.1.0.zip", size: 120_000, ago: 60 * 24 * 6 },
      { name: "holiday-photos.zip", size: 88_000_000, ago: 60 * 24 * 9 },
      { name: "steam-deck-wallpaper.png", size: 2_100_000, ago: 60 * 24 * 12 },
    ],
  },
  [`${DL}/Screenshots`]: { dirs: [], files: [{ name: "enshrouded-0001.png", ago: 300 }] },
  [`${DL}/unpacked`]: { dirs: ["cozy-camp"], files: [] },
  [`${DL}/unpacked/cozy-camp`]: { dirs: [], files: [{ name: "kiln.mod.json", ago: 400 }] },
  [`${HOME}/Desktop`]: { dirs: [], files: [{ name: "Kiln_Launcher.AppImage", size: 98_000_000, ago: 2000 }] },
  [`${HOME}/Music`]: { dirs: [], files: [] },
  [CARD]: { dirs: ["mods", "steamapps"], files: [] },
  [`${CARD}/mods`]: { dirs: [], files: [{ name: "big-loot-1.0.0.zip", size: 64_000, ago: 60 * 24 * 2 }] },
  [`${CARD}/steamapps`]: { dirs: ["common"], files: [] },
  [`${CARD}/steamapps/common`]: { dirs: [], files: [] },
  [GAMES]: { dirs: ["Enshrouded", "Proton - Experimental"], files: [] },
  [`${GAMES}/Enshrouded`]: { dirs: ["kiln"], files: [{ name: "enshrouded.exe", ago: 5000 }], game: true },
  [`${GAMES}/Enshrouded/kiln`]: { dirs: [], files: [] },
  [`${GAMES}/Proton - Experimental`]: { dirs: [], files: [] },
};

const PLACES: DeckPlace[] = [
  { name: "Downloads", path: DL, kind: "downloads" },
  { name: "Home", path: HOME, kind: "home" },
  { name: "Desktop", path: `${HOME}/Desktop`, kind: "desktop" },
  { name: "SD card", path: CARD, kind: "card" },
  { name: "Steam games", path: GAMES, kind: "games" },
  { name: "Games on card", path: `${CARD}/steamapps/common`, kind: "games" },
];

const recognised = (d: Detected) => ["kiln", "native", "plugin", "emf", "modpack", "bare-dll"].includes(d.kind) || (d.kind === "unknown" && d.items.length > 0);
const reject = (code: string, message: string) => { throw { code, message }; };

function detectionFromUrl(): DeckInfo {
  const q = typeof location !== "undefined" ? new URLSearchParams(location.search).get("deck") : null;
  const none: DeckInfo = { detected: false, signals: [], gamescope: false, device: null, forced: null };
  if (q === "steamdeck") return { detected: true, signals: ["SteamDeck=1", "SteamOS=1", "XDG_CURRENT_DESKTOP=gamescope", "GAMESCOPE_WAYLAND_DISPLAY", "dmi:Jupiter"], gamescope: true, device: "jupiter", forced: null };
  if (q === "forced") return { ...none, forced: "deck" };
  if (q === "desktop") return { ...none, forced: "desktop" };
  return none;
}

export function deckMockHandlers(handlers: Record<string, Handler>): Record<string, Handler> {
  const appInfo = handlers.app_info;
  const detect = (path: string) => handlers.library_detect({ path }) as Detected;
  return {
    app_info: (a) => ({ ...(appInfo(a) as AppInfo), deck: detectionFromUrl() }),
    deck_browse: (a): DeckFolder => {
      const dir = (a.dir as string | null) || DL;
      const node = FS[dir.replace(/\/+$/, "")];
      if (!node) reject("not_found", `Can't open ${dir}.`);
      const now = Date.now();
      const mods: DeckFoundMod[] = [];
      let other = 0;
      for (const f of [...node!.files].sort((x, y) => x.ago - y.ago)) {
        const path = `${dir}/${f.name}`;
        const big = (f.size ?? 0) > 512 * 1024 * 1024;
        const d = /\.(zip|dll)$/i.test(f.name) && !/photos|wallpaper/i.test(f.name) && !big ? detect(path) : null;
        if (d && recognised(d)) mods.push({ path, file: f.name, folder: false, size: f.size ?? 0, modified: new Date(now - f.ago * 60_000).toISOString(), detected: d });
        else other++;
      }
      const folders: { name: string; path: string }[] = [];
      for (const name of node!.dirs) {
        const path = `${dir}/${name}`;
        const sub = FS[path];
        if (sub?.files.some((f) => f.name === "kiln.mod.json")) {
          mods.push({ path, file: name, folder: true, size: 0, modified: new Date(now - 400 * 60_000).toISOString(), detected: detect(path) });
        } else folders.push({ name, path });
      }
      const cut = dir.lastIndexOf("/");
      return {
        dir, name: dir.slice(cut + 1) || "/", parent: cut > 0 ? dir.slice(0, cut) : dir === "/" ? null : "/",
        folders, mods, other, truncated: false, hasGame: !!node!.game, places: PLACES,
      };
    },
    deck_gamepad: (): GamepadStatus => ({ native: false, running: false, devices: [], denied: 0 }),
    deck_fullscreen: (a) => !!a.on,
    deck_quit: () => null,
  };
}
