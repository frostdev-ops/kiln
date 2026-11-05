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
  { id: "isle-icons", version: "1.1.0", folder: "isle-icons", enabled: true, source: { kind: "kiln", id: "isle-icons", version: "1.1.0", hash: hash("a") }, clients: "mod", state: "loaded", kind: "kiln" },
  { id: "fps-unlock", version: "2.0.0", folder: "fps-unlock", enabled: false, source: { kind: "native", id: "fps-unlock", version: "2.0.0", hash: hash("b") }, clients: "mod", state: null, kind: "native" },
];
const required = [
  { id: "isle-icons", version: "1.1.0", hash: hash("a"), kind: "kiln", name: "Isle Icons" },
];
const backups = [
  { name: "20261104-075346-launcher", created: "2026-11-04T07:53:46Z", bytes: 412_000, source: "kiln" },
  { name: "lab-savegame-20261103-201500Z.tar.gz", created: "2026-11-03T20:15:00Z", bytes: 398_112, source: "kilnctl" },
];

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

export const serverMockHandlers: Record<string, (args: Args) => unknown> = {
  server_console_open: (a) => ({ subscription: "console-mock", serverId: a.serverId, source: "capture", path: "/home/player/.local/state/kiln/lab.run.log", truncated: false, lines: consoleLines }),
  server_console_close: () => null,
  server_settings_get: () => ({ path: "/home/player/srv/enshrouded/lab/enshrouded_server.json", values: settings, schema, running: false }),
  server_settings_set: (a) => ({ path: "/home/player/srv/enshrouded/lab/enshrouded_server.json", values: { ...settings, ...(a.values as object) }, schema, running: false }),
  server_mods: () => mods,
  server_mod_enable: () => null,
  server_backups: () => backups,
  server_query: () => ({ name: "Lab server", players: 0, maxPlayers: 4, version: "0.0.15.0", pingMs: 2 }),
  server_modpack_status: () => ({ config: null, built: null, required }),
  server_modpack_publish: (a) => ({ config: { url: String(a.url), sha256: "c".repeat(64), name: "Lab", version: "1", size: 120_000 }, built: null, required }),
  modpack_inspect: () => ({ manifest: null, server: { id: "srv-4b8e2c", name: "Ember Isles", address: null, kiln: "0.2.0-dev" }, url: "https://example.org/ember-isles-3.zip", https: true, size: 1_048_576, missing: required, present: [], nativeCode: false }),
  modpack_dismiss: () => null,
};
