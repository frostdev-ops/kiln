// The save editor's commands in the browser mock (api/mock.ts registers them). The saves are synthetic: built here, in
// memory, from a few item, skill and knowledge ids of the editor's own tables (src/editor/data), never read from a real
// save or from the game. Four characters: Aldric (worn gear, a full inventory and backpack, skills, 2,000 knowledge
// entries), Brienne (a new character: two worn pieces), Mordred (a stack of an item no table knows: read-only) and
// Cedric (a character block the editor cannot read). Writing, the editor's backups (one per write, as
// kiln_manage::saves::editor::write_save makes) and restoring work on the memory copies.
//   ?editor=none     no saves (the editor's empty state)
//   ?editor=fixture  the local fixture instead (launcher/assets-local/fixtures/editor/: copies of your own saves, never
//                    committed; the development fallback this mock had before)
import { Buffer } from "buffer";
import { createXXHash3 } from "hash-wasm";
import type { EditorBackup, KilnError } from "./commands";

type Handler = (args: Record<string, unknown>) => unknown;
type Reject = (code: KilnError["code"], message: string) => never;

// ---------------------------------------------------------------- the save format (shared/sv2c-web.mjs, character.mjs)

interface Entry { slot: number; itemId: number; count: number; level?: number; durability?: number; maxDurability?: number;
  liquid?: number; maxLiquid?: number; maxUpgrades?: number; upgrades?: number; socket?: string }

function entry(e: Entry): Buffer {
  let flags = 0;
  if (e.level !== undefined) flags |= 0x01;
  if (e.durability !== undefined) flags |= 0x02;
  if (e.liquid !== undefined) flags |= 0x04;
  if (e.maxUpgrades !== undefined) flags |= 0x10;
  if (e.socket !== undefined) flags |= 0x20;
  const parts: Buffer[] = [];
  const h = Buffer.alloc(8);
  h[0] = e.slot; h.writeUInt32LE(e.itemId >>> 0, 1); h.writeUInt16LE(e.count, 5); h[7] = flags;
  parts.push(h);
  if (flags & 0x01) parts.push(Buffer.from([e.level!]));
  if (flags & 0x02) { const b = Buffer.alloc(8); b.writeUInt32LE(e.durability!, 0); b.writeUInt32LE(e.maxDurability!, 4); parts.push(b); }
  if (flags & 0x04) { const b = Buffer.alloc(8); b.writeUInt32LE(e.liquid!, 0); b.writeUInt32LE(e.maxLiquid!, 4); parts.push(b); }
  if (flags & 0x10) parts.push(Buffer.from([e.maxUpgrades!, e.upgrades!]));
  if (flags & 0x20) parts.push(Buffer.from(e.socket!, "hex"));
  return Buffer.concat(parts);
}

interface Character {
  name: string; level: number; xp: number; skills: [number, number][];
  /** Worn pieces, by their bit in the equipment mask (0-4 appearance, 5-9 armour, 10-15 rings, shield, hook, glider, backpack). */
  worn: [number, Omit<Entry, "slot">][];
  main: Entry[]; extra: Entry[]; knowledge: [number, number][];
}

/** The player_character block: the layout parseCharacter reads (opaque parts are zeros; 11 free equipment places). */
function characterBlock(c: Character): Buffer {
  const top = Buffer.alloc(12);
  top.writeUInt32LE(c.level, 0); top.writeUInt32LE(c.xp, 4); top.writeUInt32LE(c.skills.length, 8);
  const skills = Buffer.alloc(5 * c.skills.length);
  c.skills.forEach(([id, lv], i) => { skills.writeUInt32LE(id >>> 0, 5 * i); skills[5 * i + 4] = lv; });
  const worn = [...c.worn].sort((a, b) => a[0] - b[0]);
  // two more lit bits than worn pieces: the weapons, kept elsewhere in the block (as in real saves)
  const mask = worn.reduce((m, [bit]) => m | (1 << bit), 0) | (1 << 16) | (1 << 17);
  const mid = Buffer.alloc(16);
  mid.writeUInt32LE(mask >>> 0, 12);
  const between = Buffer.from([11, 0, 0, 0, 0, 0, 0x05, c.main.length, c.extra.length ? 1 : 0, c.extra.length]);
  return Buffer.concat([
    Buffer.alloc(0x3a), top, skills, mid,
    ...worn.map(([, e], i) => entry({ ...e, slot: i })), between,
    ...[...c.main].sort((a, b) => a.slot - b.slot).map(entry), ...c.extra.map(entry),
    Buffer.alloc(24),
  ]);
}

