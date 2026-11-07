// Mock answers for the nexus_* commands (browser mock mode; src/api/mock.ts registers them): a small Enshrouded
// catalog on a fake Nexus Mods. The account comes from the URL:
//   ?nexus=free (default)  a free account with a key: Add opens the file's page (Mod Manager Download)
//   ?nexus=premium         a premium account: Add downloads in the launcher
//   ?nexus=nokey           no API key yet: every Enshrouded mod listed, Kiln mods need the key
// Tests and the console: `__kilnNexusMock.nxm("nxm://enshrouded/mods/310/files/1001?key=k&expires=...")` sends a Mod
// Manager Download link as the browser would; `__kilnNexusMock.state()` shows the account and the handler.
import type { AddReport } from "./commands";
import type {
  CatalogDetails, CatalogFile, CatalogMod, CatalogPage, CatalogQuery, CatalogRequirement, HandlerStatus, InstallReport, InstalledCatalogMod,
  ModType, NexusAccount, PendingLink,
} from "./nexus";

type Handler = (args: Record<string, unknown>) => unknown;
type StartTask = (kind: string, title: string, steps: string[], finish: () => unknown, stepMs?: number) => string;
type QuickAdd = (gameId: string, paths: string[], makeIt: boolean) => AddReport;

const MODE = typeof location !== "undefined" ? new URLSearchParams(location.search).get("nexus") ?? "free" : "free";

interface MockMod {
  id: string;
  name: string;
  slug: string;
  summary: string;
  version: string;
  author: string;
  category: string;
  endorsements: number;
  downloads: number;
  created: string;
  updated: string;
  kind: ModType | null;
  files: { fileId: string; gsid: string; name: string; version: string; category: string }[];
  requires?: string[];
  description: string;
  hue: number;
}

const day = (d: number) => new Date(Date.UTC(2026, 10, 7) - d * 86_400_000).toISOString().replace(/\.\d+Z$/, "Z");

const MODS: MockMod[] = [
  { id: "301", name: "Kiln - Universal Mod framework and Launcher", slug: "kiln", summary: "Kiln is a mod framework and launcher for Enshrouded.", version: "0.2.0-dev.15", author: "gumboi",
    category: "Miscellaneous", endorsements: 120, downloads: 2400, created: day(1), updated: day(0), kind: null, files: [], description: "The launcher you are using.", hue: 18 },
  { id: "310", name: "Lantern Glow", slug: "lantern-glow", summary: "Warmer, brighter lanterns and torches that light the fog around you.", version: "1.2.0", author: "emberwright",
    category: "Visuals", endorsements: 412, downloads: 5310, created: day(5), updated: day(1), kind: "kiln", requires: ["301", "320"],
    files: [{ fileId: "90310", gsid: "1001", name: "Lantern Glow", version: "1.2.0", category: "main" }],
    description: "Warm light for the long nights.\n\n• Brighter lanterns\n• Torches light the shroud's edge\n• Configurable in the launcher", hue: 32 },
  { id: "313", name: "Night Sky", slug: "night-sky", summary: "A starfield and two moons over Embervale. Nights you want to look at.", version: "0.3.1", author: "stargazer",
    category: "Visuals", endorsements: 260, downloads: 3100, created: day(4), updated: day(2), kind: "kiln", requires: ["301"],
    files: [{ fileId: "90313", gsid: "1003", name: "Night Sky", version: "0.3.1", category: "main" }], description: "Stars, moons and a darker night.", hue: 230 },
  { id: "320", name: "Ember Core", slug: "ember-core", summary: "Shared code for emberwright's Kiln mods: config helpers and a few hooks.", version: "1.0.0", author: "emberwright",
    category: "Modders Resources", endorsements: 88, downloads: 4200, created: day(6), updated: day(3), kind: "kiln", requires: ["301"],
    files: [{ fileId: "90320", gsid: "1020", name: "Ember Core", version: "1.0.0", category: "main" }], description: "A library other mods need. It does nothing on its own.", hue: 12 },
  { id: "316", name: "Weather Plus", slug: "weather-plus", summary: "Storms, fog banks and clear mornings, with a weather forecast at the bed.", version: "1.1.0", author: "cloudkeeper",
    category: "Gameplay", endorsements: 190, downloads: 2800, created: day(6), updated: day(0), kind: "kiln", requires: ["301"],
    files: [{ fileId: "90316", gsid: "1016", name: "Weather Plus", version: "1.1.0", category: "main" }], description: "Weather that changes your plans.", hue: 200 },
  { id: "317", name: "Cozy Camp", slug: "cozy-camp", summary: "Campfires heal faster, beds rest you sooner, and your camp feels lived in.", version: "2.0.0", author: "hearthling",
    category: "Gameplay", endorsements: 140, downloads: 1900, created: day(3), updated: day(3), kind: "kiln", requires: ["301"],
    files: [{ fileId: "90317", gsid: "1017", name: "Cozy Camp", version: "2.0.0", category: "main" }], description: "Rest well.", hue: 40 },
  { id: "312", name: "Bigger Bags EMF", slug: "bigger-bags-emf", summary: "More backpack slots. An EMF mod: Kiln loads it too.", version: "2.0", author: "packrat",
    category: "Gameplay", endorsements: 330, downloads: 9100, created: day(40), updated: day(4), kind: "emf",
    files: [{ fileId: "90312", gsid: "1012", name: "Bigger Bags", version: "2.0", category: "main" }], description: "Carry more.", hue: 120 },
  { id: "315", name: "Fast Travel Anywhere", slug: "fast-travel", summary: "Travel from anywhere, not just flame altars. A plain DLL mod.", version: "0.4.0", author: "wayfinder",
    category: "Utilities", endorsements: 75, downloads: 1500, created: day(30), updated: day(5), kind: "dll",
    files: [{ fileId: "90315", gsid: "1015", name: "Fast Travel Anywhere", version: "0.4.0", category: "main" }], description: "Go anywhere.", hue: 280 },
  { id: "311", name: "HD Texture Pack", slug: "hd-textures", summary: "Sharper rocks, trees and ground. Pak files: not a Kiln mod.", version: "3", author: "pixels",
    category: "Visuals", endorsements: 520, downloads: 15000, created: day(220), updated: day(200), kind: null,
    files: [{ fileId: "90311", gsid: "1011", name: "HD Textures", version: "3", category: "main" }], description: "Textures.", hue: 90 },
];

