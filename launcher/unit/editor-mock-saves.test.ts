// The browser mock's synthetic character saves (src/api/mockEditor.ts) read back through the editor's own parsers
// (shared/sv2c-web.mjs, shared/character.mjs, shared/knowledge.mjs), and a rebuild of each character block is
// byte-identical: the Playwright specs of the editor and the Deck edit real save bytes, not a stand-in.
import { describe, expect, test } from "bun:test";
import { editorMockHandlers, UNKNOWN_ITEM } from "../src/api/mockEditor";
// @ts-ignore: the editor is plain JavaScript
import * as sv2c from "../src/editor/shared/sv2c-web.mjs";
// @ts-ignore
import { parseCharacter, buildCharacter } from "../src/editor/shared/character.mjs";
// @ts-ignore
import * as know from "../src/editor/shared/knowledge.mjs";
import items from "../src/editor/data/items.json";
import skills from "../src/editor/data/skills.json";

const reject = (code: string, message: string): never => { throw Object.assign(new Error(message), { code }); };
const isItem = (id: number) => Object.hasOwn(items, id);
const isSkill = (id: number) => Object.hasOwn(skills.nodes, id);
const WORN = new Set(["Armour_Head", "Armour_UpperBody", "Armour_Arms", "Armour_LowerBody", "Armour_Feet", "Armour_Ring", "Shield", "HookShot", "Glider", "Backpack"]);
const wearable = (id: number) => WORN.has((items as Record<string, { slot?: string }>)[id]?.slot ?? "");

async function saves(running = false) {
  const h = editorMockHandlers(reject as never, () => running);
  const list = (await h.editor_list_saves({})) as { path: string; file: string }[];
  const read = async (path: string) => {
    const bytes = new Uint8Array((await h.editor_read_file({ path })) as ArrayBuffer);
    await sv2c.init();
    return sv2c.parse(bytes, () => { throw new Error("compressed"); }, ["character_data", "player_character", "player_knowledge"]);
  };
  return { h, list, read };
}

describe("synthetic editor saves", () => {
  test("four characters: three readable, Mordred only with unknown ids accepted, Cedric not at all", async () => {
    const { list, read } = await saves();
    expect(list.map((s) => s.file)).toEqual(["character0.ksd", "character1.ksd", "character2.ksd", "character3.ksd"]);
    const names = [];
    for (const s of list) {
      const save = await read(s.path);
      names.push(sv2c.characterName(save));
    }
    expect(names).toEqual(["Aldric", "Brienne", "Mordred", "Cedric"]);

    const block = async (i: number) => (await read(list[i].path)).blocks.find((b: { name: string }) => b.name === "player_character").payload;
    const aldric = parseCharacter(await block(0), isItem, isSkill, wearable);
    expect([aldric.level, aldric.xp, aldric.skills.length, aldric.equipment.length, aldric.main.length, aldric.extra.length]).toEqual([25, 1200, 5, 9, 14, 3]);
    // the worn pieces sit in the mask's lit bits (head ... backpack), the weapons' two bits past them
    expect(aldric._equipMask).toBe((0b11100111111 << 5 | 3 << 16) >>> 0);
    expect(aldric.main.find((e: { slot: number }) => e.slot === 24)).toMatchObject({ level: 32, upgrades: 2, maxUpgrades: 4, socket: "ca35fd051e" });
    // a rebuild of what was read is the same bytes
    expect(Buffer.compare(buildCharacter(aldric), await block(0))).toBe(0);

    const brienne = parseCharacter(await block(1), isItem, isSkill, wearable);
    expect(brienne.equipment.length).toBe(2); // a new character: fewer than four worn pieces
    expect(Buffer.compare(buildCharacter(brienne), await block(1))).toBe(0);

    const mordredBlock = await block(2);
    expect(() => parseCharacter(mordredBlock, isItem, isSkill, wearable)).toThrow();
    const mordred = parseCharacter(mordredBlock, (id: number) => id > 0xffff, isSkill, (id: number) => !isItem(id) || wearable(id));
    expect(mordred.main.some((e: { itemId: number }) => e.itemId === UNKNOWN_ITEM)).toBe(true);
    const cedric = await block(3);
    expect(() => parseCharacter(cedric, isItem, isSkill, wearable)).toThrow();

    const k = know.parseBlock((await read(list[0].path)).blocks.find((b: { name: string }) => b.name === "player_knowledge").payload);
    expect(k.entries.length).toBe(2000);
  });

  test("a write keeps a backup of the save before it; restore puts a backup back and keeps the one it replaced", async () => {
    const { h, list } = await saves();
    const path = list[0].path;
    const before = new Uint8Array((await h.editor_read_file({ path })) as ArrayBuffer);
    expect(((await h.editor_list_backups({ target: path })) as { kind: string }[]).map((b) => b.kind)).toEqual(["game", "game"]);
    const changed = Uint8Array.from(before);
    changed[0x20] ^= 1;
    await h.editor_write_save({ path, data: Array.from(changed) });
    expect(new Uint8Array((await h.editor_read_file({ path })) as ArrayBuffer)).toEqual(changed);
    const after = (await h.editor_list_backups({ target: path })) as { kind: string; path: string }[];
    expect(after.map((b) => b.kind)).toEqual(["folder", "game", "game"]);
    await h.editor_restore_backup({ src: after[0].path, target: path });
    expect(new Uint8Array((await h.editor_read_file({ path })) as ArrayBuffer)).toEqual(before);
    expect(((await h.editor_list_backups({ target: path })) as unknown[]).length).toBe(4);
    expect(() => h.editor_write_save({ path, data: [1, 2, 3] })).toThrow("SV2C");
  });

  test("the game running refuses writes and restores", async () => {
    const { h, list } = await saves(true);
    expect(() => h.editor_write_save({ path: list[0].path, data: [] })).toThrow("quit Enshrouded");
    expect(() => h.editor_restore_backup({ src: "x", target: list[0].path })).toThrow("quit Enshrouded");
  });
});