function characterData(name: string): Buffer {
  const n = Buffer.from(name, "utf8");
  const b = Buffer.alloc(9 + n.length + 8);
  b.writeUInt32LE(n.length, 5);
  n.copy(b, 9);
  return b;
}

function knowledgeBlock(entries: [number, number][]): Buffer {
  const p = Buffer.alloc(8 + 8 * entries.length);
  p.writeUInt32LE(1, 0); p.writeUInt32LE(entries.length, 4);
  entries.forEach(([id, v], i) => { p.writeUInt32LE(id >>> 0, 8 + 4 * i); p.writeUInt32LE(v >>> 0, 8 + 4 * entries.length + 4 * i); });
  return p;
}

let xxh3: Awaited<ReturnType<typeof createXXHash3>> | null = null;
const hash32 = (b: Buffer) => { xxh3!.init(); xxh3!.update(b); return parseInt(xxh3!.digest("hex").slice(8), 16) >>> 0; };

/** An SV2C container of uncompressed blocks (the editor reads those without zstd), as shared/sv2c-web.mjs build(). */
export async function sv2cFile(blocks: [string, Buffer][]): Promise<Uint8Array> {
  xxh3 ??= await createXXHash3();
  const header = Buffer.alloc(0x30);
  header.write("SV2C", 0, "latin1");
  const parts = [header];
  let pos = header.length;
  const entries: { off: number; size: number; hash: number; name: Buffer }[] = [];
  for (const [name, payload] of blocks) {
    const head = Buffer.alloc(14);
    head.write("S2BH", 0, "latin1"); // byte 9 = 0: not compressed
    const raw = Buffer.concat([head, payload]);
    entries.push({ off: pos, size: raw.length, hash: hash32(raw), name: Buffer.from(name, "utf8") });
    parts.push(raw);
    pos += raw.length;
  }
  const pad = (8 - (pos % 8)) % 8;
  parts.push(Buffer.alloc(pad));
  pos += pad;
  const dir = Buffer.alloc(20 * entries.length);
  let namePos = 0;
  entries.forEach((e, i) => {
    dir.writeUInt32LE(20 * (entries.length - i) + namePos, 20 * i);
    dir.writeUInt32LE(e.name.length, 20 * i + 4);
    dir.writeUInt32LE(e.off, 20 * i + 8);
    dir.writeUInt32LE(e.size, 20 * i + 12);
    dir.writeUInt32LE(e.hash, 20 * i + 16);
    namePos += e.name.length;
  });
  const out = Buffer.concat([...parts, dir, ...entries.map((e) => e.name)]);
  out.writeUInt32LE(out.length, 0x08);
  out.writeUInt32LE(pos - 40, 0x28);
  out.writeUInt32LE(entries.length, 0x2c);
  return new Uint8Array(out);
}

// ---------------------------------------------------------------- the characters (ids from src/editor/data)

const ITEM = {
  bullHelmet: 63209182, warlockShirt: 3372205, stalkerGloves: 27519013, ashwindPants: 9501187, mercBoots: 30416182,
  ringOfHealth: 72782423, hook: 1438617272, glider3: 284247111, glider1: 1028290781, largeBackpack: 996296787,
  tinyBackpack: 531058391, eclipsedBlade: 66688434, wolfsHydra: 544559006, hailscourge: 558617602, heroShield: 40007242,
  gemBattleMania: 100480458, gemFocusedLight: 63026718, diamond: 61577244, mintMeat: 90588998, metalScraps: 132545371,
  ectoplasm: 148404293, ironBars: 159114158, wetBoot: 166917849, manaPotion: 3087876, grilledGame: 16972502,
  waterPouch: 3833797336, steelArrow: 103008069, fireball: 83283491,
};
/** An item id no table has (a stack of a Kiln mod that is gone). */
export const UNKNOWN_ITEM = 0x7f00abcd;
const socket = (gem: number, level: number) => Buffer.from([gem & 0xff, (gem >>> 8) & 0xff, (gem >>> 16) & 0xff, (gem >>> 24) & 0xff, level]).toString("hex");
const armour = (itemId: number, level: number, rarity: number, upgrades = 0) => ({ itemId, count: 1, level: level + upgrades, maxUpgrades: rarity, upgrades });

