// The browser mock's creature ops (on MockWorld): creature_status, _catalog,
// _near, _inspect, _set, _ai, _reset, _spawn, _variants, _command with the shapes Kiln.Admin's Ops.Creatures.cs answers.
// Synthetic content only (the fixture's "Creature A..." templates); creatures appear when spawned.
import type { MockWorld } from "./world";
import { OpError } from "./world";

type Obj = Record<string, unknown>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Any = any;

const FACTIONS = ["Fogger", "Scavenger", "Wildlife", "Skeleton"];
const FIELDS = ["level", "faction", "scale", "xp", "speed", "damage", "home", "loot-labels", "loot-slots", "loot-off", "feature-on", "feature-off", "arsenal"];
export const CREATURE_OPS = ["creature_status", "creature_catalog", "creature_near", "creature_inspect", "creature_set", "creature_ai", "creature_reset", "creature_spawn", "creature_variants", "creature_command"];

const n = (req: Obj, k: string): number => {
  const v = req[k];
  if (typeof v !== "number" || !Number.isFinite(v)) throw new OpError(`${k} must be a number`);
  return v;
};

interface Mob {
  info: Any;
  base: Any;
  layers: number;
}

/** Adds the creature ops to a MockWorld (called from its constructor). */
export function installCreatures(w: MockWorld) {
  const W = w as unknown as Record<string, unknown> & { ops: string[]; nextEntity: number };
  for (const op of CREATURE_OPS) if (!W.ops.includes(op)) W.ops.push(op);
  const catalog = (w.fx.creatures ?? []).map((c, i) => ({
    guid: c.guid, name: c.template, display: c.name, kind: i % 5 === 4 ? "wildlife" : "enemy", faction: FACTIONS[i % FACTIONS.length],
    level: 1 + (i % 4) * 5, levelMax: 30, boss: i % 7 === 6, arsenal: null, runtime: false, loaded: true,
  }));
  const mobs = new Map<number, Mob>();
  const get = (req: Obj): Mob => {
    const m = mobs.get(n(req, "entity"));
    if (!m) throw new OpError("not a creature");
    return m;
  };
  const view = (m: Mob) => JSON.parse(JSON.stringify(m.info));
  Object.assign(W, {
    op_creature_status: () => ({
      slots: true, aiHooks: true, readyBit: true, arsenals: true, reflection: true, commands: true, marks: true, labReady: false,
      catalog: catalog.length, catalogLoaded: catalog.length, engineCreaturesMissing: 0, arsenalCount: 12, entities: mobs.size,
      layers: [...mobs.values()].reduce((a, m) => a + m.layers, 0), privateCopies: 0, pendingFree: 0, stateChanges: 0,
      eventsDelivered: 0, eventsDropped: 0, commandsSent: 0, marksWritten: 0, copyReads: 0,
    }),
    op_creature_catalog: (req: Obj) => {
      const f = typeof req.filter === "string" ? req.filter.toLowerCase() : "";
      const k = typeof req.kind === "string" ? req.kind : "";
      return catalog.filter((c) => (!f || c.name.toLowerCase().includes(f) || c.display.toLowerCase().includes(f)) && (!k || c.kind === k));
    },
    op_creature_near: (req: Obj) => {
      const at = { x: n(req, "x"), y: n(req, "y"), z: n(req, "z") };
      const r = n(req, "radius");
      return [...mobs.values()]
        .map((m) => ({ m, d: Math.hypot(m.info.home.x - at.x, m.info.home.y - at.y, m.info.home.z - at.z) }))
        .filter((x) => x.d <= r).sort((a, b) => a.d - b.d).map((x) => view(x.m));
    },
    op_creature_inspect: (req: Obj) => view(get(req)),
    op_creature_set: (req: Obj) => {
      const m = get(req);
      const f = String(req.field ?? "").toLowerCase();
      const v = req.value;
      const num = () => { const x = Number(v); if (!Number.isFinite(x)) throw new OpError(`bad value '${String(v)}'`); return x; };
      switch (f) {
        case "level": m.info.level = Math.round(num()); break;
        case "faction": if (!FACTIONS.includes(String(v))) throw new OpError(`unknown faction '${String(v)}'`); m.info.faction = String(v); break;
        case "scale": m.info.scale = num(); break;
        case "xp": m.info.xp = num(); break;
        case "speed": m.info.speed = num(); break;
        case "damage": m.info.damage = num(); break;
        case "home": { const p = v as Any; m.info.home = { x: Number(p?.x), y: Number(p?.y), z: Number(p?.z) }; break; }
        case "loot-off": m.info.loot.disabled = v === true || v === "true" || v === "on"; break;
        case "feature-off": m.info.features.disabled = String(v); break;
        case "feature-on": m.info.features.enabled = String(v); break;
        default:
          if (!f.startsWith("resist:") && !FIELDS.includes(f)) throw new OpError(`unknown field '${f}' (${FIELDS.join(", ")}, resist:<kind>)`);
      }
      m.layers++;
      m.info.layers = m.layers;
      return view(m);
    },
    op_creature_ai: (req: Obj) => {
      get(req);
      const path = String(req.path ?? "");
      if (!path.includes(".")) throw new OpError(`entity: Ai.Get ${path}: invalid argument or value`);
      return { path, value: req.value !== undefined ? String(req.value) : "12", private: req.value !== undefined };
    },
    op_creature_reset: (req: Obj) => {
      const m = get(req);
      const k = m.layers;
      m.info = JSON.parse(JSON.stringify(m.base));
      m.layers = 0;
      return { reset: k };
    },
    op_creature_spawn: (req: Obj) => {
      const what = String(req.what ?? "");
      const t = catalog.find((c) => c.name === what || c.guid === what);
      if (!t) throw new OpError(`no creature template or variant '${what}'`);
      const count = req.count === undefined ? 1 : n(req, "count");
      const at = { x: n(req, "x"), y: n(req, "y"), z: n(req, "z") };
      const ids: number[] = [];
      for (let i = 0; i < count; i++) {
        const e = W.nextEntity++;
        const info = {
          entity: e, template: t.guid, templateName: t.name, kind: t.kind, hasAi: true, state: "Idle", awarenessState: 0, awareness: 0,
          faction: t.faction, level: t.level, levelMax: t.levelMax, health: 500, healthMax: 500, scale: 1, xp: 1, speed: 1, damage: 1,
          features: { template: "Move, Rotate, Patrol, Combat", enabled: "None", disabled: "None" }, target: null, guard: null,
          home: { x: at.x + i * 2, y: at.y, z: at.z }, loot: { labels: null, min: 1, max: 3, disabled: false },
          dead: false, ready: true, forcedReady: false, layers: 0, privateCopies: 0,
        };
        mobs.set(e, { info, base: JSON.parse(JSON.stringify(info)), layers: 0 });
        ids.push(e);
      }
      return { what, entities: ids };
    },
    op_creature_variants: () => ({ variants: [], spawnRules: [] }),
    op_creature_command: (req: Obj) => {
      const m = get(req);
      const a = String(req.action ?? "");
      if (!["attack", "guard", "stopguard", "stay", "heal", "moveto"].includes(a)) throw new OpError("action: attack, guard, stopguard, stay, heal or moveto");
      if (a === "moveto") m.info.home = { x: n(req, "x"), y: n(req, "y"), z: n(req, "z") };
      if (a === "attack" || a === "guard") m.info.target = n(req, "target");
      m.info.state = a === "attack" ? "Combat" : a === "guard" ? "Guard" : m.info.state;
      return { entity: m.info.entity, action: a, sent: true };
    },
  });
}
