// SPDX-License-Identifier: MIT
// Impact assembly (.kimp format 1, docs/kiln/modding/impact.md): opcodes, directives, and the native/type/config data
// from tools/impact/data (bundled as data/impact/index.json by scripts/sync.mjs) plus a mod's impact/natives.json.

export interface NativeArg {
  name: string;
  types: string[];
  dir: string;
  doc?: string;
  raw?: boolean;
}

export interface Native {
  name: string;
  node: string;
  hash: string;
  argc: number;
  origin: string;
  args: NativeArg[];
  returns?: string;
  suspends?: boolean | string;
  doc?: string;
  query?: boolean;
  programs?: number;
}

export interface ImpactType {
  name: string;
  hash: string;
  size?: number;
  align?: number;
  qualified?: string;
  uses?: number;
}

export interface ConfigId {
  id: string;
  aliases: string[];
  types: string[];
  programs?: number;
}

export interface ImpactIndex {
  format: number;
  errors: Record<string, string>;
  natives: Native[];
  types: ImpactType[];
  configs: ConfigId[];
}

export interface Opcode {
  name: string;
  operands: number;
  doc: string;
  syntax?: string;
}

// from tools/impact/impact/ops.py (the interpreter's table) and the assembler's mnemonics
export const OPCODES: Opcode[] = [
  { name: "iadd", operands: 0, doc: "a b -> a+b (32-bit wrap)" },
  { name: "isub", operands: 0, doc: "a b -> a-b" },
  { name: "imul", operands: 0, doc: "a b -> a*b" },
  { name: "idiv", operands: 0, doc: "a b -> a/b (signed; b = 0 faults the game)" },
  { name: "ilt", operands: 0, doc: "a b -> a<b (signed) ? 1 : 0" },
  { name: "ieq", operands: 0, doc: "a b -> a==b ? 1 : 0" },
  { name: "ileq", operands: 0, doc: "a b -> a<=b (signed) ? 1 : 0" },
  { name: "br", operands: 1, doc: "jump to the label", syntax: "br <label>" },
  { name: "brt", operands: 1, doc: "pop c; jump if c != 0", syntax: "brt <label>" },
  { name: "brf", operands: 1, doc: "pop c; jump if c == 0", syntax: "brf <label>" },
  { name: "iconst", operands: 1, doc: "push a variable handle (0xFFFF0000|index) or a raw value", syntax: "iconst $var | iconst <n>" },
  { name: "iconst0", operands: 0, doc: "push 0 (an unconnected native input)" },
  { name: "iconst1", operands: 0, doc: "push 1" },
  { name: "inc", operands: 0, doc: "top += 1" },
  { name: "dec", operands: 0, doc: "top -= 1" },
  { name: "copy", operands: 2, doc: "copy variable a into b (same type, size bytes)", syntax: "copy $a, $b" },
  { name: "dup", operands: 0, doc: "a -> a a" },
  { name: "call", operands: 2, doc: "subroutine call (unused by vanilla; rejected by the verifier)" },
  {
    name: "ecall",
    operands: 2,
    doc: "call a native: arguments in args[] order (args[0] is pushed last); pushes the result byte (pop it or branch on it)",
    syntax: "ecall name(a0, a1, …) [site s] [argc n]",
  },
  { name: "ret", operands: 0, doc: "return from call (unused by vanilla; rejected by the verifier)" },
  { name: "load", operands: 1, doc: "push a frame slot (unused by vanilla; rejected by the verifier)" },
  { name: "gload", operands: 1, doc: "push a uint32/sint32/float32 (bits)/bool/Time (ms) variable", syntax: "gload $var" },
  { name: "store", operands: 1, doc: "pop into a frame slot (unused by vanilla; rejected by the verifier)" },
  { name: "gstore", operands: 1, doc: "pop into a uint32/sint32/float32/bool/Time (from ms) variable", syntax: "gstore $var" },
  { name: "ltime", operands: 0, doc: "push the ECS time in ms" },
  { name: "timeff", operands: 0, doc: "top: float32 seconds -> ms" },
  { name: "pop", operands: 0, doc: "drop the top" },
  { name: "rvm", operands: 0, doc: "yield: continue at the next instruction in the next simulation step" },
  { name: "dself", operands: 0, doc: "mark the program's entity for destruction (execution continues)" },
  { name: "halt", operands: 0, doc: "end the program (code and shutdown each end in halt)" },
  { name: "word", operands: -1, doc: "raw words (listings of broken code only)", syntax: "word 0x…, 0x…" },
];

