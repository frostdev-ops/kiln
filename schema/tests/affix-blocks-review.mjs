// Affix blocks' structural checks (the "affixes" and "affixPools" sections) agree with Kiln.Blocks' AffixRuntime checks
// where a schema can say it; no game or browser is started. Run: node schema/tests/affix-blocks-review.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";

const require = createRequire(new URL("../../launcher/package.json", import.meta.url));
const Ajv2020 = require("ajv/dist/2020.js");

const read = name => JSON.parse(readFileSync(new URL(`../${name}`, import.meta.url), "utf8"));
const ajv = new Ajv2020({ strict: false, validateFormats: false });
ajv.addSchema(read("common.schema.json"));
ajv.addSchema(read("kiln-vfx.schema.json"));
ajv.addSchema(read("kiln-quests.schema.json"));
const validate = ajv.compile(read("kiln-blocks.schema.json"));
const check = (value, expected) => assert.equal(validate({ format: 1, ...value }), expected, JSON.stringify(validate.errors));

const bear = { name: "of the Bear", stat: "strength", tiers: [{ min: 2, max: 4 }, { min: 5, max: 8, weight: 0.5, minItemLevel: 20 }] };
const fleet = { name: "Fleet", stat: "move-speed", stacks: false, group: "edge", tiers: [{ min: 0.05, max: 0.15, step: 0.05 }] };
const blades = { items: ["Weapon_T1_1H_SwordEpic_01"], affixes: [{ affix: "bear" }, { affix: "fleet", weight: 2 }, { affix: "other-mod:keen" }], min: 1, max: 2 };
check({ affixes: { bear, fleet }, affixPools: { blades } }, true);
check({ affixes: { curse: { name: "Cursed", stat: "strength", tiers: [{ min: -5, max: -1 }] } } }, true);                  // a curse
check({ affixes: { odd: { name: "Odd", stat: "3d1f2a40-5b6c-4d7e-8f90-a1b2c3d4e5f6", tiers: [{ min: 0.1, max: 0.2 }] } } }, true);   // a GUID: another attribute
check({ affixPools: { any: { affixes: [{ affix: "bear" }], min: 0, max: 0 } } }, true);                                         // no items: any item

// names: an affix's own rule, stricter than other blocks'
for (const name of ["Bear", "bad.name", "-bear", "a".repeat(65)]) check({ affixes: { [name]: bear } }, false);
check({ affixPools: { "Blades": blades } }, false);
// fields
check({ affixes: { bear: { ...bear, stat: "strenght" } } }, false);
check({ affixes: { bear: { ...bear, tiers: [] } } }, false);
check({ affixes: { bear: { ...bear, tiers: Array(17).fill({ min: 1, max: 2 }) } } }, false);
check({ affixes: { bear: { ...bear, tiers: [{ min: 0, max: 2 }] } } }, false);
check({ affixes: { bear: { ...bear, tiers: [{ min: 1, max: 2, step: 0 }] } } }, false);
check({ affixes: { bear: { ...bear, tiers: [{ min: 1, max: 2, weight: -1 }] } } }, false);
check({ affixes: { bear: { ...bear, tiers: [{ min: 1, max: 2, wieght: 2 }] } } }, false);
check({ affixes: { bear: { ...bear, name: "" } } }, false);
check({ affixes: { bear: { ...bear, group: "" } } }, false);
const { stat, ...noStat } = bear;
check({ affixes: { bear: noStat } }, false);
check({ affixPools: { blades: { ...blades, affixes: [] } } }, false);
check({ affixPools: { blades: { ...blades, items: [] } } }, false);
check({ affixPools: { blades: { ...blades, max: 9 } } }, false);
check({ affixPools: { blades: { ...blades, affixes: [{ affix: "" }] } } }, false);
check({ affixPools: { blades: { ...blades, affixes: [{ weight: 1 }] } } }, false);
check({ affixPools: { blades: { ...blades, pools: 1 } } }, false);
// what the schema cannot say and Kiln.Blocks refuses at load: max below min, a pool that can never give its minimum,
// a range across 0 or with max below min, a fraction for a whole-number stat, an entry no affix answers
check({ affixPools: { blades: { ...blades, min: 2, max: 1 } } }, true);
check({ affixes: { bear: { ...bear, tiers: [{ min: -1, max: 2 }] } } }, true);
console.log("affix blocks schema checks passed");
