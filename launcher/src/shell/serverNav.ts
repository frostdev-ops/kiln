// One grouped list per server (it replaces the server tabs and the admin tabs): Run, World, Data, Files. Each entry is a
// route under /servers/:id/. Configs, Mods and the console are merged pages (live while the server runs, the files
// while it is stopped).

import { t } from "../i18n";

export interface ServerNavItem {
  /** The word in the current language (a getter: read it at render). */
  readonly label: string;
  /** Route under /servers/:id/. */
  to: string;
}

export const SERVER_NAV: { readonly group: string; items: ServerNavItem[] }[] = [
  { get group() { return t("shell.serverNav.groupRun"); }, items: [
    { get label() { return t("shell.serverNav.overview"); }, to: "overview" },
    { get label() { return t("shell.serverNav.logs"); }, to: "logs" },
    { get label() { return t("shell.serverNav.tickStats"); }, to: "ticks" },
  ] },
  { get group() { return t("shell.serverNav.groupWorld"); }, items: [
    { get label() { return t("shell.serverNav.players"); }, to: "admin/players" },
    { get label() { return t("shell.serverNav.map"); }, to: "admin/map" },
    { get label() { return t("shell.serverNav.zones"); }, to: "admin/zones" },
    { get label() { return t("shell.serverNav.spawn"); }, to: "admin/spawn" },
    { get label() { return t("shell.serverNav.creatures"); }, to: "admin/creatures" },
    { get label() { return t("shell.serverNav.combat"); }, to: "admin/combat" },
    { get label() { return t("shell.serverNav.world"); }, to: "admin/world" },
  ] },
  { get group() { return t("shell.serverNav.groupData"); }, items: [
    { get label() { return t("shell.serverNav.items"); }, to: "admin/items" },
    { get label() { return t("shell.serverNav.gameData"); }, to: "admin/gamedata" },
    { get label() { return t("shell.serverNav.content"); }, to: "admin/content" },
    { get label() { return t("shell.serverNav.permissions"); }, to: "admin/permissions" },
  ] },
  { get group() { return t("shell.serverNav.groupFiles"); }, items: [
    { get label() { return t("shell.serverNav.settings"); }, to: "settings" },
    { get label() { return t("shell.serverNav.configs"); }, to: "configs" },
    { get label() { return t("shell.serverNav.mods"); }, to: "mods" },
    { get label() { return t("shell.serverNav.backups"); }, to: "backups" },
    { get label() { return t("shell.serverNav.saves"); }, to: "admin/saves" },
    { get label() { return t("shell.serverNav.modpack"); }, to: "modpack" },
    { get label() { return t("shell.serverNav.crashes"); }, to: "admin/crashes" },
  ] },
];

/** The list entry for a path inside a server ("/servers/lab/admin/items" → Items). */
/** Old admin routes (`/servers/:id/admin/<from>`) merged into the server's own pages (`/servers/:id/<to>`). App.tsx
 *  redirects them in the router itself: an old link never waits for the admin pages' bundle to load. */
export const ADMIN_MERGED: [from: string, to: string][] = [["configs", "configs"], ["mods", "mods"], ["console", "overview"]];

export function serverNavFor(rest: string): ServerNavItem | null {
  const all = SERVER_NAV.flatMap((g) => g.items);
  return all.find((i) => rest === i.to || rest.startsWith(i.to + "/")) ?? null;
}