export const FRAME_OPS = new Set(["call", "ret", "load", "store"]);

export const DIRECTIVES: Opcode[] = [
  { name: ".kimp", operands: 1, doc: "format line: `.kimp 1`" },
  { name: "program", operands: 1, doc: "the program's GUID, fixed forever; the id defaults to FNV-1a of its 16 bytes (never set it by hand)", syntax: "program <GUID>" },
  { name: "stack", operands: 1, doc: "the header's stackSize (vanilla 256); the real instance stack is 32 slots", syntax: "stack 256" },
  { name: "streams", operands: 1, doc: "event streams the instance allocates (onEvent needs them): OnCollision, OnHit, OnParry, EntityInRange, or none", syntax: "streams none | OnCollision, OnHit" },
  { name: "data_size", operands: 1, doc: "only when the data block must be larger than the variables need", syntax: "data_size <n>" },
  { name: "queries", operands: 1, doc: "queryCallIds: `auto` (default) = the call sites of the query natives", syntax: "queries auto" },
  {
    name: "var",
    operands: -1,
    doc: "a variable: `var $sym <type> [@offset | @$other+n] [size n] [config C] [name 0x…] [dbg \"…\"] [= value]`",
    syntax: "var $name <type> [config C] [= value]",
  },
];

export const SECTIONS = ["code:", "shutdown:"];
export const STREAMS = ["OnCollision", "OnHit", "OnParry", "EntityInRange", "none"];

export class ImpactData {
  readonly natives = new Map<string, Native>(); // by lower-case name and lower-case node name and hash
  readonly nativeList: Native[] = [];
  readonly types = new Map<string, ImpactType>();
  readonly configs = new Map<string, ConfigId>(); // by alias and by id
  readonly opcodes = new Map(OPCODES.map((o) => [o.name, o]));
  readonly directives = new Map(DIRECTIVES.map((o) => [o.name, o]));

  constructor(readonly index: ImpactIndex) {
    for (const n of index.natives) this.addNative(n);
    for (const t of index.types) this.types.set(t.name, t);
    for (const c of index.configs) {
      this.configs.set(c.id.toLowerCase(), c);
      for (const a of c.aliases) if (!this.configs.has(a)) this.configs.set(a, c);
    }
  }

  addNative(n: Native): void {
    if (!this.natives.has(n.name.toLowerCase())) this.nativeList.push(n);
    this.natives.set(n.name.toLowerCase(), n);
    if (n.node) this.natives.set(n.node.toLowerCase(), n);
    if (n.hash) this.natives.set(n.hash.toLowerCase(), n);
  }

  /** A mod's impact/natives.json ({format: 1, natives: [{name, argc, args, ...}]}): returns a copy with them added. */
  withModNatives(file: { natives?: Partial<Native>[] }, origin = "mod"): ImpactData {
    const d = new ImpactData(this.index);
    for (const raw of file.natives ?? []) {
      if (!raw.name) continue;
      d.addNative({
        name: raw.name,
        node: raw.name,
        hash: raw.hash ?? "0x" + fnv1a32(raw.name).toString(16).padStart(8, "0"),
        argc: raw.argc ?? raw.args?.length ?? 0,
        origin,
        args: (raw.args ?? []).map((a) => ({ name: a.name, types: a.types ?? [], dir: a.dir ?? "in", doc: a.doc, raw: a.raw })),
        returns: raw.returns,
        suspends: raw.suspends,
        doc: raw.doc,
      });
    }
    return d;
  }

  native(name: string): Native | undefined {
    return this.natives.get(name.toLowerCase());
  }

  config(token: string): ConfigId | undefined {
    return this.configs.get(token) ?? this.configs.get(token.toLowerCase());
  }
}

