// Sidebar navigation. Server and admin pages are reached through Servers.
import { Archive, Flame, Info, Layers, Library, PenTool, Play, ScrollText, Server, Settings, type LucideIcon } from "lucide-react";

export interface NavItem {
  label: string;
  to: string;
  icon: LucideIcon;
  /** Also active on these route prefixes. */
  match?: string[];
}

export const NAV_MAIN: NavItem[] = [
  { label: "Play", to: "/", icon: Play },
  { label: "Profiles", to: "/profiles", icon: Layers },
  { label: "Library", to: "/library", icon: Library },
  { label: "Runtime", to: "/runtime", icon: Flame },
  { label: "Logs", to: "/logs", icon: ScrollText },
  { label: "Saves", to: "/saves", icon: Archive },
  { label: "Editor", to: "/editor", icon: PenTool },
  { label: "Servers", to: "/servers", icon: Server },
];

export const NAV_FOOT: NavItem[] = [
  { label: "Settings", to: "/settings", icon: Settings },
  { label: "About", to: "/about", icon: Info },
];

/** Top-bar title for a path. */
export function titleFor(path: string): string {
  const all = [...NAV_MAIN, ...NAV_FOOT];
  if (path === "/" || path === "") return "Play";
  const hit = all.filter((n) => n.to !== "/" && path.startsWith(n.to)).sort((a, b) => b.to.length - a.to.length)[0];
  return hit?.label ?? "Kiln";
}
