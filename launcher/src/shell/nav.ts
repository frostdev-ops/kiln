// Header navigation: words only, no icons. Settings and About sit at the right of the header. Server and admin pages
// are reached through Servers (one grouped list per server: shell/serverNav.ts). Mods are seen and switched on Play;
// profiles are in Settings; the mods' advanced views (/mods/*) are reached from Play's list.

export interface NavItem {
  label: string;
  to: string;
}

export const NAV_MAIN: NavItem[] = [
  { label: "Play", to: "/" },
  { label: "Catalog", to: "/catalog" },
  { label: "Runtime", to: "/runtime" },
  { label: "Logs", to: "/logs" },
  { label: "Saves", to: "/saves" },
  { label: "Editor", to: "/editor" },
  { label: "Servers", to: "/servers" },
];

export const NAV_SIDE: NavItem[] = [
  { label: "Settings", to: "/settings" },
  { label: "About", to: "/about" },
];

/** Pages reached from a page, not from the header (the window title still names them). */
const NAV_INNER: NavItem[] = [{ label: "Mods", to: "/mods" }];

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