function art(m: MockMod): string {
  const initials = m.name.split(/\s+/).filter((w) => /^[A-Za-z]/.test(w)).slice(0, 2).map((w) => w[0]).join("");
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">` +
    `<stop offset="0" stop-color="hsl(${m.hue},70%,32%)"/><stop offset="1" stop-color="hsl(${(m.hue + 30) % 360},60%,12%)"/></linearGradient></defs>` +
    `<rect width="320" height="180" fill="url(#g)"/><text x="160" y="112" font-family="Georgia,serif" font-size="64" text-anchor="middle" fill="rgba(255,240,220,.85)">${initials}</text></svg>`;
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
}

const page = (id: string) => `https://www.nexusmods.com/enshrouded/mods/${id}`;

interface State {
  account: NexusAccount;
  handler: HandlerStatus;
  installed: InstalledCatalogMod[];
  links: (PendingLink & { key: string | null })[];
  seq: number;
}

const st: State = {
  account: {
    keySet: MODE !== "nokey", keyPlace: MODE === "nokey" ? null : "keyring", name: MODE === "nokey" ? null : "embervale_player", premium: MODE === "premium", error: null,
    rate: { hourlyRemaining: 498, dailyRemaining: 19990, blockedFor: 0, calls: 0 }, keyPage: "https://www.nexusmods.com/settings/api-keys",
  },
  handler: { supported: true, registered: false, active: false, previous: "Vortex" },
  installed: [{
    modId: "316", name: "Weather Plus", fileId: "90316", fileName: "Weather Plus", versionId: "801016", versionGsid: "1006", version: "1.0.0",
    refs: [{ kind: "kiln", id: "weather-plus", version: "1.0.0", hash: "kdh1:" + "0".repeat(64) }], installed: day(6), source: "nxm",
    info: { version: "1.0.0", versionGsid: "1006", fileName: "Weather Plus", inLibrary: true, latestVersion: "1.1.0", latestGsid: "1016", updateAvailable: true },
  }],
  links: [],
  seq: 0,
};

const inst = (id: string) => st.installed.find((i) => i.modId === id)?.info ?? null;

function toMod(m: MockMod): CatalogMod {
  const known = st.account.keySet || m.kind === null ? m.kind : null;
  return {
    id: m.id, uid: `2495805495${m.id.padStart(4, "0")}`, name: m.name, summary: m.summary, version: m.version, author: m.author, category: m.category,
    thumbnail: art(m), endorsements: m.endorsements, downloads: m.downloads, created: m.created, updated: m.updated, adult: false,
    kind: known, url: page(m.id), installed: inst(m.id),
  };
}

