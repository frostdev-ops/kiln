// The browser mock's Kiln.Admin: answers every op from a fixture (fixtures/synthetic.json: real
// result shapes, invented game content; or a local capture, see ./index.ts) plus a small simulated world: two players
// walking about, time of day, zones, permissions, configs, content edits, backups, mods. tools/launcher/mock-admin.py
// is the same over KAP/1 (TCP) for the Rust side and E2E tests.
import type { Json } from "../../api/commands";
import type { AdminState } from "../../api/admin";
import { installCreatures } from "./creatures";
import { installGameData } from "./gamedata";

type Obj = Record<string, unknown>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Any = any;

export interface Fixture {
  server: { id: string; name: string };
  ops: string[];
  results: Record<string, Any>;
  content: { names: Record<string, Any[]>; entries: Record<string, Any[]> };
  items: { id: number; name: string; gear: boolean }[];
  creatures: { guid: string; template: string; name: string }[];
  crashReports: Any[];
  seed: Any;
}

export class OpError extends Error {}

const clone = <T,>(v: T): T => JSON.parse(JSON.stringify(v)) as T;
const ACTIONS = ["build", "break", "terraform", "interact", "loot", "craft", "spawn"];
const NODE = /^(\*|[a-z0-9_:-]+(\.[a-z0-9_:-]+)*(\.\*)?)$/;
const SLOW: Record<string, number> = { mod_test: 1500, backup_create: 800, save_now: 500, mod_reload: 600, mod_unload: 400, mod_load: 600, teleport: 300, teleport_to_player: 300, give_item: 200, spawn: 400, kill: 200 };

function num(req: Obj, key: string): number {
  const v = req[key];
  if (typeof v !== "number" || !Number.isFinite(v)) throw new OpError(`${key} must be a number`);
  return v;
}

export class MockWorld {
  fx: Fixture;
  r: Record<string, Any>;
  t0 = Date.now();
  step = 0;
  ops: string[];
  kiln: string;
  players: Any[];
  time: Any;
  settings: Record<string, Any>;
  zones: Any[];
  perm: Any;
  configs: Any[];
  mods: Any[];
  backups: Any[];
  save: Any;
  changes = new Map<string, string>();
  spawned: number[] = [];
  nextEntity = 90000;
  combatDeaths: Any[];
  log: string[] = [];

  constructor(fx: Fixture) {
    this.fx = fx;
    this.r = fx.results;
    this.ops = [...(this.r.status?.ops ?? fx.ops ?? [])];
    this.kiln = this.r.status?.kiln ?? "0.2.0-dev";
    const seed = fx.seed ?? {};
    this.players = clone(seed.players ?? []);
    this.time = { ...(this.r.time_get ?? { hours: 9.5, clock: "09:30", day: true, dayMinutes: 30, nightMinutes: 12, weather: true }) };
    this.settings = { ...(this.r.settings_get?.values ?? {}) };
    this.zones = clone(this.r.zones_list?.length ? this.r.zones_list : seed.zones ?? []);
    this.perm = clone(this.r.permissions_get ?? { defaultGroup: "default", groups: { default: { nodes: [] } }, players: {}, userGroups: {}, nodes: [] });
    // a couple of players in the permissions file so the Players tab has something without a capture of real players
    this.perm.players ??= {};
    if (!Object.keys(this.perm.players).length) {
      this.perm.groups.builders ??= { admin: false, inherits: ["default"], nodes: ["claims.command.claim"] };
      this.perm.players["76561198000000102"] = { name: "Ysolde", groups: ["builders"], nodes: ["-claims.command.claim"] };
      this.perm.userGroups ??= {};
      this.perm.userGroups.Friend = ["builders"];
    }
    this.configs = clone(this.r.config_list ?? []);
    this.mods = clone(this.r.mods ?? []);
    this.backups = clone(this.r.backup_list?.length ? this.r.backup_list : seed.backups ?? []);
    this.save = { ...(this.r.save_status ?? {}) };
    this.combatDeaths = clone(seed.combatDeaths ?? []);
    installCreatures(this);
    installGameData(this);
    this.tick();
  }

  slow(op: string) {
    return SLOW[op] ?? 30;
  }

  // ------------------------------------------------------------------ state
  tick() {
    this.step += 25;
    const t = (Date.now() - this.t0) / 1000;
    this.players.forEach((p, i) => {
      const [hx, hy, hz] = p.home;
      const a = t / (40 + 15 * i) + i;
      p.pos = { x: +(hx + 60 * Math.cos(a)).toFixed(2), y: +(hy + 3 * Math.sin(a * 3)).toFixed(2), z: +(hz + 45 * Math.sin(a)).toFixed(2) };
    });
    const per = 24 / (60 * ((this.time.dayMinutes ?? 30) + (this.time.nightMinutes ?? 12)));
    this.setHours((this.time.hours + per) % 24);
  }

