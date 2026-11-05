// Protection zones, shared by the Zones page and the map. The op is Kiln.Admin's zones_add (kiln.gameplay; kept in kiln/data/kiln-admin/zones.json).
import { create } from "zustand";
import type { AdminPos } from "../api/admin";
import type { Json } from "../api/commands";
import { steamIdError } from "./common";

export const ACTIONS = ["build", "break", "terraform", "interact", "loot", "craft", "spawn"] as const;
export const DEFAULT_DENY = ["build", "break", "terraform", "interact", "loot"];
export const ACTION_LABEL: Record<string, string> = { build: "Build", break: "Dismantle", terraform: "Dig & terraform", interact: "Open & use", loot: "Loot & harvest", craft: "Craft", spawn: "Enemy spawns" };
export const ACTION_HINT: Record<string, string> = {
  build: "place blocks and props", break: "take blocks and props down", terraform: "dig, add or flatten terrain",
  interact: "chests, doors, stations, beds, portals, NPCs", loot: "loot, harvest, pick items up", craft: "craft at stations inside", spawn: "enemies spawning inside",
};

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
  if (!name || name.length > 64) return { error: "Zone name: 1 to 64 characters." };
  const num = (s: string) => (s.trim() === "" ? NaN : Number(s));
  const pos = { x: num(f.x), y: num(f.y), z: num(f.z) };
  if (![pos.x, pos.y, pos.z].every(Number.isFinite)) return { error: "Give the centre (x, y and z), or take it from a player or the map." };
  if (![pos.x, pos.y, pos.z].every((v) => v >= -1e6 && v <= 1e6)) return { error: "The centre is out of range." };
  const radius = num(f.radius);
  if (!Number.isFinite(radius) || radius < 1 || radius > 2000) return { error: "Radius: 1 to 2000 m." };
  if (!f.deny.length || f.deny.some((a) => !(ACTIONS as readonly string[]).includes(a))) return { error: `Pick what the zone refuses: ${ACTIONS.join(", ")}.` };
  if (f.owners.length > 64) return { error: "At most 64 owners." };
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