/** FNV-1a-32 over UTF-8 (natives, configs, types; `"mymod.Radius"` config ids). */
export function fnv1a32(s: string | Uint8Array): number {
  const bytes = typeof s === "string" ? Buffer.from(s, "utf8") : s;
  let h = 0x811c9dc5;
  for (const b of bytes) {
    h ^= b;
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h >>> 0;
}

export function hex32(n: number): string {
  return "0x" + (n >>> 0).toString(16).padStart(8, "0");
}

/** GUID text -> its 16 bytes as the game stores them (little-endian first three groups, like .NET/Windows). */
export function guidBytes(guid: string): Uint8Array {
  const h = guid.replace(/-/g, "");
  if (!/^[0-9a-fA-F]{32}$/.test(h)) throw new Error(`bad GUID ${guid}`);
  const b = Buffer.from(h, "hex");
  const out = Buffer.from(b);
  out[0] = b[3]; out[1] = b[2]; out[2] = b[1]; out[3] = b[0];
  out[4] = b[5]; out[5] = b[4];
  out[6] = b[7]; out[7] = b[6];
  return out;
}

// ---- line analysis for hover/completion/signature help

export interface Token {
  text: string;
  start: number;
  end: number;
}

/** Strips a `;` comment (outside strings). */
export function codePart(line: string): string {
  let q = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (c === '"' && line[i - 1] !== "\\") q = !q;
    if (c === ";" && !q) return line.slice(0, i);
  }
  return line;
}

/** The word under `col`: $vars, labels, native names with dots, numbers, GUIDs. */
export function wordAt(line: string, col: number): Token | undefined {
  const rx = /\$?[A-Za-z_.][\w.:-]*|0x[0-9a-fA-F]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/g;
  for (const m of line.matchAll(rx)) {
    const s = m.index!;
    const e = s + m[0].length;
    if (col >= s && col <= e) return { text: m[0].replace(/:$/, ""), start: s, end: e - (m[0].endsWith(":") ? 1 : 0) };
  }
  return undefined;
}

export interface EcallContext {
  native: string;
  /** index of the argument the cursor is in */
  arg: number;
  /** column where the native name starts */
  nameStart: number;
}

/** Inside `ecall name(a, b|` -> {native, arg}. */
export function ecallAt(line: string, col: number): EcallContext | undefined {
  const code = codePart(line);
  const m = /^(\s*(?:[A-Za-z_][\w.]*:\s*)?)ecall\s+([^\s(]+)\s*\(/i.exec(code);
  if (!m) return undefined;
  const open = m[0].length - 1;
  if (col <= open) return undefined;
  let depth = 0;
  let arg = 0;
  for (let i = open + 1; i < col && i < code.length; i++) {
    const c = code[i];
    if (c === "(" || c === "[" || c === "{") depth++;
    else if (c === ")" || c === "]" || c === "}") {
      if (depth === 0) return undefined; // past the closing paren
      depth--;
    } else if (c === "," && depth === 0) arg++;
  }
  return { native: m[2], arg, nameStart: code.indexOf(m[2], m[1].length + 5) };
}

export interface KimpSymbols {
  vars: Map<string, { line: number; col: number; type: string; rest: string }>;
  labels: Map<string, { line: number; col: number; section: string }>;
  guid?: string;
}

/** Variables, labels and the program GUID of a .kimp/.kpatch text. */
export function scanSymbols(text: string): KimpSymbols {
  const vars = new Map<string, { line: number; col: number; type: string; rest: string }>();
  const labels = new Map<string, { line: number; col: number; section: string }>();
  let guid: string | undefined;
  let section = "header";
  const lines = text.split(/\r?\n/);
  lines.forEach((raw, i) => {
    const s = codePart(raw);
    const t = s.trim();
    if (!t) return;
    if (t === "code:" || t === "shutdown:") {
      section = t.slice(0, -1);
      return;
    }
    const v = /^(\s*)var\s+\$(\S+)\s+(\S+)(.*)$/.exec(s);
    if (v) {
      vars.set(v[2], { line: i, col: v[1].length + 5, type: v[3], rest: v[4].trim() });
      return;
    }
    const p = /^\s*program\s+([0-9a-fA-F-]{36})/.exec(s);
    if (p) guid = p[1].toLowerCase();
    const l = /^(\s*)([A-Za-z_][\w.]*):/.exec(s);
    if (l && !/^\s*(ecall|word)\b/.test(s)) labels.set(l[2], { line: i, col: l[1].length, section });
  });
  return { vars, labels, guid };
}