function file(m: MockMod, f: MockMod["files"][number]): CatalogFile {
  return { fileId: f.fileId, fileName: f.name, versionId: `80${f.gsid}`, versionGsid: f.gsid, version: f.version, category: f.category, uploaded: m.updated, primary: true,
    kind: m.kind ?? "other", archive: `${m.slug}-${f.version}.zip` };
}

function requirements(m: MockMod): CatalogRequirement[] {
  return (m.requires ?? []).map((id) => {
    const r = MODS.find((x) => x.id === id)!;
    return { modId: id, name: r.name, fileName: r.files[0]?.name ?? r.name, minVersion: null, installed: !!inst(id)?.inLibrary, kiln: id === "301", otherGame: false, url: page(id) };
  });
}

function err(code: string, message: string, detail?: unknown): never {
  throw { code, message, ...(detail !== undefined ? { detail } : {}) };
}

export function nexusMockHandlers(handlers: Record<string, Handler>, emit: (name: string, payload: unknown) => void, startTask: StartTask, quickAdd: QuickAdd): Record<string, Handler> {
  const install = (gameId: string, m: MockMod, f: MockMod["files"][number], source: "nxm" | "catalog") => {
    const active = handlers.profile_active({ gameId }) as unknown;
    const profiles = handlers.profile_list({}) as unknown[];
    const makeIt = !active && profiles.length === 0;
    const fileName = m.kind === "dll" ? `${m.slug}.dll` : `${m.slug}-${f.version}.zip`;
    return startTask("nexus_install", "Add from Nexus Mods", ["Asking Nexus Mods", "Downloading", "Checking the file", "Adding to the profile"], (): InstallReport => {
      const add = quickAdd(gameId, [fileName], makeIt);
      const refs = [...add.import.imported, ...add.import.existing];
      st.installed = st.installed.filter((i) => i.modId !== m.id);
      st.installed.push({ modId: m.id, name: m.name, fileId: f.fileId, fileName: f.name, versionId: `80${f.gsid}`, versionGsid: f.gsid, version: f.version, refs,
        installed: new Date().toISOString(), source, info: { version: f.version, versionGsid: f.gsid, fileName: f.name, inLibrary: true, latestVersion: f.version, latestGsid: f.gsid, updateAvailable: false } });
      return { add, modId: m.id, name: m.name, version: f.version, fileName: f.name, check: { files: 2, matched: 2, hashed: 0, noManifest: false, sha256: "ab".repeat(32), size: 182_000 },
        requirements: requirements(m), download: `/home/player/.local/share/kiln/nexus/downloads/${m.id}-${f.gsid}/${fileName}` };
    }, 250);
  };
  const modOf = (id: unknown) => MODS.find((m) => m.id === String(id)) ?? err("not_found", "Not found on Nexus Mods.");
  const fileOf = (m: MockMod, gsid: unknown) => m.files.find((f) => f.gsid === String(gsid)) ?? err("not_found", "That file is not on the page.");

  const h: Record<string, Handler> = {
    nexus_account: () => st.account,
    nexus_key_set: (a) => {
      const key = String(a.key ?? "").trim();
      if (!key || key === "bad") err("auth_failed", "Nexus Mods did not accept the API key. Check it in Settings.");
      st.account = { ...st.account, keySet: true, keyPlace: "keyring", name: "embervale_player", premium: key.startsWith("premium"), error: null };
      return st.account;
    },
    nexus_key_clear: () => { st.account = { ...st.account, keySet: false, keyPlace: null, name: null, premium: false }; return st.account; },
    nexus_search: (a): CatalogPage => {
      const q = (a.query ?? {}) as CatalogQuery;
      const words = (q.text ?? "").toLowerCase().split(/\s+/).filter(Boolean);
      let list = MODS.filter((m) => words.every((w) => `${m.name} ${m.summary} ${m.author} ${m.category}`.toLowerCase().includes(w)));
      const filter = q.filter ?? "kiln";
      let needsKey = false;
      if (filter === "kiln") {
        if (!st.account.keySet) { needsKey = true; list = []; } else list = list.filter((m) => m.kind === "kiln");
      } else if (filter === "installed") list = list.filter((m) => inst(m.id));
      const by: Record<string, (x: MockMod, y: MockMod) => number> = {
        endorsements: (x, y) => y.endorsements - x.endorsements, downloads: (x, y) => y.downloads - x.downloads,
        updated: (x, y) => y.updated.localeCompare(x.updated), new: (x, y) => y.created.localeCompare(x.created), name: (x, y) => x.name.localeCompare(y.name),
      };
      list.sort(by[q.sort ?? "endorsements"]);
      const size = q.pageSize || 24;
      const p = Math.max(1, q.page ?? 1);
      st.account.rate.calls++;
      return { mods: list.slice((p - 1) * size, p * size).map(toMod), total: list.length, page: p, pageSize: size, unchecked: 0, needsKey, fetched: new Date().toISOString() };
    },
    nexus_mod: (a): CatalogDetails => {
      const m = modOf(a.modId);
      const files = st.account.keySet ? m.files.map((f) => file(m, f)) : [];
      const best = files[0] ?? null;
      return {
        ...toMod(m), description: m.description, credit: m.author, tags: [m.category], uniqueDownloads: Math.round(m.downloads * 0.7),
        images: [{ url: art(m), thumb: art(m), title: null }, { url: art({ ...m, hue: (m.hue + 60) % 360 }), thumb: art({ ...m, hue: (m.hue + 60) % 360 }), title: "In the fog" }],
        files, bestFile: best, requirements: st.account.keySet ? requirements(m) : [],
        filesUrl: `${page(m.id)}?tab=files${best ? `&file_id=${best.versionGsid}&nmm=1` : ""}`, needsKey: !st.account.keySet,
      };
    },
    nexus_installed: () => st.installed,
    nexus_download: (a) => {
      const m = modOf(a.modId);
      if (m.id === "301") err("refused", "That is Kiln itself: update the launcher from its page instead.");
      const f = fileOf(m, a.fileId);
      if (!st.account.keySet) err("auth_failed", "Add your Nexus Mods API key in Settings first.");
      if (!st.account.premium) err("refused", "Free accounts download through the Nexus Mods website.", { url: `${page(m.id)}?tab=files&file_id=${f.gsid}&nmm=1` });
      return install(String(a.gameId), m, f, "catalog");
    },
    nexus_links: () => st.links.map(({ key: _k, ...l }) => l),
    nexus_link_install: (a) => {
      const i = st.links.findIndex((l) => l.id === a.linkId);
      if (i < 0) err("not_found", "That link is gone. Click Mod Manager Download again.");
      const [l] = st.links.splice(i, 1);
      if (l.expired) err("refused", "The download link has expired. Click Mod Manager Download on the site again.");
      const m = modOf(l.modId);
      return install(String(a.gameId), m, fileOf(m, l.fileId), "nxm");
    },
    nexus_link_dismiss: (a) => { st.links = st.links.filter((l) => l.id !== a.linkId); return null; },
    nexus_handler_status: () => st.handler,
    nexus_handler_set: (a) => { st.handler = { ...st.handler, registered: !!a.on, active: !!a.on }; return st.handler; },
  };

  if (typeof window !== "undefined") {
    (window as unknown as { __kilnNexusMock?: unknown }).__kilnNexusMock = {
      /** A Mod Manager Download link, as the browser hands it to the registered handler. */
      nxm: (url: string) => {
        const m = /^nxm:\/\/([^/]+)\/mods\/(\d+)\/files\/(\d+)(?:\?(.*))?$/i.exec(url);
        if (!m || m[1].toLowerCase() !== "enshrouded") return false;
        const q = new URLSearchParams(m[4] ?? "");
        const expires = Number(q.get("expires") ?? 0);
        st.links = st.links.filter((l) => l.fileId !== m[3]);
        st.links.push({ id: `nxm-${++st.seq}`, modId: m[2], fileId: m[3], expired: !!expires && expires * 1000 < Date.now() - 60_000, received: new Date().toISOString(), key: q.get("key") });
        emit("kiln://nxm", { count: st.links.length });
        return true;
      },
      state: () => JSON.parse(JSON.stringify({ account: st.account, handler: st.handler, installed: st.installed, links: st.links.length })),
      setUpdate: (modId: string, latest: string) => { const i = st.installed.find((x) => x.modId === modId); if (i) i.info = { ...i.info, latestVersion: latest, updateAvailable: latest !== i.version }; },
    };
  }
  return h;
}