  setHours(h: number) {
    this.time.hours = +h.toFixed(4);
    this.time.clock = `${String(Math.floor(h)).padStart(2, "0")}:${String(Math.floor((h % 1) * 60)).padStart(2, "0")}`;
    this.time.day = h >= 6 && h < 20;
  }

  playerList() {
    return this.players.map(({ home: _home, ...p }) => clone(p));
  }

  state(): AdminState {
    const st: AdminState = { clock: +(this.step / 24.5).toFixed(2), kiln: this.kiln, ready: true, players: this.playerList(), deaths: [], spawned: this.spawned.length };
    if (this.combatDeaths.length) st.combatDeaths = clone(this.combatDeaths.slice(-50));
    st.mods = {
      loaded: this.mods.filter((m) => m.state === "Loaded").length,
      failed: this.mods.filter((m) => m.state === "Failed").length,
      disabled: this.mods.filter((m) => m.state === "Disabled").length,
      phase: "Running",
    };
    return st;
  }

  say(line: string) {
    const d = new Date();
    this.log.push(`[I ${d.toTimeString().slice(0, 8)}.000] [mod:kiln-admin] ${line}`);
    if (this.log.length > 500) this.log.splice(0, this.log.length - 500);
  }

  player(req: Obj, key = "entity") {
    const e = req[key];
    if (typeof e !== "number" || e < 1 || !Number.isInteger(e)) throw new OpError(`${key} must be an entity id`);
    const p = this.players.find((q) => q.entity === e);
    if (!p) throw new OpError("That player is no longer in the world.");
    return p;
  }

  // ------------------------------------------------------------------ ops
  run(op: string, req: Obj): Json {
    if (!this.ops.includes(op)) throw new OpError(`unknown op '${op}'`);
    const h = (this as unknown as Record<string, (r: Obj) => unknown>)["op_" + op];
    if (h) return h.call(this, req) as Json;
    if (op in this.r) return clone(this.r[op]) as Json;
    throw new OpError(`the mock has no answer for '${op}'`);
  }

