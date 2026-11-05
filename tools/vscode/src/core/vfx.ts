// SPDX-License-Identifier: MIT
// VFX in blocks/*.json: which JSON places name a VFX, the game's by "game:<name>" or a GUID, a block of the mod's by name; what a game VFX takes; what blocks ask of
// the game's VFX system. The limits are the builder's (native/services/vfx/cook/vfxcook.h) and kiln lint's KL320.
import { parse } from "jsonc-parser";
import { Catalog, CatalogEntry } from "./catalog";

export const VFX_LIMITS = {
  vfx: { capacity: 2048, game: 1575 },
  meshes: { capacity: 1024, game: 638 },
  meshBytes: { capacity: 17_825_792, game: 17_567_564, margin: 16_384 },
  sounds: { capacity: 16, game: 6 },
  quadBytes: 140,
  entries: 55,
};

export type VfxPlace =
  | { kind: "reference"; holder: (string | number)[] }
  | { kind: "parameter"; holder: (string | number)[] }
  | { kind: "block"; name: string };

/** What a JSON location in a blocks file is: `path` from jsonc-parser, `atKey` when the cursor is on a property name. */
export function vfxPlace(path: (string | number)[], atKey: boolean): VfxPlace | undefined {
  const n = path.length;
  if (n === 2 && path[0] === "vfx" && atKey) return { kind: "block", name: String(path[1]) };
  if (n < 2 || path[0] === "vfx") return undefined;
  if (!atKey && path[n - 1] === "vfx") return { kind: "reference", holder: path.slice(0, -1) };
  if (!atKey && path[n - 1] === "name" && path[n - 2] === "vfx" && n > 2) return { kind: "reference", holder: path.slice(0, -2) };
  if (atKey && path[n - 2] === "parameters" && path[n - 3] === "vfx") return { kind: "parameter", holder: path.slice(0, -3) };
  return undefined;
}

/** A value's game VFX: "game:<name or GUID>", or a bare GUID. */
export function vfxGameName(value: string): string | undefined {
  if (value.startsWith("game:")) return value.slice(5);
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) ? value : undefined;
}

/** The `vfx` value at `holder` of a parsed document, as a name. */
export function vfxNameAt(text: string, holder: (string | number)[]): string | undefined {
  let v: unknown = parse(text, [], { allowTrailingComma: true });
  for (const k of [...holder, "vfx"]) {
    if (v === null || typeof v !== "object") return undefined;
    v = (v as Record<string | number, unknown>)[k];
  }
  if (typeof v === "string") return v;
  const name = v && typeof v === "object" ? (v as { name?: unknown }).name : undefined;
  return typeof name === "string" ? name : undefined;
}

/** Any name `VfxId.Game` takes for an entry, or its GUID. */
export function findGameVfx(c: Catalog, name: string): CatalogEntry | undefined {
  const guid = vfxGameName(`game:${name}`) === name && /^[0-9a-f]{8}-/i.test(name);
  const list = c.entries("vfx");
  if (guid) return list.find((e) => e.guid?.toLowerCase() === name.toLowerCase());
  return list.find((e) => e.name === name) ?? list.find((e) => Array.isArray(e.names) && (e.names as string[]).includes(name));
}

export interface VfxParameter {
  name?: string;
  hash?: string;
  type: string;
  default?: unknown;
}

export function vfxParameters(e: CatalogEntry): VfxParameter[] {
  return Array.isArray(e.parameters) ? (e.parameters as VfxParameter[]) : [];
}

/** What to write for a parameter of this type, as a snippet; undefined when a block can't set that type. */
export function parameterSnippet(p: VfxParameter): string | undefined {
  switch (p.type) {
    case "float":
      return `\${1:${typeof p.default === "number" ? p.default : 1}}`;
    case "int":
      return `{ "int": \${1:${typeof p.default === "number" ? p.default : 1}} }`;
    case "float3":
      return Array.isArray(p.default) ? `[\${1:${p.default[0]}}, \${2:${p.default[1]}}, \${3:${p.default[2]}}]` : "[${1:0}, ${2:0}, ${3:0}]";
    case "color":
      return `"\${1:${typeof p.default === "string" ? p.default.slice(0, 7) : "#ffffff"}}"`;
    default:
      return undefined;
  }
}

