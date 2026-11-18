// Protection zones, shared by the Zones page and the map. The op is Kiln.Admin's zones_add (kiln.gameplay; kept in kiln/data/kiln-admin/zones.json).
import { create } from "zustand";
import type { AdminPos } from "../api/admin";
import type { Json } from "../api/commands";
import { t } from "../i18n";
import { steamIdError } from "./common";

export const ACTIONS = ["build", "break", "terraform", "interact", "loot", "craft", "spawn"] as const;
export const DEFAULT_DENY = ["build", "break", "terraform", "interact", "loot"];
/** What a zone action is called (unknown actions show as they are). */
export function actionLabel(a: string): string {
  switch (a) {
    case "build": return t("admin.zones.action.build");
    case "break": return t("admin.zones.action.break");
    case "terraform": return t("admin.zones.action.terraform");
    case "interact": return t("admin.zones.action.interact");
    case "loot": return t("admin.zones.action.loot");
    case "craft": return t("admin.zones.action.craft");
    case "spawn": return t("admin.zones.action.spawn");
    default: return a;
  }
}
/** What a zone action covers (tooltip). */
export function actionHint(a: string): string | undefined {
  switch (a) {
    case "build": return t("admin.zones.actionHint.build");
    case "break": return t("admin.zones.actionHint.break");
    case "terraform": return t("admin.zones.actionHint.terraform");
    case "interact": return t("admin.zones.actionHint.interact");
    case "loot": return t("admin.zones.actionHint.loot");
    case "craft": return t("admin.zones.actionHint.craft");
    case "spawn": return t("admin.zones.actionHint.spawn");
    default: return undefined;
  }
}

export interface ZoneForm {
  name: string;
  x: string;
  y: string;
  z: string;
  radius: string;
  deny: string[];
  owners: string[];
  allowAdmins: boolean;
  editing: boolean;
}
export const blankZone = (): ZoneForm => ({ name: "", x: "", y: "", z: "", radius: "30", deny: [...DEFAULT_DENY], owners: [], allowAdmins: true, editing: false });

/** zones_add args, or the first problem. */
export function zoneArgs(f: ZoneForm): { args?: Record<string, Json>; error?: string } {
  const name = f.name.trim();
  if (!name || name.length > 64) return { error: t("admin.zones.nameRange") };
  const num = (s: string) => (s.trim() === "" ? NaN : Number(s));
  const pos = { x: num(f.x), y: num(f.y), z: num(f.z) };
  if (![pos.x, pos.y, pos.z].every(Number.isFinite)) return { error: t("admin.zones.needCentre") };
  if (![pos.x, pos.y, pos.z].every((v) => v >= -1e6 && v <= 1e6)) return { error: t("admin.zones.centreRange") };
  const radius = num(f.radius);
  if (!Number.isFinite(radius) || radius < 1 || radius > 2000) return { error: t("admin.zones.radiusRange") };
  if (!f.deny.length || f.deny.some((a) => !(ACTIONS as readonly string[]).includes(a))) return { error: t("admin.zones.pickDeny", { actions: ACTIONS.join(", ") }) };
  if (f.owners.length > 64) return { error: t("admin.zones.ownersMax") };
  for (const o of f.owners) {
    const e = steamIdError(o, true);
    if (e) return { error: e };
  }
  const deny = [...new Set(f.deny)].sort((a, b) => ACTIONS.indexOf(a as never) - ACTIONS.indexOf(b as never));
  return { args: { name, x: pos.x, y: pos.y, z: pos.z, radius, deny, owners: [...new Set(f.owners)].sort(), allowAdmins: f.allowAdmins } };
}

/** A centre picked on the map for the Zones form ("Protect an area here…"). */
export const useZoneDraft = create<{ draft: AdminPos | null; set: (p: AdminPos | null) => void }>((set) => ({ draft: null, set: (draft) => set({ draft }) }));

/** The map focuses here when it opens (other pages: "Show on map"). */
export const useMapFocus = create<{ focus: { x: number; z: number; zoom?: number } | null; set: (f: { x: number; z: number; zoom?: number } | null) => void }>((set) => ({ focus: null, set: (focus) => set({ focus }) }));
