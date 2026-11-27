// Mock answers for the server and modpack commands, merged into launcher/src/api/mock.ts's
// handlers (`...serverMockHandlers`). Shapes are the real backend's (kiln_manage::server, ::modpack).
type Args = Record<string, unknown>;

const hash = (c: string) => "kdh1:" + c.repeat(64).slice(0, 64);
const settings = {
  name: "Lab server", saveDirectory: "./savegame", logDirectory: "./logs", ip: "0.0.0.0", queryPort: 26091, slotCount: 4,
  voiceChatMode: "Proximity", enableVoiceChat: false, enableTextChat: false, gameSettingsPreset: "Custom",
  gameSettings: {
    playerHealthFactor: 1, playerManaFactor: 1, playerStaminaFactor: 4, playerBodyHeatFactor: 2, playerDivingTimeFactor: 1,
    enableDurability: true, enableStarvingDebuff: false, foodBuffDurationFactor: 1, fromHungerToStarving: 600000000000,
    shroudTimeFactor: 1, tombstoneMode: "AddBackpackMaterials", enableGliderTurbulences: true, weatherFrequency: "Normal",
    miningDamageFactor: 1, plantGrowthSpeedFactor: 1, resourceDropStackAmountFactor: 1, factoryProductionSpeedFactor: 1,
    perkUpgradeRecyclingFactor: 0.5, perkCostFactor: 1, experienceCombatFactor: 1, experienceMiningFactor: 1,
    experienceExplorationQuestsFactor: 1, randomSpawnerAmount: "Normal", aggroPoolAmount: "Normal", enemyDamageFactor: 1,
    enemyHealthFactor: 1, enemyStaminaFactor: 1, enemyPerceptionRangeFactor: 1, bossDamageFactor: 1, bossHealthFactor: 1,
    threatBonus: 1, pacifyAllEnemies: false, tamingStartleRepercussion: "LoseSomeProgress", dayTimeDuration: 1800000000000,
    nightTimeDuration: 720000000000, curseModifier: "Normal", fishingDifficulty: "Normal", flameLevelFogRemoval: 1,
  },
  userGroups: [
    { name: "Admin", password: "admin-pw", canKickBan: true, canAccessInventories: true, canEditWorld: true, canEditBase: true, canExtendBase: true, reservedSlots: 0 },
    { name: "Friend", password: "friend-pw", canKickBan: false, canAccessInventories: true, canEditWorld: true, canEditBase: true, canExtendBase: false, reservedSlots: 0 },
  ],
  bannedAccounts: [],
};
const f = (key: string, group: string, label: string, min: number, max: number, hint = "") => ({ key, group, label, kind: "factor", min, max, hint });
const schema = {
  gameSettings: [
    f("playerHealthFactor", "Player", "Health", 0.25, 4), f("playerManaFactor", "Player", "Mana", 0.25, 4), f("playerStaminaFactor", "Player", "Stamina", 0.25, 4),
    { key: "enableDurability", group: "Survival", label: "Item durability", kind: "bool", hint: "" },
    { key: "fromHungerToStarving", group: "Survival", label: "Hunger to starving", kind: "minutes", min: 5, max: 20, hint: "Minutes" },
    { key: "weatherFrequency", group: "World", label: "Weather", kind: "enum", options: ["Disabled", "Rare", "Normal", "Often"], hint: "" },
    { key: "flameLevelFogRemoval", group: "World", label: "Fog removal flame level", kind: "int", min: 0, max: 20, hint: "" },
    { key: "dayTimeDuration", group: "Time", label: "Day length", kind: "minutes", min: 2, max: 60, hint: "Minutes" },
    f("enemyDamageFactor", "Enemies", "Enemy damage", 0.25, 5),
  ],
  presets: ["Default", "Relaxed", "Hard", "Survival", "Custom"],
  permissions: [
    { key: "canKickBan", field: "CanKickBan", label: "Kick & ban" }, { key: "canAccessInventories", field: "CanAccessInventories", label: "Open chests" },
    { key: "canEditWorld", field: "CanEditWorld", label: "Edit the world" }, { key: "canEditBase", field: "CanEditBase", label: "Build in bases" },
    { key: "canExtendBase", field: "CanExtendBase", label: "Extend bases" },
  ],
};
const mods = [
  { id: "kiln-admin", version: "0.2.0", folder: "kiln-admin", enabled: true, source: null, clients: "none", state: "loaded", kind: "kiln" },
  { id: "isle-icons", version: "1.1.0", folder: "isle-icons", enabled: true, source: { kind: "kiln", id: "isle-icons", version: "1.1.0", hash: hash("a") }, clients: "mod", state: "loaded", kind: "kiln", steamMessages: true },
  { id: "fps-unlock", version: "2.0.0", folder: "fps-unlock", enabled: false, source: { kind: "native", id: "fps-unlock", version: "2.0.0", hash: hash("b") }, clients: "mod", state: null, kind: "native" },
];
const required = [
  { id: "isle-icons", version: "1.1.0", hash: hash("a"), kind: "kiln", name: "Isle Icons" },
];
const backups = [
  { name: "20261104-075346-launcher", created: "2026-11-04T07:53:46Z", bytes: 412_000, source: "kiln" },
  { name: "lab-savegame-20261103-201500Z.tar.gz", created: "2026-11-03T20:15:00Z", bytes: 398_112, source: "kilnctl" },
];

