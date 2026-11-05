// The browser mock's game data ops (on MockWorld): gamedata_status, _search,
// _get, _set, _reset, template_fields, with Kiln.Admin's Ops.GameData.cs shapes. Synthetic entries only.
import type { MockWorld } from "./world";
import { OpError } from "./world";

type Obj = Record<string, unknown>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Any = any;

const SECTIONS = ["templates", "arsenals", "sequences", "buffs", "perks", "skills", "items"];
export const GAMEDATA_OPS = ["gamedata_status", "gamedata_search", "gamedata_get", "gamedata_set", "gamedata_reset", "template_fields", "gamedata_definitions"];

export function installGameData(w: MockWorld) {
  const W = w as unknown as Record<string, unknown> & { ops: string[] };
  for (const op of GAMEDATA_OPS) if (!W.ops.includes(op)) W.ops.push(op);
  const entries: Record<string, Any[]> = {};
  for (const s of SECTIONS)
    entries[s] = Array.from({ length: 12 }, (_, i) => ({
      section: s, name: `${s.slice(0, -1)}_test_${String.fromCharCode(65 + i)}`, id: "0x" + (0x1000 + i).toString(16).padStart(8, "0"),
      guid: null, modified: false, clone: false,
      fields: { "Health.max": { type: "keen::float32", value: 100 + i * 10, original: 100 + i * 10, writable: true }, "Speed.walk": { type: "keen::float32", value: 2.5, original: 2.5, writable: true }, "Flags": { type: "keen::Flags", value: ["Move"], original: ["Move"], writable: true } },
    }));
  const section = (req: Obj) => {
    const s = String(req.section ?? "");
    if (!SECTIONS.includes(s)) throw new OpError(`section: ${SECTIONS.join(", ")}`);
    return s;
  };
  const entry = (s: string, req: Obj) => {
    const e = entries[s].find((x) => x.name === req.entry || x.id === req.entry);
    if (!e) throw new OpError(`no ${s} entry '${String(req.entry)}'`);
    return e;
  };
  const head = (e: Any) => ({ section: e.section, name: e.name, id: e.id, guid: e.guid, modified: e.modified, clone: e.clone });
  const field = (e: Any, path: string) => {
    if (!path) return Object.fromEntries(Object.entries(e.fields).map(([k, v]: [string, Any]) => [k, v.value]));
    const f = e.fields[path];
    if (!f) throw new OpError(`${e.name}.${path}: no such path`);
    return { path, type: f.type, value: f.value, original: f.original, writable: f.writable };
  };
  Object.assign(W, {
    op_gamedata_status: () => ({ ready: true, detail: "ready", sections: Object.fromEntries(SECTIONS.map((s) => [s, { available: true, count: entries[s].length, changed: entries[s].filter((e) => e.modified).length }])) }),
    op_gamedata_search: (req: Obj) => {
      const s = section(req);
      const q = String(req.q ?? "").toLowerCase();
      return entries[s].filter((e) => !q || e.name.toLowerCase().includes(q) || e.id.includes(q)).map(head);
    },
    op_gamedata_get: (req: Obj) => field(entry(section(req), req), String(req.path ?? "")),
    op_template_fields: (req: Obj) => {
      const e = entry("templates", req);
      return Object.entries(e.fields).map(([path, f]: [string, Any]) => ({ path, type: f.type, value: f.value, writable: f.writable, original: f.original }));
    },
    op_gamedata_set: (req: Obj) => {
      const e = entry(section(req), req);
      const path = String(req.path ?? "");
      const f = e.fields[path];
      if (!f) throw new OpError(`${e.name}.${path}: no such path`);
      const v = req.value as Any;
      f.value = v && typeof v === "object" && "mul" in v ? f.value * Number(v.mul) : v;
      e.modified = true;
      return { applied: 1, value: field(e, path) };
    },
    op_gamedata_reset: (req: Obj) => {
      const e = entry(section(req), req);
      const path = String(req.path ?? "");
      for (const [k, f] of Object.entries(e.fields) as [string, Any][]) if (!path || k === path) f.value = f.original;
      e.modified = false;
      return { reset: true, value: path ? field(e, path) : null };
    },
    op_gamedata_definitions: () => [],
  });
}
