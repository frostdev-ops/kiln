// The search index of Settings: every row, kiln.json entry and card the page shows, with its section, group, name, one
// line of help, More text and keywords, in the launcher's language. The rows read their words from here too
// (parts.tsx SettingRow), so a row and its search hit always say the same.
import type { AppInfo, DesktopInfo, ForceInfo, RenderInfo, SandboxInfo, SchemaSnapshot, SnapshotEntry, X11Info } from "../../api/commands";
import { getPath } from "../../ui";
import { t } from "../../i18n";
import { graphicsLabels, isGraphicsPath, UPSCALER_PATHS } from "../../components/GraphicsSettings";
import { isShroudForgePath } from "../../components/ShroudForgeSettings";
import { choiceOf } from "../../components/upscalerStatus";
import { keyWords, moreBeyond, textOr, type SearchItem, type SectionId, type SettingsCtx } from "./model";

/** kiln.json entries of Game & Kiln > Kiln in the game (the rest but language, graphics and ShroudForge: Advanced). */
export const KILN_GAME_PATHS = ["ui.look", "ui.theme", "ui.background", "ui.scale", "ui.chatHistory", "ui.chatCompletion", "ui.outlines"];

/** Names for kiln.json rows whose keys do not read as words; a key a newer core adds is named from its key. */
export function kilnLabels(): Record<string, string> {
  return {
    "ui.look": t("settings.kilnGame.labels.look"),
    "ui.theme": t("settings.kilnGame.labels.theme"),
    "ui.background": t("settings.kilnGame.labels.background"),
    "ui.scale": t("settings.kilnGame.labels.scale"),
    "ui.chatHistory": t("settings.kilnGame.labels.chatHistory"),
    "ui.chatCompletion": t("settings.kilnGame.labels.chatCompletion"),
    "ui.outlines": t("settings.kilnGame.labels.outlines"),
    "ui.opacity": t("settings.kilnFile.labels.opacity"),
    "ui.overlay": t("settings.kilnFile.labels.overlay"),
    policy: t("settings.kilnFile.labels.policy"),
    log_level: t("settings.kilnFile.labels.logLevel"),
    clr: t("settings.kilnFile.labels.clr"),
    plugins: t("settings.kilnFile.labels.plugins"),
    "native.policy": t("settings.kilnFile.labels.nativePolicy"),
    "mods.fault_limit": t("settings.kilnFile.labels.faultLimit"),
    "mods.fault_window_s": t("settings.kilnFile.labels.faultWindow"),
    "mods.stop_timeout_ms": t("settings.kilnFile.labels.stopTimeout"),
    "mods.test_timeout_s": t("settings.kilnFile.labels.testTimeout"),
    "mods.crash_reports": t("settings.kilnFile.labels.crashReports"),
  };
}

/** Which kiln.json group shows an entry: Kiln in the game, Graphics, or the rest (Advanced > Kiln's file); null: a
 *  control of its own (the language row, the upscaler choice, the ShroudForge card). */
export function kilnGroupOf(path: string): "kiln" | "graphics" | "kilnjson" | null {
  if (path === "ui.language" || isShroudForgePath(path) || UPSCALER_PATHS.includes(path)) return null;
  if (isGraphicsPath(path)) return "graphics";
  if (KILN_GAME_PATHS.includes(path)) return "kiln";
  return "kilnjson";
}

/** A kiln.json group's part of the schema: its entries alone, no section heads (the group's title says it). */
export function kilnSchema(schema: SchemaSnapshot, group: "kiln" | "graphics" | "kilnjson", only?: Set<string> | null): SchemaSnapshot {
  const order = group === "kiln" ? (e: SnapshotEntry) => KILN_GAME_PATHS.indexOf(e.path) : () => 0;
  return {
    ...schema, sections: [],
    entries: schema.entries.filter((e) => kilnGroupOf(e.path) === group && (!only || only.has(`kj:${e.path}`))).sort((a, b) => order(a) - order(b)),
  };
}

/** Why the fire is off for this start (app_info.noWebgl). */
export const fireOff = (info: AppInfo | undefined) =>
  info?.render?.mode === "software" ? t("settings.appearance.fireOffSoftware") : t("settings.appearance.fireOffEnv");

/** The AppImage's app menu entry: what it does, where it is, what is in the way. */
export function desktopHint(d: DesktopInfo): string {
  if (d.foreign) return t("settings.appearance.desktopHintForeign", { file: d.file });
  return d.on && d.installed ? t("settings.appearance.desktopHintFile", { file: d.file }) : t("settings.appearance.desktopHint");
}

