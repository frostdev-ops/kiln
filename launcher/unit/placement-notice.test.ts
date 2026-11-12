// "No profile" and uninstall move the files a mod wrote into its removed copy to kiln/kept-mod-files and say where
// (PlacementCleanup.keptFiles). The window shows those sentences; before, only folders that stayed were named.
import { beforeEach, expect, test } from "bun:test";
import { notifyPlacementCleanup, useToasts } from "../src/ui/Toast";

beforeEach(() => useToasts.setState({ items: [] }));

test("kept files are said, and stay until dismissed", () => {
  notifyPlacementCleanup({ removed: ["/game/mods/creative"], retained: [], keptFiles: ["flame:creative: 2 files it wrote are kept in kiln/kept-mod-files/flame/creative."] });
  const items = useToasts.getState().items;
  expect(items.length).toBe(1);
  expect(String(items[0].detail)).toContain("kiln/kept-mod-files/flame/creative");
});

test("a clean removal says nothing, and an older backend's result without the field is read", () => {
  notifyPlacementCleanup({ removed: ["/game/mods/creative"], retained: [] });
  notifyPlacementCleanup(null);
  expect(useToasts.getState().items.length).toBe(0);
});

test("kept folders and kept files are two notices", () => {
  notifyPlacementCleanup({ removed: [], retained: [{ path: "/game/mods/a", reason: "changed", mayRun: true }], keptFiles: ["eml:b: 1 file is kept."] });
  expect(useToasts.getState().items.length).toBe(2);
});
