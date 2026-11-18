// Header navigation: words only, no icons. Settings and About sit at the right of the header. Server and admin pages
// are reached through Servers (one grouped list per server: shell/serverNav.ts). Mods are seen and switched on Play;
// profiles are in Settings; the mods' advanced views (/mods/*) are reached from Play's list.
import { t } from "../i18n";

export interface NavItem {
  /** The word in the current language (a getter: read it at render). */
  readonly label: string;
  to: string;
}

// each label is read when it is shown, so a language change shows at the next render
export const NAV_MAIN: NavItem[] = [
  { get label() { return t("nav.play"); }, to: "/" },
  { get label() { return t("nav.catalog"); }, to: "/catalog" },
  { get label() { return t("nav.runtime"); }, to: "/runtime" },
  { get label() { return t("nav.logs"); }, to: "/logs" },
  { get label() { return t("nav.saves"); }, to: "/saves" },
  { get label() { return t("nav.editor"); }, to: "/editor" },
  { get label() { return t("nav.servers"); }, to: "/servers" },
];

export const NAV_SIDE: NavItem[] = [
  { get label() { return t("nav.settings"); }, to: "/settings" },
  { get label() { return t("nav.about"); }, to: "/about" },
];

/** Pages reached from a page, not from the header (the window title still names them). */
const NAV_INNER: NavItem[] = [{ get label() { return t("nav.mods"); }, to: "/mods" }];

/** The nav entry a path belongs to ("/" only for Play itself). */
export function navFor(path: string): NavItem | null {
  if (path === "/" || path === "") return NAV_MAIN[0];
  return [...NAV_MAIN, ...NAV_SIDE, ...NAV_INNER].filter((n) => n.to !== "/" && (path === n.to || path.startsWith(n.to + "/")))
    .sort((a, b) => b.to.length - a.to.length)[0] ?? null;
}

/** Window title part for a path. */
export function titleFor(path: string): string {
  return navFor(path)?.label ?? "Kiln";
}
