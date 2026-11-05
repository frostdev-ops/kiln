// SPDX-License-Identifier: MIT
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { Catalog } from "../../src/core/catalog";
import { budgetLines, describeGameVfx, findGameVfx, overBudget, parameterSnippet, readVfxNeed, vfxGameName, vfxNameAt, vfxPlace, VFX_LIMITS } from "../../src/core/vfx";

const REPO = path.resolve(__dirname, "..", "..", "..", "..", "..");

suite("vfx", () => {
  const catalog = () => Catalog.load(path.join(REPO, "crates", "kiln-manage", "src", "catalog", "testdata", "client.json"));

  test("which places in a blocks file name a VFX", () => {
    assert.deepEqual(vfxPlace(["effects", "boom", "vfx"], false), { kind: "reference", holder: ["effects", "boom"] });
    assert.deepEqual(vfxPlace(["effects", "boom", "vfx", "name"], false), { kind: "reference", holder: ["effects", "boom"] });
    assert.deepEqual(vfxPlace(["spells", "s", "delivery", "onImpact", 0, "then", 1, "vfx"], false), { kind: "reference", holder: ["spells", "s", "delivery", "onImpact", 0, "then", 1] }, "any depth");
    assert.deepEqual(vfxPlace(["effects", "boom", "vfx", "parameters", "radius"], true), { kind: "parameter", holder: ["effects", "boom"] });
    assert.deepEqual(vfxPlace(["vfx", "burst"], true), { kind: "block", name: "burst" });
    assert.equal(vfxPlace(["vfx", "burst", "sound"], false), undefined, "inside a definition is not a reference");
    assert.equal(vfxPlace(["vfx", "name"], false), undefined);
    assert.equal(vfxPlace(["vfx"], true), undefined);
    assert.equal(vfxPlace(["effects", "boom", "sound"], false), undefined);
  });
  test("what is the game's", () => {
    assert.equal(vfxGameName("game:Explosion_X"), "Explosion_X");
    assert.equal(vfxGameName("000003ea-7e57-4a11-9c00-0000000003ea"), "000003ea-7e57-4a11-9c00-0000000003ea");
    assert.equal(vfxGameName("frost_burst"), undefined);
    assert.equal(vfxGameName("other-mod:frost"), undefined);
  });
  test("the name of a vfx field, in both forms", () => {
    const t = '{ "effects": { "a": { "vfx": "x" }, "b": { "vfx": { "name": "y", "parameters": { "r": 1 } } } } } // a comment';
    assert.equal(vfxNameAt(t, ["effects", "a"]), "x");
    assert.equal(vfxNameAt(t, ["effects", "b"]), "y");
    assert.equal(vfxNameAt(t, ["effects", "c"]), undefined);
  });
  test("a game VFX by any name that finds it, or its GUID", () => {
    const c = catalog();
    assert.equal(findGameVfx(c, "Explosion_Test_Frost")?.guid, "000003e9-7e57-4a11-9c00-0000000003e9");
    assert.equal(findGameVfx(c, "Attack_Test_Frostling_Slash")?.name, "Explosion_Test_Frost", "names");
    assert.equal(findGameVfx(c, "000003EA-7E57-4A11-9C00-0000000003EA")?.name, "Prop_Test_Crate_DestroyOnLoot");
    assert.equal(findGameVfx(c, "Nope"), undefined);
  });
  test("a description says what finds it and what it takes", () => {
    const c = catalog();
    const text = describeGameVfx(findGameVfx(c, "Explosion_Test_Frost")!).join("\n");
    assert.match(text, /runs 10 s/);
    assert.match(text, /`game:<name>` finds it/);
    assert.match(text, /`radius` float = 2/);
    assert.match(text, /and 1 without a known name/);
    assert.match(text, /Attack_Test_Frostling_Slash/);
    assert.match(describeGameVfx(findGameVfx(c, "Prop_Test_Crate_DestroyOnLoot")!).join("\n"), /use `game:<guid>`/);
  });
  test("what a block can say for a parameter", () => {
    assert.equal(parameterSnippet({ type: "float", default: 2 }), "${1:2}");
    assert.equal(parameterSnippet({ type: "int" }), '{ "int": ${1:1} }');
    assert.equal(parameterSnippet({ type: "float3", default: [0, 1, 2] }), "[${1:0}, ${2:1}, ${3:2}]");
    assert.equal(parameterSnippet({ type: "color", default: "#7fd4ffff" }), '"${1:#7fd4ff}"');
    assert.equal(parameterSnippet({ type: "bool" }), undefined);
  });
  test("the budget counts blocks, and shared things once", () => {
    const need = readVfxNeed([
      '{ "vfx": { "a": { "particles": [ { "burst": 1 }, { "burst": 1, "mesh": "quad-xz" } ], "light": {}, "sound": "s" } } }',
      '{ "vfx": { "b": { "particles": [ { "rate": 1 } ], "sound": "s" }, "c": { "light": {} } } } // comment',
      "{ not json",
    ]);
    assert.equal(need.blocks.size, 3);
    assert.equal(need.blocks.get("a")?.entries, 5);
    assert.equal(need.blocks.get("c")?.entries, 1);
    assert.deepEqual([...need.meshes].sort(), ["quad", "quad-xz"]);
    assert.deepEqual([...need.sounds], ["s"]);
    const lines = budgetLines(need).join("\n");
    assert.match(lines, /VFX: 3 of the 473 left/);
    assert.match(lines, /Meshes: 2 of 386/);
    assert.match(lines, /Mesh memory: 280 of 241,844 bytes/);
    assert.match(lines, /Sounds in VFX: 1 of 10/);
    assert.match(budgetLines(need, 1600).join("\n"), /of the 448 left/, "the catalog's count");
  });
  test("over the budget, at each limit", () => {
    const blocks = (n: number) => `{ "vfx": { ${Array.from({ length: n }, (_, i) => `"b${i}": { "light": {} }`).join(", ")} } }`;
    assert.equal(overBudget(readVfxNeed([blocks(473)])), false);
    assert.equal(overBudget(readVfxNeed([blocks(474)])), true);
    const sounds = (n: number) => `{ "vfx": { ${Array.from({ length: n }, (_, i) => `"b${i}": { "light": {}, "sound": "s${i}" }`).join(", ")} } }`;
    assert.equal(overBudget(readVfxNeed([sounds(10)])), false);
    assert.equal(overBudget(readVfxNeed([sounds(11)])), true);
  });
  test("the limits are the builder's", function () {
    const h = path.join(REPO, "native", "services", "vfx", "cook", "vfxcook.h");
    if (!fs.existsSync(h)) this.skip();
    const t = fs.readFileSync(h, "utf8");
    const l = VFX_LIMITS;
    assert.ok(t.includes(`uint32_t vfx = ${l.vfx.game}, meshes = ${l.meshes.game}, curves = 8, sounds = ${l.sounds.game};`));
    assert.ok(t.includes(`uint64_t mesh_bytes = ${l.meshBytes.game};`));
    assert.ok(t.includes(`uint32_t vfx = ${l.vfx.capacity}, meshes = ${l.meshes.capacity}, curves = 16, sounds = ${l.sounds.capacity};`));
    assert.ok(t.includes(`uint64_t mesh_bytes = 0x${l.meshBytes.capacity.toString(16)};`));
    assert.ok(t.includes(`uint64_t mesh_margin = ${l.meshBytes.margin};`));
  });
});