function aldric(know: string[]): Character {
  return {
    name: "Aldric", level: 25, xp: 1200,
    // connected to the root (Spark): Spirit, Giant Slayer Hook, Mage Apprentice 2/3, Intelligence, Bone to Ash
    skills: [[31613866, 1], [29426941, 1], [3166875476, 2], [2378964554, 1], [1407590399, 1]],
    worn: [
      [5, armour(ITEM.bullHelmet, 30, 3, 1)], [6, armour(ITEM.warlockShirt, 20, 3)], [7, armour(ITEM.stalkerGloves, 40, 3)],
      [8, armour(ITEM.ashwindPants, 45, 3, 2)], [9, armour(ITEM.mercBoots, 15, 3)], [10, armour(ITEM.ringOfHealth, 40, 2)],
      [13, { itemId: ITEM.hook, count: 1 }], [14, { itemId: ITEM.glider3, count: 1 }], [15, { itemId: ITEM.largeBackpack, count: 1 }],
    ],
    main: [
      { slot: 0, itemId: ITEM.diamond, count: 120 },
      { slot: 1, itemId: ITEM.metalScraps, count: 250 },
      { slot: 2, itemId: ITEM.ironBars, count: 75 },
      { slot: 3, itemId: ITEM.manaPotion, count: 12 },
      { slot: 4, itemId: ITEM.waterPouch, count: 1, liquid: 640, maxLiquid: 1280 },
      { slot: 5, itemId: ITEM.steelArrow, count: 300 },
      { slot: 6, itemId: ITEM.gemFocusedLight, count: 1, level: 40 },
      { slot: 7, itemId: ITEM.heroShield, count: 1, level: 10, durability: 60, maxDurability: 100, maxUpgrades: 3, upgrades: 0 },
      { slot: 8, itemId: ITEM.wetBoot, count: 3 },
      { slot: 9, itemId: ITEM.ectoplasm, count: 40 },
      { slot: 16, itemId: ITEM.fireball, count: 1 },
      { slot: 24, itemId: ITEM.eclipsedBlade, count: 1, level: 32, durability: 220, maxDurability: 300, maxUpgrades: 4, upgrades: 2, socket: socket(ITEM.gemBattleMania, 30) },
      { slot: 25, itemId: ITEM.hailscourge, count: 1, level: 20, durability: 300, maxDurability: 300, maxUpgrades: 4, upgrades: 0, socket: "0000000000" },
      { slot: 26, itemId: ITEM.grilledGame, count: 8 },
    ],
    extra: [
      { slot: 0, itemId: ITEM.mintMeat, count: 60 },
      { slot: 1, itemId: ITEM.diamond, count: 250 },
      { slot: 5, itemId: ITEM.wolfsHydra, count: 1, level: 18, durability: 150, maxDurability: 300, maxUpgrades: 4, upgrades: 1, socket: "0000000000" },
    ],
    // every fourth known entry (2,000 of them), and three ids no table names
    knowledge: [...know.filter((_, i) => i % 4 === 0).slice(0, 1997).map((id, i) => [Number(id), i % 3 === 0 ? 3 : 7] as [number, number]),
      [999990001, 1], [999990002, 1], [999990003, 1]],
  };
}

function brienne(know: string[]): Character {
  return {
    name: "Brienne", level: 3, xp: 40, skills: [],
    worn: [[14, { itemId: ITEM.glider1, count: 1 }], [15, { itemId: ITEM.tinyBackpack, count: 1 }]],
    main: [{ slot: 0, itemId: ITEM.metalScraps, count: 5 }, { slot: 1, itemId: ITEM.wetBoot, count: 1 }],
    extra: [],
    knowledge: know.slice(0, 40).map((id) => [Number(id), 1]),
  };
}

function mordred(know: string[]): Character {
  const c = aldric(know);
  return { ...c, name: "Mordred", level: 20, main: [...c.main.filter((e) => e.slot !== 9), { slot: 9, itemId: UNKNOWN_ITEM, count: 12 }], knowledge: c.knowledge.slice(0, 200) };
}

// ---------------------------------------------------------------- the commands

const DIR = "/home/player/.local/share/Steam/userdata/10000001/1203620/remote";
const BACKUPS = "/home/player/.local/share/kiln/backups/editor";
interface Stored { bytes: Uint8Array; modified: number }