/** Markdown lines about a game VFX: what finds it, how long it runs, what it takes. */
export function describeGameVfx(e: CatalogEntry): string[] {
  const lines = [`\`${e.name}\`  _game VFX_`];
  const facts: string[] = [];
  if (typeof e.length === "number") facts.push(`runs ${e.length} s`);
  if (typeof e.particles === "number") facts.push(`${e.particles} particle entries`);
  if (typeof e.textures === "number" && e.textures > 0) facts.push(`${e.textures} textures`);
  if (Array.isArray(e.cullDistance)) facts.push(`culled at ${e.cullDistance[1]} m`);
  if (e.guid) facts.push(`guid \`${e.guid}\``);
  lines.push(facts.join(" · "));
  lines.push(e.byName ? "`game:<name>` finds it." : "Nothing finds it by this name at run time: use `game:<guid>`.");
  const others = [...(Array.isArray(e.names) ? (e.names as string[]) : []), ...(Array.isArray(e.usedBy) ? (e.usedBy as string[]) : [])].filter((n) => n !== e.name);
  if (others.length) lines.push(`Also: ${others.slice(0, 6).map((n) => `\`${n}\``).join(", ")}${others.length > 6 ? `, and ${others.length - 6} more` : ""}`);
  const params = vfxParameters(e);
  const named = params.filter((p) => p.name);
  if (named.length) {
    lines.push(`Parameters: ${named.map((p) => `\`${p.name}\` ${p.type}${p.default !== undefined ? ` = ${JSON.stringify(p.default)}` : ""}`).join(", ")}${named.length < params.length ? `, and ${params.length - named.length} without a known name` : ""}`);
  } else if (params.length) {
    lines.push(`${params.length} parameters, none with a known name.`);
  }
  return lines;
}

export interface VfxNeed {
  blocks: Map<string, { entries: number; particles: number; light: boolean; sound?: string }>;
  meshes: Set<string>;
  sounds: Set<string>;
}

/** What blocks files ask for, from their text. A mesh or a sound counts once, however many blocks name it. */
export function readVfxNeed(texts: string[]): VfxNeed {
  const need: VfxNeed = { blocks: new Map(), meshes: new Set(), sounds: new Set() };
  for (const text of texts) {
    const doc = parse(text, [], { allowTrailingComma: true }) as { vfx?: Record<string, unknown> } | undefined;
    if (!doc || typeof doc.vfx !== "object" || doc.vfx === null) continue;
    for (const [name, raw] of Object.entries(doc.vfx)) {
      const b = (raw ?? {}) as { particles?: { mesh?: unknown }[]; light?: unknown; sound?: unknown };
      const particles = Array.isArray(b.particles) ? b.particles : [];
      for (const p of particles) need.meshes.add(typeof p?.mesh === "string" ? p.mesh : "quad");
      const sound = typeof b.sound === "string" ? b.sound : undefined;
      if (sound) need.sounds.add(sound);
      need.blocks.set(name, { entries: particles.length * 2 + (b.light ? 1 : 0), particles: particles.length, light: !!b.light, sound });
    }
  }
  return need;
}

/** How much of the VFX system blocks use, one line each. `gameVfx`: the catalog's count when there is one. */
export function budgetLines(need: VfxNeed, gameVfx?: number): string[] {
  const n = (v: number) => v.toLocaleString("en-US");
  const l = VFX_LIMITS;
  const game = gameVfx && gameVfx > 0 ? gameVfx : l.vfx.game;
  const bytes = need.meshes.size * l.quadBytes;
  const room = l.meshBytes.capacity - l.meshBytes.game - l.meshBytes.margin;
  return [
    `VFX: ${n(need.blocks.size)} of the ${n(l.vfx.capacity - game)} left for all mods (${n(l.vfx.capacity)}, the game uses ${n(game)})`,
    `Meshes: ${n(need.meshes.size)} of ${n(l.meshes.capacity - l.meshes.game)}`,
    `Mesh memory: ${n(bytes)} of ${n(room)} bytes`,
    `Sounds in VFX: ${n(need.sounds.size)} of ${n(l.sounds.capacity - l.sounds.game)}`,
  ];
}

/** True when anything is past a limit, as kiln lint's KL320 sees it. */
export function overBudget(need: VfxNeed, gameVfx?: number): boolean {
  const l = VFX_LIMITS;
  const game = gameVfx && gameVfx > 0 ? gameVfx : l.vfx.game;
  return (
    need.blocks.size > l.vfx.capacity - game ||
    need.meshes.size > l.meshes.capacity - l.meshes.game ||
    need.meshes.size * l.quadBytes > l.meshBytes.capacity - l.meshBytes.game - l.meshBytes.margin ||
    need.sounds.size > l.sounds.capacity - l.sounds.game
  );
}
