// kiln#155: [Range(0, 1, 0.1, SoftMax = true)]: the slider spans 0..1, the number box and checkValue take 2 or 3.
import { expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import Ajv2020 from "ajv/dist/2020";
import addFormats from "ajv-formats";
import type { Json, SchemaSnapshot, SnapshotEntry } from "../src/api/shared";
import { SchemaForm, checkValue } from "../src/ui/SchemaForm";
import common from "../../schema/common.schema.json";
import configSchema from "../../schema/config-schema.schema.json";

const entry = (extra = {}): SnapshotEntry => ({ path: "cost", type: "float", comment: null, default: 1, requiresRestart: false, synced: false, min: 0, max: 1, step: 0.1, ...extra });
const schema = (e: SnapshotEntry) => ({ format: 1, source: "kiln", file: "soft.json", id: "soft", mod: "soft", sections: [], entries: [e] }) as SchemaSnapshot;
const render = (e: SnapshotEntry, value: Json) => renderToStaticMarkup(<SchemaForm schema={schema(e)} values={{ cost: value }} onChange={() => { throw new Error("render must not commit"); }} />);

test("a value past a soft max shows as it is in the number box, the slider spans min..max with its bar full", () => {
  const html = render(entry({ softMax: true }), 3);
  expect(html).toContain('type="range"');
  expect(html).toContain('min="0" max="1" step="0.1"');
  expect(html).toContain("background-size:100% 100%");
  expect(html).toContain('value="3"');
  expect(html).not.toContain("invalid");
  // the number box keeps the hard min only
  const box = html.slice(html.indexOf("k-num"));
  expect(box).toContain('min="0"');
  expect(box).not.toContain('max="1"');
  // a hard max: the same value is marked, as before
  expect(render(entry(), 3)).toContain("invalid");
});

test("a value past a soft min shows as it is, the bar empty", () => {
  const html = render(entry({ softMin: true, type: "int", min: 0, max: 10, step: 1 }), -4);
  expect(html).toContain('value="-4"');
  expect(html).toContain("background-size:0% 100%");
  expect(html).not.toContain("invalid");
});

test("checkValue lets a soft side through and keeps the hard one", () => {
  expect(checkValue(entry({ softMax: true }), 3)).toBeNull();
  expect(checkValue(entry({ softMax: true }), -1)).not.toBeNull();
  expect(checkValue(entry({ softMin: true }), -1)).toBeNull();
  expect(checkValue(entry({ softMin: true }), 2)).not.toBeNull();
  expect(checkValue(entry({ softMin: true, softMax: true }), 1e9)).toBeNull();
  expect(checkValue(entry(), 3)).not.toBeNull();
  expect(checkValue(entry({ softMax: true, type: "int" }), 2.5)).not.toBeNull();   // still whole numbers
});

test("the format-1 config schema takes optional boolean softMin/softMax", () => {
  const ajv = new Ajv2020({ strict: false });
  addFormats(ajv);
  ajv.addSchema(common);
  const validate = ajv.compile(configSchema);
  expect(validate(schema(entry({ softMax: true })))).toBe(true);
  expect(validate(schema(entry({ softMin: true, softMax: false })))).toBe(true);
  for (const soft of ["yes", 1, null]) expect(validate(schema(entry({ softMax: soft })))).toBe(false);
});
