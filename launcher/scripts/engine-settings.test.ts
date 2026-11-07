// Pure adapter regressions can run without a listening dev server: bun test scripts/engine-settings.test.ts.
import { expect, test } from "bun:test";
import { settingsGroups, topLevelChange } from "../src/components/EngineSettings";

test("numeric ShroudForge options keep numeric controls and values", () => {
  const groups = settingsGroups({ kind: "shroudforge", id: "test", format: "extended.mod.json", file: null,
    schema: { properties: { choice: { type: "integer", enum: ["1", "2"] } } }, values: { choice: 1 } });
  expect(groups[0].schema.entries[0].type).toBe("int");
  expect(groups[0].schema.entries[0].default).toBe(1);
  expect(topLevelChange({ choice: 1 }, "choice", 2)).toEqual({ key: "choice", value: 2 });
});

test("nested engine setting keys cannot mutate Object.prototype", () => {
  const values = { group: { keep: 7 } };
  const changed = topLevelChange(values, "group.__proto__.engineReviewPolluted", true);
  expect((Object.prototype as Record<string, unknown>).engineReviewPolluted).toBeUndefined();
  expect(values).toEqual({ group: { keep: 7 } });
  expect(changed.key).toBe("group");
  expect(Object.hasOwn(changed.value, "__proto__")).toBe(true);
  expect(JSON.stringify(changed.value)).toBe('{"keep":7,"__proto__":{"engineReviewPolluted":true}}');
});

test("literal dots and brackets stay JSON keys rather than paths", () => {
  const values = { "mode.speed": 1, mode: { speed: 2, "key[0]": "old", keep: true } };
  const groups = settingsGroups({ kind: "shroudtopia", id: "test", format: "shroudtopia.json", file: null,
    schema: { properties: { "mode.speed": { type: "integer" }, mode: { type: "object", properties: { speed: { type: "integer" }, "key[0]": { type: "string" } } } } }, values });
  expect(groups[0].paths).toEqual([["mode.speed"], ["mode", "speed"], ["mode", "key[0]"]]);
  expect(groups[0].schema.entries.map((e) => e.default)).toEqual([1, 2, "old"]);
  expect(topLevelChange(values, groups[0].paths[0], 9)).toEqual({ key: "mode.speed", value: 9 });
  expect(topLevelChange(values, groups[0].paths[2], "new")).toEqual({ key: "mode", value: { speed: 2, "key[0]": "new", keep: true } });
  expect(values.mode["key[0]"]).toBe("old");
});
