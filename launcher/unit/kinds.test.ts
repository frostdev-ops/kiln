import { expect, test } from "bun:test";
import { CORE_KINDS, IMPORTED_KINDS, KIND_NAME, KIND_PLUGIN, KIND_SHORT, MOD_KINDS, emptyMods, totalCount } from "../src/api/kinds";
import { DROPIN_KINDS, dropinKinds, setDropins, withDropin } from "../src/pages/profiles/dropins";
import type { Profile } from "../src/api/commands";
import { KIND_WORD } from "../src/deck/status";

test("XHL companion selections roundtrip without a Kiln execution or drop-in switch", () => {
  const mods = emptyMods();
  mods.xhl.push({ id: "xhl-vein-mining", version: "0.0.0", hash: `kdh1:${"a".repeat(64)}`, enabled: true });
  expect(JSON.parse(JSON.stringify(mods)).xhl).toEqual(mods.xhl);
  expect(MOD_KINDS.filter((k) => k === "xhl")).toEqual(["xhl"]);
  expect(IMPORTED_KINDS).toContain("xhl");
  expect(CORE_KINDS).not.toContain("xhl");
  expect(DROPIN_KINDS).not.toContain("xhl");
  expect(KIND_PLUGIN.xhl).toBeUndefined();
  expect(KIND_NAME.xhl).toContain("requires XHL-Core");
  expect(KIND_WORD.xhl).toContain("requires XHL-Core");
  expect(KIND_SHORT.xhl).toBe("XHL");
  expect(totalCount({ xhl: 1 })).toBe(1);
});

test("old and modern drop-in controls cannot claim to control XHL-Core", () => {
  for (const modern of [false, true]) {
    for (const kinds of [[], CORE_KINDS, MOD_KINDS, ["xhl"]] as Profile["dropins"][]) {
      const p: Profile = {
        format: 1, id: "p", name: "P", created: "", updated: "", generation: 1, notes: "",
        mods: emptyMods(), dropins: [...kinds], dropinsEngines: modern, dataDir: "shared", server: null,
      };
      const before = dropinKinds(p);
      expect(before).not.toContain("xhl");
      withDropin(p, "xhl", true);
      expect(p.dropins).not.toContain("xhl");
      expect(dropinKinds(p)).toEqual(before);
      setDropins(p, ["xhl", "eml"]);
      expect(p.dropins).toEqual(["eml"]);
    }
  }
});
