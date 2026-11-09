// UI blocks' structural checks agree with the compiler; no game or browser is started.
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
const row = { type: "text", text: "{game.fps:0}" };
const window = { title: { en: "Settings", de: "Einstellungen" }, rows: [row] };
const check = (value, expected) => assert.equal(validate({ format: 1, ...value }), expected, JSON.stringify(validate.errors));
check({ windows: { settings: window }, hudLines: { fps: { text: "{game.fps:0}", anchor: "topRight" } }, keyBindings: { open: { key: "F8", does: { openWindow: "settings" } } } }, true);
check({ windows: { settings: { ...window, kind: "screen", dim: true, interactive: true, autoResize: false } } }, true);
check({ windows: { settings: { ...window, kind: "window", position: [0.5, 0.5], alpha: 0.8 } } }, true);
for (const field of [{ position: [0.5, 0.5] }, { alpha: 0.5 }, { autoResize: true }, { interactive: false }])
    check({ windows: { settings: { ...window, kind: "screen", ...field } } }, false);
check({ windows: { settings: { ...window, dim: true } } }, false);
check({ windows: { settings: { ...window, rows: [{ ...row, wrap: true, color: "red" }] } } }, false);
check({ windows: { settings: { ...window, rows: [{ ...row, wrap: false, color: "red" }] } } }, true);
check({ windows: { settings: { ...window, rows: [{ type: "dial" }] } } }, false);
check({ hudLines: { fps: { text: "x", refresh: 0 } } }, false);
check({ keyBindings: { open: { key: "F8", does: { command: "x", toast: "y" } } } }, false);
console.log("UI blocks schema checks passed");