export function editorMockHandlers(reject: Reject, gameRunning: () => boolean): Record<string, Handler> {
  const mode = typeof location !== "undefined" ? new URLSearchParams(location.search).get("editor") : null;
  const files = new Map<string, Stored>();
  const backups = new Map<string, { kind: string; label: string; path: string; modified: number; bytes: Uint8Array }[]>();
  let ready: Promise<void> | null = null;
  const prepare = () => (ready ??= (async () => {
    if (mode === "none" || mode === "fixture") return;
    const know = Object.keys((await import("../editor/data/conhecimento.json")).default);
    const t0 = Date.now() - 3 * 3600_000;
    const make = async (file: string, c: Character | null, ago: number, broken?: string) => {
      const blocks: [string, Buffer][] = c
        ? [["character_data", characterData(c.name)], ["player_character", characterBlock(c)], ["player_knowledge", knowledgeBlock(c.knowledge)]]
        : [["character_data", characterData(broken!)], ["player_character", Buffer.alloc(0x60)], ["player_knowledge", knowledgeBlock([])]];
      const bytes = await sv2cFile(blocks);
      const path = `${DIR}/${file}`;
      files.set(path, { bytes, modified: t0 - ago });
      // the game's own rolling copies (remote/backup/<file>.backup-N)
      backups.set(path, [1, 2].map((n) => ({ kind: "game", label: `${file}.backup-${n}`, path: `${DIR}/backup/${file}.backup-${n}`, modified: t0 - ago - n * 600_000, bytes })));
    };
    await make("character0.ksd", aldric(know), 0);
    await make("character1.ksd", brienne(know), 3600_000);
    await make("character2.ksd", mordred(know), 7200_000);
    await make("character3.ksd", null, 9000_000, "Cedric");
  })());
  const fixture = () => fetch(FIXTURE + "index.json").then((r) => (r.ok ? r.json() : null)).catch(() => null) as Promise<{ saves: { path: string; file: string; modified: number }[]; blocks: Record<string, string> } | null>;
  const file = (path: unknown) => files.get(String(path)) ?? reject("not_found", "No such save.");
  const backupNow = (path: string) => {
    const f = file(path);
    const at = Date.now();
    const dir = `${BACKUPS}/tauri-${Math.floor(at / 1000)}-${(backups.get(path)?.length ?? 0) + 1}`;
    backups.set(path, [{ kind: "folder", label: dir.slice(dir.indexOf("backups/") + 8), path: `${dir}/${path.split("/").pop()}`, modified: at, bytes: f.bytes }, ...(backups.get(path) ?? [])]);
    return dir;
  };
  const sv2c = (b: Uint8Array) => b.length >= 0x30 && String.fromCharCode(...b.slice(0, 4)) === "SV2C";
  return {
    editor_list_saves: async () => {
      if (mode === "fixture") return (await fixture())?.saves ?? [];
      await prepare();
      return [...files].map(([path, f]) => ({ path, file: path.split("/").pop()!, modified: f.modified }));
    },
    editor_file_info: async (a) => {
      if (mode === "fixture") return { modified: Date.now() };
      await prepare();
      return { modified: file(a.path).modified };
    },
    editor_read_file: async (a) => {
      if (mode === "fixture") {
        const r = await fetch(FIXTURE + String(a.path).split("/").pop());
        if (!r.ok) reject("not_found", "No such save.");
        return r.arrayBuffer();
      }
      await prepare();
      const b = file(a.path).bytes;
      return b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength);
    },
    editor_zstd_decompress: async (a) => {
      // the synthetic saves are not compressed; the local fixture answers with its own blocks
      const d = a.data as number[];
      const key = `${d.length}:${d.slice(0, 24).map((b) => b.toString(16).padStart(2, "0")).join("")}`;
      const b64 = (await fixture())?.blocks[key];
      if (!b64) reject("unsupported", "The browser mock can only read the fixture's own blocks.");
      return Uint8Array.from(atob(b64!), (c) => c.charCodeAt(0)).buffer;
    },
    editor_game_running: () => gameRunning(),
    editor_backup_file: (a) => backupNow(String(a.path)),
    editor_write_save: (a) => {
      if (gameRunning()) reject("game_running", "quit Enshrouded before saving");
      const data = Uint8Array.from(a.data as number[]);
      if (!sv2c(data)) reject("invalid", "not a save (SV2C header missing)");
      backupNow(String(a.path));
      files.set(String(a.path), { bytes: data, modified: Date.now() });
      return null;
    },
    editor_list_backups: (a): EditorBackup[] => (backups.get(String(a.target)) ?? []).map(({ bytes: _b, ...b }) => b),
    editor_restore_backup: (a) => {
      if (gameRunning()) reject("game_running", "quit Enshrouded before restoring");
      const b = (backups.get(String(a.target)) ?? []).find((x) => x.path === a.src) ?? reject("not_found", "backup not found");
      backupNow(String(a.target));
      files.set(String(a.target), { bytes: b.bytes, modified: Date.now() });
      return null;
    },
    editor_open_backups: () => null,
  };
}

const FIXTURE = "/assets-local/fixtures/editor/";