// "Open web panel" (0.2.0-dev.31.14): the web panel mod on the mock server, Kilnwarden (`kilnwarden`) or its old name
// Kiln Web Admin (`kiln-webadmin`, the 0.1.0 test builds), or both (tests: `__kilnMock.webPanelMods`). None until a test
// sets it, so the other specs' Overview stays as it was. admin_open_web_panel answers as kiln_manage::admin::web_panel
// does: the panel that listens is Kilnwarden's, unless the old copy is enabled too (Kilnwarden then stays off and the
// old one listens), and the one-time login is minted for that mod's id.
type PanelMod = { id: string; enabled: boolean };
let panelMods: PanelMod[] = [];
const panelOpens: { serverId: string; mod: string; consumer: string }[] = [];
const panelModRow = (m: PanelMod) => ({ id: m.id, version: "0.1.0", folder: m.id, enabled: m.enabled, source: null, clients: "none", state: m.enabled ? "loaded" : null, kind: "kiln" });
function listeningPanel(): string | null {
  const on = (id: string) => panelMods.some((m) => m.id === id && m.enabled) || (id === "kiln-webadmin" && webAdmin);
  if (on("kilnwarden") && !on("kiln-webadmin")) return "kilnwarden";
  return on("kiln-webadmin") ? "kiln-webadmin" : null;
}
export const webPanelModMockHooks = {
  /** The web panel mods in the lab server's mods folder (`[]`: none). */
  set: (list: PanelMod[]) => { panelMods = list.map((m) => ({ ...m })); },
  /** Each admin_open_web_panel that opened a panel: the mod that listened and the login's consumer. */
  opens: () => panelOpens.map((o) => ({ ...o })),
};

// a launcher start's capture (kiln_manage::server::console), shortened from the server-console lab
const consoleLines = [
  ["---- kiln-launcher start lab at 2026-11-04T23:10:02Z", "meta"],
  ["kiln-launcher: /home/player/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin/wine ./enshrouded_server.exe in /home/player/srv/enshrouded/lab", "meta"],
  ["ntsync: up and running.", "info"],
  ["enshrouded_server(detached HEAD) - version e4dfdc45cb5d96b452a45df11195a0e56d5bafa3 (master)", "info"],
  ["Config Parsed: 'Z:/home/player/srv/enshrouded/lab/enshrouded_server.json'", "info"],
  ["[steam] Could not find MatchmakingServers interface", "warn"],
  ["0124:fixme:ntdll:NtQuerySystemInformation info_class SYSTEM_PERFORMANCE_INFORMATION", "debug"],
  ["[ecss] Could not find component 6b1adfa8 when loading template Benchmark", "error"],
  ["[server] Server started successfully, ready for connections", "info"],
  ["[game_server] Switching state from LoadEcsScene to Run after 509.38 ms", "info"],
].map(([text, level]) => ({ text, level }));

// "Open web panel" (0.2.0-dev.31.12): the mock's gateways, one per server on its own loopback origin, like
// kiln_manage::admin::web_panel (tests: `__kilnMock.webPanel`). Kiln Web Admin is in the server's mods only after
// `webPanel.install()`, so the other specs' Overview stays as it was.
type MockWebPanel = { serverId: string; origin: string; fingerprint: string | null; via: "local" | "ssh"; isolated: boolean; idleCloseS: number; connections: number };
const webPanels = new Map<string, MockWebPanel>();
let webAdmin = false;
let webVia: "local" | "ssh" = "local";
// (0.2.0-dev.31.19) the panel's own address on the internet in its launcher-endpoint.json (`public`), and how the
// launcher's check of it goes (kiln_manage::admin::web_panel::public); every open, with its route and where it went
let webPublic: { url: string; check: "ok" | "unreachable" | "refused" } | null = null;
const webOpens: { serverId: string; route: string; via: string }[] = [];
// (0.2.0-dev.31.20) each check of the public address (the server's id), and each server's web panel settings
// (kiln_manage::admin::web_panel::settings), kept in local storage as the launcher keeps web-panel-settings.json
const webChecks: string[] = [];
const WEB_SETTINGS = "kiln.mock.webPanelSettings";
const webSettings = (): Record<string, { alwaysLocal: boolean }> => {
  try { return JSON.parse(localStorage.getItem(WEB_SETTINGS) ?? "{}"); } catch { return {}; }
};
const webAlwaysLocal = (id: string) => webSettings()[id]?.alwaysLocal === true;
const asInfo = (p: MockWebPanel, fallback: "unreachable" | "refused" | null = null) => ({ ...p, public: null, fallback });
const webadminMod = { id: "kiln-webadmin", version: "0.1.0", folder: "kiln-webadmin", enabled: true, source: null, clients: "none", state: "loaded", kind: "kiln" };
const originOf = (id: string) => {
  let h = 2166136261;
  for (const c of id) h = Math.imul(h ^ c.charCodeAt(0), 16777619) >>> 0;
  return `https://127.${64 + (h % 191)}.${(h >>> 8) & 255}.${1 + ((h >>> 16) % 254)}:${40000 + (h % 20000)}`;
};
export const webPanelMockHooks = {
  /** Kiln Web Admin loaded on the mock server (the Overview shows "Open web panel"). */
  install: (on = true) => { webAdmin = on; },
  /** How the next gateway reaches its panel. */
  setVia: (via: "local" | "ssh") => { webVia = via; },
  /** The gateway closed by itself (the panel's last tab closed). */
  expire: (serverId: string) => { webPanels.delete(serverId); },
  list: () => [...webPanels.values()],
  /** (0.2.0-dev.31.19) The panel's own address in its file (null: none) and how the launcher's check of it goes. */
  setPublic: (url: string | null, check: "ok" | "unreachable" | "refused" = "ok") => { webPublic = url ? { url, check } : null; },
  /** (0.2.0-dev.31.19) Each admin_open_web_panel: its route ("auto", "local") and where the panel opened. */
  opens: () => webOpens.map((o) => ({ ...o })),
  /** (0.2.0-dev.31.20) Each check of a panel's public address, by server id. */
  checks: () => [...webChecks],
};

