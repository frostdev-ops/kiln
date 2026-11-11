import { beforeEach, expect, test } from "bun:test";
import type { InstallResult, UninstallResult } from "../src/api/commands";
import { notifyInstall } from "../src/shell/notices";
import { notifyPlacementCleanup, useToasts } from "../src/ui/Toast";

beforeEach(() => useToasts.setState({ items: [] }));

const installed: InstallResult = {
  format: 1, installId: "g", installed: "2026-11-11T00:00:00Z", side: "client",
  runtime: { version: "0.2.0-dev", components: ["core"] }, files: [], replaced: [],
};
const uninstalled: UninstallResult = {
  removed: 1, restored: [], adopted: [], kept: [], placements: { removed: [], retained: [] }, warnings: [],
};

test("install recovery paths and changed file names stay until dismissed", () => {
  notifyInstall({ ...installed, changedBackedUp: ["kiln/kiln-core.dll", "kiln/plugins/edited.dll"], backup: "/fixtures/data/backups/install/g/install-1" });
  const notices = useToasts.getState().items;
  expect(notices).toHaveLength(1);
  expect(notices[0].persistent).toBe(true);
  expect(notices[0].detail).toContain("kiln/kiln-core.dll");
  expect(notices[0].detail).toContain("kiln/plugins/edited.dll");
  expect(notices[0].detail).toContain("/fixtures/data/backups/install/g/install-1");
});

test("uninstall lists changed runtime files even without restoration warnings", () => {
  notifyInstall({ ...uninstalled, kept: ["dinput8.dll", "kiln/plugins/edited.dll"] });
  const notices = useToasts.getState().items;
  expect(notices).toHaveLength(1);
  expect(notices[0].persistent).toBe(true);
  expect(notices[0].detail).toContain("dinput8.dll");
  expect(notices[0].detail).toContain("kiln/plugins/edited.dll");
});

test("cleanup warns that a retained SET copy can still run through Kiln itself", () => {
  notifyPlacementCleanup({ removed: [], retained: [{ path: "shrouded_engine_tweaks/mods/edited", reason: "init.lua was changed", mayRun: true }] });
  const notice = useToasts.getState().items[0];
  expect(notice.persistent).toBe(true);
  expect(notice.detail).toContain("may still run through Kiln or another loader");
});

test("an uneventful runtime change has no recovery notice", () => {
  notifyInstall(installed);
  notifyInstall(uninstalled);
  expect(useToasts.getState().items).toHaveLength(0);
});