/** What the render switch says: when it applies, how this start draws and why, who turned it on. */
export function renderHint(r: RenderInfo): string {
  const sw = r.mode === "software";
  const now = r.why === "restart" ? (sw ? t("settings.render.noGpuRestart") : t("settings.render.gpuRestart"))
    : r.why === "environment" ? (sw ? t("settings.render.noGpuEnv") : t("settings.render.gpuEnv"))
    : r.why === "argument" ? (sw ? t("settings.render.noGpuArg", { flag: "--software-render" }) : t("settings.render.gpuArg", { flag: "--gpu-render" }))
    : r.why === "marker" && sw && r.marker ? t("settings.render.noGpuMarker")
    : sw ? t("settings.render.noGpu") : t("settings.render.gpu");
  // markerWhy: the backend's sentence, as it is
  return `${t("settings.render.intro")} ${now}${r.marker && r.markerWhy ? ` ${r.markerWhy}.` : ""}`;
}

/** What the X11 switch says (Linux, a Wayland session with XWayland): what it does, and how this start draws. */
export function x11Hint(x: X11Info): string {
  const now = x.why === "yours" ? t("settings.render.x11Yours", { value: x.yours ?? "" })
    : x.on ? t("settings.render.x11On")
    : x.why === "tried" ? t("settings.render.x11Tried")
    : x.why === "software" ? t("settings.render.x11Software")
    : t("settings.render.x11Wayland");
  return `${t("settings.render.x11Intro")} ${now}`;
}

/** What "Force GPU drawing on NVIDIA" says: what it does and that it is off by default, why the launcher turned it off by
 *  itself (a forced start failed), and what this start does. */
export function forceHint(f: ForceInfo): string {
  const now = f.why === "yours" ? t("settings.render.forceYours")
    : f.why === "on" ? t("settings.render.forceOn")
    : f.why === "failed" ? t("settings.render.forceFailed")
    : f.why === "recovery" ? t("settings.render.forceRecovery")
    : f.why === "recovered" ? t("settings.render.forceRecovered")
    : f.why === "unguarded" ? t("settings.render.forceUnguarded")
    : f.why === "unused" ? t("settings.render.forceUnused")
    : f.blocked ? t("settings.render.forceBlocked")
    : t("settings.render.forceOff");
  // failedWhy: the backend's sentence, as it is (only while the switch is still off)
  return `${t("settings.render.forceIntro")} ${now}${f.failedWhy && !f.on ? ` ${f.failedWhy}.` : ""}`;
}

/** What the sandbox switch says: what off means and when it applies, what WebKit does in this start and why, who turned
 *  it off, what the probe of bwrap found when it failed. */
export function sandboxHint(sb: SandboxInfo): string {
  const now = sb.yours !== undefined ? t("settings.sandbox.yours", { value: sb.yours })
    : sb.why === "argument" ? (sb.want === "off" ? t("settings.sandbox.argOff", { flag: "--no-webkit-sandbox" }) : t("settings.sandbox.argOn", { flag: "--webkit-sandbox" }))
    : sb.why === "environment" ? t("settings.sandbox.env")
    : sb.webkit === "unused" ? t("settings.sandbox.unused")
    : sb.webkit === "on" ? t("settings.sandbox.on")
    : t("settings.sandbox.off");
  const probe = sb.probeFailed ? ` ${t("settings.sandbox.probe", { says: sb.probeSays })}` : "";
  // markerWhy: the backend's sentence, as it is
  return `${t("settings.sandbox.intro")} ${now}${sb.marker && sb.markerWhy ? ` ${sb.markerWhy}.` : ""}${probe}`;
}

/** The Steam Deck line: this start's flag, the signals found, or none. */
export function deckLine(info: AppInfo | undefined): string {
  const d = info?.deck;
  return d?.forced ? t("deck.modeCard.forced", { flag: `--${d.forced}` }) : d?.detected ? t("deck.modeCard.found", { signals: d.signals.join(", ") }) : t("deck.modeCard.notFound");
}

const UPSCALING = ["FSR", "FSR 3", "FSR 4", "XeSS", "AMD", "Intel", "upscaling", "upscaler"];