export const serverMockHandlers: Record<string, (args: Args) => unknown> = {
  admin_open_web_panel: (a) => {
    const id = String(a.serverId);
    const route = a.route === "local" ? "local" : "auto";
    const mod = listeningPanel();
    if (!mod) throw { code: "admin_unavailable", message: "Kilnwarden is not listening. Check that the current mod is loaded." };
    panelOpens.push({ serverId: id, mod, consumer: mod });
    // (0.2.0-dev.31.20) "Always open the web panel on this computer": auto is local, with no check
    const checked = route === "auto" && !webAlwaysLocal(id) && webPublic !== null;
    if (checked) webChecks.push(id);
    // (0.2.0-dev.31.19) the panel's own address when its check passes: the browser goes there, no gateway
    if (checked && webPublic?.check === "ok") {
      webOpens.push({ serverId: id, route, via: "hosted" });
      return { serverId: id, origin: webPublic.url.replace(/\/$/, ""), fingerprint: "cd".repeat(32), via: "hosted", isolated: true, idleCloseS: 0, connections: 0, public: webPublic.url, fallback: null };
    }
    const p = webPanels.get(id) ?? { serverId: id, origin: originOf(id), fingerprint: "ab".repeat(32), via: webVia, isolated: true, idleCloseS: 120, connections: 1 };
    webPanels.set(id, p);
    webOpens.push({ serverId: id, route, via: p.via });
    return asInfo(p, checked && webPublic ? (webPublic.check as "unreachable" | "refused") : null);
  },
  admin_close_web_panel: (a) => webPanels.delete(String(a.serverId)),
  admin_web_panels: () => [...webPanels.values()].map((p) => asInfo(p)),
  admin_web_panel_settings: (a) => ({ alwaysLocal: webAlwaysLocal(String(a.serverId)) }),
  admin_web_panel_settings_set: (a) => {
    const id = String(a.serverId);
    const next = { alwaysLocal: (a.settings as { alwaysLocal?: unknown } | undefined)?.alwaysLocal === true };
    const all = webSettings();
    if (next.alwaysLocal) all[id] = next;
    else delete all[id];
    localStorage.setItem(WEB_SETTINGS, JSON.stringify(all));
    return next;
  },
  server_console_open: (a) => ({ subscription: "console-mock", serverId: a.serverId, source: "capture", path: "/home/player/.local/state/kiln/lab.run.log", truncated: false, lines: consoleLines }),
  server_console_close: () => null,
  server_settings_get: () => ({ path: "/home/player/srv/enshrouded/lab/enshrouded_server.json", values: settings, schema, running: false }),
  server_settings_set: (a) => ({ path: "/home/player/srv/enshrouded/lab/enshrouded_server.json", values: { ...settings, ...(a.values as object) }, schema, running: false }),
  server_mods: () => [...mods, ...(webAdmin ? [webadminMod] : []), ...panelMods.map(panelModRow)],
  server_mod_enable: () => null,
  server_backups: () => backups,
  server_query: () => ({ name: "Lab server", players: 0, maxPlayers: 4, version: "0.0.15.0", pingMs: 2 }),
  server_modpack_status: () => ({ config: null, built: null, required }),
  server_modpack_publish: (a) => ({ config: { url: String(a.url), sha256: "c".repeat(64), name: "Lab", version: "1", size: 120_000 }, built: null, required }),
  modpack_inspect: () => ({ manifest: null, server: { id: "srv-4b8e2c", name: "Ember Isles", address: null, kiln: "0.2.0-dev" }, url: "https://example.org/ember-isles-3.zip", https: true, size: 1_048_576, missing: required, present: [], nativeCode: false }),
  modpack_dismiss: () => null,
};