  op_ping() {
    return { pong: true, clock: this.step / 24.5, kiln: this.kiln };
  }
  op_admin_info() {
    return { ...clone(this.r.admin_info ?? {}), ops: this.ops, uptimeS: (Date.now() - this.t0) / 1000, server: this.fx.server };
  }
  op_players() {
    return this.playerList();
  }
  op_teleport(req: Obj) {
    const p = this.player(req);
    const to = { x: num(req, "x"), y: num(req, "y"), z: num(req, "z") };
    p.home = [to.x, to.y, to.z];
    this.say(`teleport ${p.name} to ${to.x.toFixed(0)}, ${to.y.toFixed(0)}, ${to.z.toFixed(0)}`);
    return { entity: p.entity, to, method: "kiln-teleport", position: to, distance: 0.4, steps: 3, flags: "Moved" };
  }
  op_teleport_to_player(req: Obj) {
    const p = this.player(req), o = this.player(req, "target");
    p.home = [o.pos.x + 2, o.pos.y + 1, o.pos.z];
    return { entity: p.entity, to: o.pos, method: "kiln-teleport", position: o.pos, distance: 2, steps: 2, flags: "Moved" };
  }
  op_enemies(req: Obj) {
    const out = clone(this.r.enemies ?? { near: [], catalog: [], total: 0 });
    const c = req.near as Any;
    if (c && typeof c === "object") {
      const cat = this.fx.creatures ?? [];
      let seed = (Math.floor(c.x) * 31 + Math.floor(c.z)) >>> 0;
      const rnd = () => ((seed = (seed * 1103515245 + 12345) >>> 0) / 2 ** 32);
      for (let i = 0; i < Math.min(6, cat.length); i++) {
        const g = cat[Math.floor(rnd() * cat.length)];
        out.near.push({ entity: 70000 + i, pos: { x: c.x + (rnd() - 0.5) * 120, y: c.y ?? 40, z: c.z + (rnd() - 0.5) * 120 }, guid: g.guid, name: g.template });
      }
      const seen = new Map<string, Any>();
      for (const e of out.near) seen.set(e.guid, { guid: e.guid, name: e.name, seen: (seen.get(e.guid)?.seen ?? 0) + 1 });
      out.catalog = [...seen.values()];
      out.total = Math.max(out.total, out.near.length + 37);
    }
    return out;
  }
  op_item_search(req: Obj) {
    const q = req.q;
    if (typeof q !== "string") throw new OpError("q required");
    const ql = q.toLowerCase();
    return this.fx.items.filter((i) => i.name.toLowerCase().includes(ql)).sort((a, b) => Number(!a.name.toLowerCase().startsWith(ql)) - Number(!b.name.toLowerCase().startsWith(ql)) || a.name.length - b.name.length).slice(0, 50);
  }
  op_inventory(req: Obj) {
    const p = this.player(req);
    const slots = this.fx.items.slice(0, 12).map((it, n) => ({ item: it.id, count: it.gear ? 1 : 5 + 7 * n, name: it.name, ...(it.gear ? { level: 12, rarity: "Rare", equipped: n % 2 === 0 } : {}) }));
    return { inventories: [{ link: 1, entity: p.entity + 1, category: "Backpack", slots: slots.slice(0, 8) }, { link: 2, entity: p.entity + 2, category: "Equipment", slots: slots.slice(8) }], generic: 40, available: 32 };
  }
  op_give_item(req: Obj) {
    const p = this.player(req);
    const item = num(req, "item"), count = num(req, "count");
    if (count < 1 || count > 100000) throw new OpError("count must be 1 to 100000");
    const it = this.fx.items.find((i) => i.id === item);
    if (!it) throw new OpError(`unknown item ${item}`);
    this.say(`gave ${p.name} ${count} x ${it.name}`);
    return { given: count, requested: count, stacks: Math.max(1, Math.ceil(count / 500)), status: "Done" };
  }
  op_spawn(req: Obj) {
    const g = req.guid;
    if (typeof g !== "string" || !/^[0-9a-fA-F-]{36}$/.test(g)) throw new OpError("guid must be a template GUID");
    const count = req.count === undefined ? 1 : num(req, "count");
    if (count < 1 || count > 20) throw new OpError("count must be 1 to 20");
    num(req, "x"); num(req, "y"); num(req, "z");
    for (let i = 0; i < count; i++) this.spawned.push(++this.nextEntity);
    return { spawned: count, failed: 0, reason: null };
  }
  op_despawn_spawned() {
    const n = this.spawned.length;
    this.spawned = [];
    return { removed: n, failed: 0, missing: 0 };
  }
  op_spawn_item() {
    return { entity: ++this.nextEntity };
  }
  op_settings_get(req: Obj) {
    const paths = Array.isArray(req.paths) ? (req.paths as string[]) : null;
    return { values: Object.fromEntries(Object.entries(this.settings).filter(([k]) => !paths || paths.includes(k))), write_supported: true };
  }
  op_settings_set(req: Obj) {
    const path = req.path;
    if (typeof path !== "string") throw new OpError("path required");
    if (!("value" in req)) throw new OpError("value required");
    if (!(path in this.settings)) throw new OpError(`unknown setting '${path}'`);
    const before = this.settings[path], v = req.value;
    if (typeof before === "boolean" && typeof v !== "boolean") throw new OpError(`${path} is a bool`);
    if (typeof before === "number" && typeof v !== "number") throw new OpError(`${path} is a number`);
    this.settings[path] = v;
    return { path, before, after: v };
  }
  op_settings_save() {
    return { saved: true };
  }
  op_time_get() {
    return { ...this.time };
  }
  op_time_set(req: Obj) {
    if (typeof req.clock === "string") {
      const m = /^(\d{1,2}):(\d{2})$/.exec(req.clock.trim());
      if (!m || +m[1] > 23 || +m[2] > 59) throw new OpError("clock must look like 19:45");
      this.setHours(+m[1] + +m[2] / 60);
    } else {
      const h = num(req, "hours");
      if (h < 0 || h >= 24) throw new OpError("hours must be 0 to <24");
      this.setHours(h);
    }
    return { ...this.time };
  }
  op_weather_reset() {
    return { reset: true };
  }
  op_broadcast(req: Obj) {
    const text = typeof req.text === "string" ? req.text.trim() : "";
    if (!text) throw new OpError("text required");
    if (text.length > 500) throw new OpError("text is longer than 500 characters");
    this.say(`broadcast: ${text}`);
    return { delivered: this.players.length };
  }
  op_tell(req: Obj) {
    this.player(req);
    this.op_broadcast(req);
    return { delivered: true };
  }
  commands(): Any[] {
    return this.r.commands_describe ?? [];
  }
  op_command_run(req: Obj) {
    const raw = req.line;
    if (typeof raw !== "string" || !raw.trim()) throw new OpError("line required");
    const line = raw.trim();
    if (line.length > 1000) throw new OpError("line must be 1 to 1000 characters");
    const name = line.replace(/^\//, "").split(" ")[0].toLowerCase();
    const cmd = this.commands().find((c) => c.name === name || (c.aliases ?? []).includes(name));
    this.say(`dashboard command${req.steamId ? " as " + req.steamId : ""}: ${line} -> ${cmd ? "ok" : "unknown"}`);
    if (!cmd) return { name, found: false, ok: false, denied: false, canceled: false, parseError: null, error: `unknown command '${name}'`, output: [] };
    if (req.steamId && cmd.permission !== "everyone") return { name: cmd.name, found: true, ok: false, denied: true, canceled: false, parseError: null, error: `you may not use /${cmd.name}`, output: [] };
    const output = cmd.name === "help" ? this.commands().map((c) => `/${c.name} - ${c.description ?? ""}`) : cmd.name === "claim" ? ["Claims: Spawn village (60 m), Ysolde base (35 m)"] : [`(mock) ran ${line}`];
    return { name: cmd.name, found: true, ok: true, denied: false, canceled: false, parseError: null, error: null, output };
  }
  op_command(req: Obj) {
    const r = this.op_command_run(req);
    if (!r.found) throw new OpError(r.error!);
    return { output: r.output };
  }
  op_command_complete(req: Obj) {
    const text = typeof req.text === "string" ? req.text : "";
    const off = text.startsWith("/") ? 1 : 0;
    const body = text.slice(off);
    const cmds = this.commands();
    if (!body.includes(" ")) {
      const names = [...new Set(cmds.flatMap((c) => [c.name, ...(c.aliases ?? [])]))].sort();
      return { start: off, suggestions: names.filter((n) => n.startsWith(body.toLowerCase())).slice(0, 40) };
    }
    const [name, ...restParts] = body.split(" ");
    const word = restParts[restParts.length - 1];
    const start = text.length - word.length;
    const cmd = cmds.find((c) => c.name === name || (c.aliases ?? []).includes(name));
    let sugg: string[] = [];
    if (cmd?.tree) {
      let node = cmd.tree;
      for (let i = 0; i < restParts.length - 1; i++) node = (node.children ?? [])[0] ?? {};
      for (const ch of node.children ?? []) {
        if (ch.kind === "literal") sugg.push(ch.name);
        else if (ch.options) sugg.push(...ch.options);
        else if (ch.name === "command") sugg.push(...cmds.map((c) => c.name));
        else sugg.push(`<${ch.name}>`);
      }
    } else if (cmd?.name === "claim" && restParts.length === 1) sugg = ["list", "here", "trust", "remove", "reload"];
    return { start, suggestions: sugg.filter((s) => s.startsWith("<") || s.toLowerCase().startsWith(word.toLowerCase())).slice(0, 40) };
  }
  op_health(req: Obj) {
    const e = req.entity;
    const p = this.players.find((q) => q.entity === e);
    if (p) return { entity: e, health: p.health, healthMax: p.healthMax, dead: p.health <= 0, god: p.god };
    if (typeof e === "number" && (this.spawned.includes(e) || e >= 70000)) return { entity: e, health: 420, healthMax: 420, dead: false, god: false };
    throw new OpError("That entity has no health.");
  }
  op_heal(req: Obj) {
    const p = this.player(req);
    const before = p.health;
    p.health = typeof req.amount === "number" ? Math.min(p.healthMax, p.health + req.amount) : p.healthMax;
    return { entity: p.entity, healed: p.health - before, health: p.health };
  }
  op_god(req: Obj) {
    const p = this.player(req);
    p.god = typeof req.on === "boolean" ? req.on : !p.god;
    return { entity: p.entity, god: p.god };
  }
  op_kill(req: Obj) {
    const e = req.entity;
    const p = this.players.find((q) => q.entity === e);
    if (p) {
      if (p.god) return { entity: e, killed: false, state: "Invulnerable" };
      p.health = 0;
      this.combatDeaths.push({ entity: e, killer: 0, origin: "Kiln", pos: { ...p.pos }, time: Math.floor(Date.now() / 1000), victimName: p.name, killerKind: "none" });
      setTimeout(() => (p.health = p.healthMax), 5000);
      return { entity: e, killed: true, state: "Killed" };
    }
    if (typeof e === "number" && this.spawned.includes(e)) {
      this.spawned = this.spawned.filter((x) => x !== e);
      return { entity: e, killed: true, state: "Killed" };
    }
    throw new OpError("That entity has no health.");
  }
  op_save_status() {
    return { ...this.save };
  }
  op_save_now() {
    this.save.revision = (this.save.revision ?? 0) + 1;
    this.save.lastSaved = new Date().toISOString();
    this.save.lastReason = "Requested";
    this.save.written = (this.save.written ?? 0) + 1;
    return { revision: this.save.revision, ms: 212.4 };
  }
  op_backup_list() {
    return clone(this.backups);
  }
  op_backup_create(req: Obj) {
    const name = req.name;
    if (typeof name !== "string" || !/^(?!\.)[A-Za-z0-9._-]{1,64}$/.test(name)) throw new OpError("name: letters, digits, - _ . (up to 64)");
    this.op_save_now();
    const now = new Date();
    const stamp = now.toISOString().replace(/[-:]/g, "").replace("T", "-").slice(0, 15);
    const b = { name, folder: `${stamp}-${name}`, created: now.toISOString(), world: this.save.world, revision: this.save.revision, bytes: 6481920, files: 3, dataBundles: 2, reason: "Backup" };
    this.backups.push(b);
    return b;
  }
  op_backup_restore(req: Obj) {
    const b = this.backups.find((x) => x.folder === req.name || x.name === req.name);
    if (!b) throw new OpError(`no backup '${String(req.name)}'`);
    this.save.pendingRestore = b.folder;
    return { staged: clone(b), note: "restart the server to restore" };
  }
  op_backup_cancel_restore() {
    const had = !!this.save.pendingRestore;
    this.save.pendingRestore = null;
    return { canceled: had };
  }
  op_zones_list() {
    return clone(this.zones);
  }
  op_gameplay_status() {
    return { ...(this.r.gameplay_status ?? { available: true }), zones: this.zones.length };
  }
  op_zones_add(req: Obj) {
    const name = typeof req.name === "string" ? req.name.trim() : "";
    if (!name || name.length > 64) throw new OpError("name required (up to 64 characters)");
    const radius = num(req, "radius");
    if (radius < 1 || radius > 2000) throw new OpError("radius must be 1 to 2000 metres");
    const deny = Array.isArray(req.deny) ? (req.deny as string[]) : ["build", "break", "terraform", "interact", "loot"];
    for (const a of deny) if (!ACTIONS.includes(a)) throw new OpError(`unknown action '${a}' (build, break, terraform, interact, loot, craft, spawn)`);
    const owners = (Array.isArray(req.owners) ? req.owners : []).map(String);
    for (const o of owners) if (!/^\d+$/.test(o) || BigInt(o) <= 76561197960265728n) throw new OpError("owners must be SteamID64s");
    const z = { name, id: 100 + this.zones.length + 1, active: true, pos: { x: num(req, "x"), y: num(req, "y"), z: num(req, "z") }, radius, deny, owners, allowAdmins: req.allowAdmins !== false };
    this.zones = [...this.zones.filter((x) => x.name.toLowerCase() !== name.toLowerCase()), z];
    this.say(`zone '${name}' set: radius ${radius} m`);
    return this.op_zones_list();
  }
  op_zones_remove(req: Obj) {
    const name = String(req.name ?? "");
    if (!this.zones.some((z) => z.name.toLowerCase() === name.toLowerCase())) throw new OpError(`no zone named '${name}'`);
    this.zones = this.zones.filter((z) => z.name.toLowerCase() !== name.toLowerCase());
    return this.op_zones_list();
  }

  // content: captured entries; set/reset edit a layer on top
  kindOf(req: Obj) {
    const k = req.kind;
    if (k !== "items" && k !== "recipes" && k !== "loot" && k !== "drops") throw new OpError("kind must be items, recipes, loot or drops");
    return k;
  }
  entry(req: Obj) {
    const kind = this.kindOf(req);
    const key = req.entry;
    if (!key) throw new OpError("entry required");
    const e = (this.fx.content.entries[kind] ?? []).find((x) => x.name === key || String(x.id).toLowerCase() === String(key).toLowerCase());
    if (!e) {
      const hit = (this.fx.content.names[kind] ?? []).find((x) => x.name === key || x.id === key);
      if (hit) return this.synthEntry(kind, hit);
      throw new OpError(`no ${kind} entry '${String(key)}'`);
    }
    return e;
  }
  /** names without a captured entry: borrow the fields of the kind's first entry */
  synthEntry(kind: string, hit: Any) {
    const base = (this.fx.content.entries[kind] ?? [])[0];
    const e = base ? { ...clone(base), id: hit.id, name: hit.name, guid: "00000000-0000-0000-0000-000000000000" } : { kind, id: hit.id, name: hit.name, guid: "", modified: false, fields: [] };
    (this.fx.content.entries[kind] ??= []).push(e);
    return e;
  }
  op_content_search(req: Obj) {
    const kind = this.kindOf(req);
    const q = typeof req.q === "string" ? req.q.trim() : "";
    if (!q) throw new OpError("q required");
    const ql = q.toLowerCase();
    return (this.fx.content.names[kind] ?? [])
      .filter((n) => n.name.toLowerCase().includes(ql) || n.id === q)
      .sort((a, b) => Number(!a.name.toLowerCase().startsWith(ql)) - Number(!b.name.toLowerCase().startsWith(ql)) || a.name.length - b.name.length)
      .slice(0, typeof req.limit === "number" ? req.limit : 30)
      .map((n) => ({ ...n, modified: [...this.changes.keys()].some((k) => k.startsWith(`${kind}|${n.name}|`)) }));
  }
  op_content_entry(req: Obj) {
    const kind = this.kindOf(req);
    const e = clone(this.entry(req));
    for (const f of e.fields) {
      const k = `${kind}|${e.name}|${f.path}`;
      if (this.changes.has(k)) f.value = this.changes.get(k);
      f.changed = f.value !== f.original;
    }
    e.modified = e.fields.some((f: Any) => f.changed);
    return e;
  }
  field(req: Obj) {
    const e = this.entry(req);
    const path = req.field;
    if (!path) throw new OpError("field required");
    const f = e.fields.find((x: Any) => x.path === path);
    if (!f) throw new OpError(`no field '${String(path)}'`);
    return { e, f, key: `${req.kind}|${e.name}|${f.path}` };
  }
  op_content_get(req: Obj) {
    const { f, key } = this.field(req);
    return { value: this.changes.get(key) ?? f.value, original: f.original };
  }
  op_content_set(req: Obj) {
    const { f, key } = this.field(req);
    if (!f.writable) throw new OpError(`${f.path} is read-only`);
    const v = req.value;
    if (f.enumKind === "enum") {
      if (!(f.options ?? []).includes(v)) throw new OpError(`${String(v)} is not one of ${(f.options ?? []).join(", ")}`);
    } else if (f.enumKind === "flags") {
      for (const x of String(v).split(",").map((t) => t.trim()).filter(Boolean)) if (!(f.options ?? []).includes(x)) throw new OpError(`${x} is not one of ${(f.options ?? []).join(", ")}`);
    } else if (f.valueType === "bool") {
      if (typeof v !== "boolean") throw new OpError(`${f.path} needs true or false`);
    } else if (["int", "float", "time"].includes(f.valueType)) {
      if (typeof v !== "number") throw new OpError(`${f.path} needs a number`);
      if (f.valueType === "int" && !Number.isInteger(v)) throw new OpError(`${f.path} needs a whole number`);
    }
    const text = String(v);
    this.changes.set(key, text);
    this.say(`dashboard set content ${String(req.kind)}/${String(req.entry)}.${f.path} = ${text}`);
    return { value: text, original: f.original };
  }
  op_content_reset(req: Obj) {
    const { f, key } = this.field(req);
    const had = this.changes.delete(key);
    return { reset: had, value: f.original, original: f.original };
  }
  op_content_status() {
    const s = { ...(this.r.content_status ?? {}) };
    s.changed = (s.changed ?? 0) + this.changes.size;
    return s;
  }
  op_content_fields(req: Obj) {
    const kind = this.kindOf(req);
    return clone(this.r.content_fields?.[kind] ?? []);
  }
  op_content_reload() {
    return { report: this.r.content_report?.report ?? "{}" };
  }

  // configs (validation like Kiln's ConfigFile.TrySet: types, ranges, options)
  op_config_list() {
    return clone(this.configs);
  }
  cfg(req: Obj) {
    const c = this.configs.find((x) => x.id === req.id);
    if (typeof req.id !== "string") throw new OpError("id required");
    if (!c) throw new OpError(`no config '${req.id}'`);
    return c;
  }
  op_config_get(req: Obj) {
    return clone(this.cfg(req));
  }
  op_config_reload(req: Obj) {
    return clone(this.cfg(req));
  }
  op_config_set(req: Obj) {
    const c = this.cfg(req);
    const e = c.entries.find((x: Any) => x.path === req.path);
    if (!e) throw new OpError(`no setting '${String(req.path)}' in ${c.id}`);
    const v = req.value as Any, t: string = e.type;
    if (t === "bool" && typeof v !== "boolean") throw new OpError(`${e.path}: true or false`);
    if (t === "int" || t === "float") {
      if (typeof v !== "number") throw new OpError(`${e.path}: a number`);
      if (t === "int" && !Number.isInteger(v)) throw new OpError(`${e.path}: a whole number`);
      if ((e.min != null && v < e.min) || (e.max != null && v > e.max)) throw new OpError(`${e.path}: ${e.min ?? "…"} to ${e.max ?? "…"}`);
    }
    if (t === "enum" && !(e.options ?? []).includes(v)) throw new OpError(`${e.path}: one of ${(e.options ?? []).join(", ")}`);
    if (t === "string" && typeof v !== "string") throw new OpError(`${e.path}: text`);
    if (t.startsWith("list") && !Array.isArray(v)) throw new OpError(`${e.path}: a list`);
    if (e.requiresRestart) e.fileValue = v;
    else {
      e.value = v;
      delete e.fileValue;
    }
    this.say(`dashboard set config ${c.id}: ${e.path}`);
    return clone(c);
  }

  // permissions (Kiln.Permissions ToJson shape)
  node(req: Obj) {
    const n = typeof req.node === "string" ? req.node.trim().toLowerCase() : "";
    if (!n || !NODE.test(n)) throw new OpError("node required");
    return n;
  }
  sid(req: Obj) {
    const v = String(req.steamId ?? "");
    if (!/^\d+$/.test(v) || v === "0") throw new OpError("steamId must be a SteamID64");
    return v;
  }
  static setRule(rules: string[], node: string, allow: unknown) {
    const out = rules.filter((r) => r.replace(/^-/, "") !== node);
    if (allow === true) out.push(node);
    else if (allow === false) out.push("-" + node);
    rules.splice(0, rules.length, ...out);
  }
  op_permissions_get() {
    return clone(this.perm);
  }
  op_permissions_player_node(req: Obj) {
    const sid = this.sid(req), node = this.node(req);
    const p = (this.perm.players[sid] ??= { name: req.name ?? null, groups: [], nodes: [] });
    MockWorld.setRule(p.nodes, node, req.allow);
    return this.op_permissions_get();
  }
  op_permissions_player_group(req: Obj) {
    const sid = this.sid(req);
    const g = String(req.group ?? "");
    if (!this.perm.groups[g]) throw new OpError(`no group '${g}'`);
    const p = (this.perm.players[sid] ??= { name: req.name ?? null, groups: [], nodes: [] });
    if (req.add !== false) {
      if (!p.groups.includes(g)) p.groups.push(g);
    } else p.groups = p.groups.filter((x: string) => x !== g);
    return this.op_permissions_get();
  }
  op_permissions_group(req: Obj) {
    const g = String(req.group ?? "");
    if (!/^[A-Za-z0-9_:-]{1,40}$/.test(g)) throw new OpError("group required");
    const grp = (this.perm.groups[g] ??= { admin: false, inherits: [], nodes: [] });
    if (typeof req.admin === "boolean") grp.admin = req.admin;
    if (Array.isArray(req.inherits)) {
      for (const i of req.inherits) if (!this.perm.groups[i as string]) throw new OpError(`no group '${String(i)}'`);
      grp.inherits = req.inherits;
    }
    if (req.node != null) MockWorld.setRule((grp.nodes ??= []), this.node(req), req.allow);
    return this.op_permissions_get();
  }
  op_permissions_group_remove(req: Obj) {
    const g = String(req.group ?? "");
    if (g === this.perm.defaultGroup) throw new OpError("the default group can't be removed");
    if (!this.perm.groups[g]) throw new OpError(`no group '${g}'`);
    delete this.perm.groups[g];
    for (const x of Object.values(this.perm.groups) as Any[]) x.inherits = (x.inherits ?? []).filter((i: string) => i !== g);
    for (const p of Object.values(this.perm.players ?? {}) as Any[]) p.groups = p.groups.filter((i: string) => i !== g);
    return this.op_permissions_get();
  }
  op_permissions_map_user_group(req: Obj) {
    const ug = req.userGroup;
    if (typeof ug !== "string" || !ug) throw new OpError("userGroup required");
    (this.perm.userGroups ??= {})[ug] = Array.isArray(req.groups) ? req.groups : [];
    return this.op_permissions_get();
  }
  op_permissions_check(req: Obj) {
    const node = this.node(req);
    const sid = String(req.steamId ?? "");
    const p = this.perm.players?.[sid] ?? { groups: [], nodes: [] };
    const groups = [this.perm.defaultGroup ?? "default", ...p.groups];
    const rules: [string, string][] = [...p.nodes.map((r: string) => [r, "player"] as [string, string]), ...groups.flatMap((g) => (this.perm.groups[g]?.nodes ?? []).map((r: string) => [r, `group:${g}`] as [string, string]))];
    for (const [r, from] of rules) {
      const n = r.replace(/^-/, "");
      if (n === node || n === "*" || (n.endsWith(".*") && node.startsWith(n.slice(0, -1)))) return { allowed: !r.startsWith("-"), node, level: "Everyone", rule: r, from, groups };
    }
    const level = (this.perm.nodes ?? []).find((x: Any) => x.node === node)?.level ?? "everyone";
    return { allowed: level === "everyone", node, level, rule: null, from: `default:${level}`, groups };
  }
  op_permissions_reload() {
    return this.op_permissions_get();
  }

  // mods
  mod(req: Obj) {
    const id = req.id;
    if (typeof id !== "string") throw new OpError("id required");
    if (id.toLowerCase() === "kiln-admin") throw new OpError("Kiln.Admin can't reload or unload itself from the dashboard; use kiln/control or a restart");
    const m = this.mods.find((x) => x.id === id);
    if (!m) throw new OpError(`no mod '${id}'`);
    return m;
  }
  op_mods() {
    return clone(this.mods);
  }
  op_mod_reload(req: Obj) {
    const m = this.mod(req);
    m.state = "Loaded";
    m.error = null;
    return { ok: true, message: `reloaded ${m.id}`, affected: [m.id] };
  }
  op_mod_unload(req: Obj) {
    const m = this.mod(req);
    if (m.state !== "Loaded") return { ok: false, message: `${m.id} is not loaded`, affected: [] };
    m.state = "Unloaded";
    return { ok: true, message: `unloaded ${m.id}`, affected: [m.id] };
  }
  op_mod_load(req: Obj) {
    const folder = req.folder;
    if (typeof folder !== "string" || !folder || /[\\/]|\.\./.test(folder)) throw new OpError("folder must be a folder name under kiln/mods");
    const m = this.mods.find((x) => x.folder === folder);
    if (!m) return { ok: false, message: `no mod folder '${folder}'`, affected: [] };
    m.state = "Loaded";
    m.error = null;
    return { ok: true, message: `loaded ${m.id}`, affected: [m.id] };
  }
  op_mod_test(req: Obj) {
    const m = this.mods.find((x) => x.id === req.id);
    if (!m || m.state !== "Loaded") throw new OpError(`${String(req.id)} is not loaded`);
    const results = [
      { name: `${m.id}.Loads`, outcome: "Pass", message: null },
      { name: `${m.id}.ConfigRoundTrip`, outcome: "Pass", message: null },
      { name: `${m.id}.NeedsAPlayer`, outcome: "Skip", message: "no player in the world" },
    ];
    return { mod: m.id, version: m.version, ms: 412, passed: 2, failed: 0, skipped: 1, file: `kiln/logs/tests/${m.id}.json`, results };
  }
  op_crash_reports(req: Obj) {
    const reports: Any[] = this.fx.crashReports?.length ? this.fx.crashReports : this.fx.seed?.crashReports ?? [];
    if (typeof req.name === "string" && req.name) {
      const r = reports.find((x) => x.name === req.name);
      if (!r) throw new OpError(`no crash report '${req.name}'`);
      return { name: r.name, text: r.text ?? "" };
    }
    return reports.map(({ text: _t, ...r }) => r);
  }
  op_log_tail(req: Obj) {
    const n = req.lines === undefined ? 200 : req.lines;
    if (typeof n !== "number" || !Number.isInteger(n) || n < 1 || n > 5000) throw new OpError("lines: 1 to 5000");
    const file = typeof req.file === "string" ? req.file : "kiln";
    const base: string[] = file === "game" ? ["[I 11:16:01] [server] world loaded", "[I 11:16:02] [server] session online"] : this.r.log_tail?.lines ?? [];
    const lines = [...base, ...(file === "game" ? [] : this.log)].slice(-n);
    return { file: file === "game" ? "enshrouded_server.log" : "kiln.log", lines, size: lines.reduce((a, l) => a + l.length + 1, 0) };
  }
  op_admin_token_rotate() {
    return { rotated: true };
  }
}