/** Every search item the page has now. */
export function buildIndex(c: SettingsCtx): SearchItem[] {
  const out: SearchItem[] = [];
  // More holds what the line does not say: a More that opens with the line keeps only the rest
  const add = (section: SectionId, group: string, id: string, label: string, hint?: string, more?: string, words?: string[]) =>
    out.push({ id, section, group, label, hint, more: moreBeyond(hint, more), words });
  const info = c.info;
  const join = (...xs: (string | false | null | undefined)[]) => xs.filter(Boolean).join(" · ");

  // General
  add("general", "language", "language.launcher", t("languageSettings.launcher"), t("settings.rows.languageLauncher"), t("languageSettings.launcherHint"), ["language", "translation"]);
  add("general", "language", "language.game", t("languageSettings.game"), t("settings.rows.languageGame"), t("languageSettings.gameHint"), ["language", "ui.language"]);
  const forced = !!info?.noWebgl;
  add("general", "look", "fire", t("settings.appearance.fire"), forced ? fireOff(info) : t("settings.rows.fire"), forced ? undefined : textOr("settings.rows.fireMore", "settings.appearance.fireHint"), ["WebGL", "animation"]);
  // the festive season's look (shell/season.ts): offered only during the season
  if (c.festive) add("general", "look", "festive", t("settings.appearance.festive"), t("settings.appearance.festiveHint"), t("settings.appearance.festiveMore"), ["holiday", "winter", "snow", "season", "theme"]);
  add("general", "deck", "deck.startIn", t("deck.modeCard.startIn"), t("settings.rows.deck"), deckLine(info), ["Steam Deck", "Deck mode", "desktop", "SteamOS"]);
  if (info?.desktop) add("general", "computer", "desktop", t("settings.appearance.desktop"), t("settings.rows.desktop"), desktopHint(info.desktop), ["AppImage", "icon", "taskbar"]);

  // Mods & profiles
  add("mods", "profiles", "profiles", t("profilesSettings.title"), t("profilesSettings.sub"),
    join(t("profilesSettings.saveAs"), t("profilesSettings.noProfile"), t("profilesSettings.noProfileSub"), t("profilesSettings.rename"), t("profilesSettings.folders")));
  if (c.crucible) add("mods", "crucible", "crucible", t("crucible.profiles.cardTitle"), t("crucible.profiles.cardSub"), undefined, ["Crucible", "pack", "modpack"]);
  add("mods", "adding", "addToActive", t("settings.launcher.addToActive"), t("settings.launcher.addToActiveHint"));
  add("mods", "adding", "folderNotes", t("settings.launcher.folderNotes"), t("settings.rows.folderNotes"), textOr("settings.rows.folderNotesMore", "settings.launcher.folderNotesHint"));
  add("mods", "adding", "dailyUpdates", t("settings.launcher.dailyUpdates"), t("settings.rows.dailyUpdates"),
    t(c.nexus ? "settings.launcher.dailyUpdatesHint" : "settings.launcher.dailyUpdatesHintNoNexus"));
  add("mods", "adding", "typeToConfirm", t("settings.typeToConfirm"), t("settings.rows.typeToConfirm"), t("settings.typeToConfirmHint"), ["remove", "confirm"]);
  add("mods", "library", "library", t("librarySettings.title"), t("librarySettings.sub"),
    join(t("librarySettings.hint"), t("librarySettings.cleanUp"), t("librarySettings.dataFolder"), t("librarySettings.keptVersions")), ["data folder", "disk", "storage"]);
  add("mods", "library", "keepVersions", t("settings.launcher.keepVersions"), t("settings.rows.keepVersions"), textOr("settings.rows.keepVersionsMore", "settings.launcher.keepVersionsHint"), ["roll back", "rollback"]);

  // Game & Kiln
  add("game", "games", "games.list", t("settings.games.title"), t("settings.rows.games"), t("settings.games.forgetMessage"), [t("settings.games.detect"), t("settings.games.forget"), "Steam", "install"]);
  add("game", "games", "games.add", t("settings.games.addFolder"), t("settings.rows.addFolder"));
  add("game", "games", "defaultGame", t("settings.launcher.defaultGame"), t("settings.rows.defaultGame"));
  add("game", "games", "savesKeep", t("settings.launcher.savesKeep"), t("settings.rows.savesKeep"), undefined, ["backup", "saves"]);
  const labels = { ...kilnLabels(), ...graphicsLabels() };
  for (const e of c.schema?.entries ?? []) {
    const g = kilnGroupOf(e.path);
    if (!g) continue;
    add(g === "kiln" ? "game" : g === "graphics" ? "graphics" : "advanced", g, `kj:${e.path}`, labels[e.path] ?? keyWords(e.path), e.comment ?? undefined, undefined,
      [e.path, ...(g === "graphics" ? ["DLSS", "frames"] : [])]);
  }
  if (c.client) add("game", "shroudforge", "shroudforge", t("sfBackend.backend.title"), t("sfBackend.backend.sub"),
    join(...(["hosted", "installed", "kiln"] as const).flatMap((b) => [t(`sfBackend.backend.choice.${b}`), t(`sfBackend.backend.about.${b}`)])), ["SF", "ShroudForge", "interpreter"]);

  // Graphics
  if (c.client) {
    const choice = choiceOf(c.values as never);
    add("graphics", "upscaler", "upscaler.choice", t("settings.graphics.upscaler.label"), t(`settings.graphics.upscaler.choiceHint.${choice}`), t("settings.graphics.upscaler.hint"), [...UPSCALING, "graphics.upscaler"]);
    if (choice === "amd") add("graphics", "upscaler", "upscaler.amd", t("settings.graphics.upscaler.amdLabel"), t("settings.rows.amd"), t("settings.graphics.upscaler.amdHint"), ["FSR", "FSR 4", "FSR 3.1", "graphics.amdUpscaler"]);
    add("graphics", "upscalers", "upscalers", t("settings.upscalers.title"), t("settings.upscalers.sub"), t("settings.upscalers.purpose"), [...UPSCALING, "download", "licence", "license"]);
  }

  // Updates & online
  if (c.dist === "nexus") add("online", "updates", "updates.nexus", t("settings.groups.updates"), t("updateSettings.newVersions", { link: "Nexus Mods" }), t("updateSettings.nexusBeta", { link: t("updateSettings.downloadPage") }), ["update", "version", "beta"]);
  else {
    add("online", "updates", "updates.auto", t("updateSettings.auto"), t("updateSettings.autoHint"), undefined, ["update"]);
    add("online", "updates", "updates.channel", t("updateSettings.channel"), t("settings.rows.channel"), undefined, ["beta", "release", "test build", "update"]);
    add("online", "updates", "updates.state", t("settings.rows.updateState"), t("updateSettings.checkNow"), undefined, ["update", "version"]);
  }
  if (c.nexus) {
    add("online", "nexus", "nexus", t("nexusSettings.title"), t("nexusSettings.sub"), join(t("nexusSettings.signIn"), t("nexusSettings.signOut")), ["Nexus", "account", "premium"]);
    add("online", "nexus", "nexus.handler", t("nexusSettings.handler"), t("nexusSettings.handlerHint"), undefined, ["nxm", "link"]);
  }
  add("online", "community", "community.shown", t("community.settings.show"), t("community.settings.showHint"), undefined, ["Discord", "Community"]);
  if (c.crucible) add("online", "sends", "crucibleStats", t("crucible.settings.stats"), t("crucible.settings.statsHint"), undefined, ["privacy", "count", "Crucible"]);

  // Advanced
  if (info?.render) {
    add("advanced", "drawing", "noGpu", t("settings.appearance.noGpu"), t("settings.rows.noGpu"), renderHint(info.render), ["GPU", "software", "black"]);
    if (info.render.x11) add("advanced", "drawing", "x11", t("settings.render.x11"), t("settings.rows.x11"), x11Hint(info.render.x11), ["X11", "Wayland", "XWayland", "HiDPI"]);
    if (info.render.force) add("advanced", "drawing", "forceDmabuf", t("settings.render.force"), t("settings.rows.forceDmabuf"), forceHint(info.render.force),
      ["NVIDIA", "GPU", "DMA-BUF", "WebKit", "Ubuntu", "WEBKIT_FORCE_DMABUF_RENDERER"]);
  }
  if (info?.sandbox) add("advanced", "protection", "sandbox", t("settings.sandbox.label"), t("settings.rows.sandbox"), sandboxHint(info.sandbox), ["WebKit", "sandbox", "security"]);
  add("advanced", "protection", "httpModpacks", t("settings.launcher.httpModpacks"), t("settings.launcher.httpModpacksHint"), undefined, ["http", "https", "modpack"]);
  add("advanced", "steam", "steamRoots", t("settings.launcher.steamRoots"), t("settings.launcher.steamRootsHint"), undefined, ["Steam", "library", "folder"]);
  add("advanced", "art", "art", t("settings.art.title"), t("settings.art.sub"), join(t("settings.art.editor"), t("settings.art.admin")), ["icons", "map"]);
  if (c.tauri) add("advanced", "developer", "devMock", t("settings.dev.mock"), t("settings.dev.mockHint"), undefined, ["developer", "mock"]);
  return out;
}

/** The selected upscaler's kiln.json value, for the Graphics line in the list. */
export const fpsCapOf = (values: unknown): number | null => {
  const v = getPath(values as never, "graphics.fpsCap");
  return typeof v === "number" ? v : null;
};
