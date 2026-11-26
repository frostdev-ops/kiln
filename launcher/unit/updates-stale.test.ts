// netjunk's report (2026-11-26): "Update 0.2.10" beside Kiln Auto Loot 0.2.10, still there after he updated by drag and
// drop and by Add mod. An offer names a version above the installed one (semver, never text), and the shared result
// is worked out again (offline, no request) as soon as the Library changes, whichever way it changed.
import { describe, expect, test } from "bun:test";
import { QueryClient } from "@tanstack/react-query";
import { compareVersions, hasUpdate, type ModUpdateStatus } from "../src/api/updates";
import { noticeable, watchLibrary } from "../src/shell/modUpdates";

const status = (patch: Partial<ModUpdateStatus>): ModUpdateStatus => ({
  libraryRef: { kind: "kiln", id: "kiln-auto-loot", version: "0.2.9", hash: "kdh1:9" }, name: "Kiln Auto Loot",
  source: { provider: "github", destination: "frostdev-ops/kiln-auto-loot", origin: "manifest", authenticated: false },
  installedVersion: "0.2.9", candidateVersion: "0.2.10", candidateIdentity: null, state: "available", compatibility: "unknown", coverage: "complete",
  checkedAt: "2026-11-25T12:00:00Z", stale: false, reason: null, pageUrl: "https://github.com/frostdev-ops/kiln-auto-loot/releases/tag/v0.2.10", ...patch,
  // a GitHub status counts in the Library's versions: its installed version is its Library entry's
  ...(patch.installedVersion && !patch.libraryRef ? { libraryRef: { kind: "kiln", id: "kiln-auto-loot", version: patch.installedVersion, hash: `kdh1:${patch.installedVersion}` } } : {}),
});

describe("versions", () => {
  test("semver precedence, not text", () => {
    expect(compareVersions("0.2.10", "0.2.9")).toBe(1);
    expect(compareVersions("0.2.9", "0.2.10")).toBe(-1);
    expect(compareVersions("0.2.12", "0.2.12")).toBe(0);
    expect(compareVersions("v0.2.12", "0.2.12")).toBe(0);
    expect(compareVersions("1.2", "1.2.0")).toBe(0);
    expect(compareVersions("1.0.0+build.2", "1.0.0+build.1")).toBe(0);
    expect(compareVersions("1.0.0-rc.1", "1.0.0")).toBe(-1);
    expect(compareVersions("0.2.0-dev.10", "0.2.0-dev.9")).toBe(1);
    expect(compareVersions("1.0.0-alpha", "1.0.0-1")).toBe(1);
    expect(compareVersions("1.0.0-alpha.1", "1.0.0-alpha")).toBe(1);
    expect(compareVersions("99999999999999999999.0.0", "99999999999999999998.0.0")).toBe(1);
    expect(compareVersions("Main file v2", "0.2.12")).toBeNull();
    expect(compareVersions("", "1.0.0")).toBeNull();
  });
});

describe("an offer", () => {
  test("names a version above the installed one", () => {
    expect(hasUpdate(status({}))).toBe(true);
    // the report: the status says available, and its candidate is the installed version
    expect(hasUpdate(status({ installedVersion: "0.2.10", libraryRef: { kind: "kiln", id: "kiln-auto-loot", version: "0.2.10", hash: "kdh1:10" } }))).toBe(false);
    expect(hasUpdate(status({ installedVersion: "0.2.12" }))).toBe(false);
    // versions that are not semantic (Nexus Mods' free text): the backend's word stands
    expect(hasUpdate(status({ installedVersion: "Main file", candidateVersion: "Main file v2" }))).toBe(true);
    expect(hasUpdate(status({ state: "new-upload", candidateVersion: "0.2.9" }))).toBe(true);
  });

  test("the row's installed copy decides: a result from before an update offers nothing", () => {
    const before = status({});
    expect(noticeable(before, "0.2.9")).toBe(true);
    // the player installed 0.2.10 since that result: no "Update 0.2.10" beside 0.2.10
    expect(noticeable(before, "0.2.10")).toBe(false);
    expect(noticeable(before, "0.2.12")).toBe(false);
    // a row rolled back below the newest copy in the Library is still offered what is above
    expect(noticeable(status({ installedVersion: "0.2.10", candidateVersion: "0.2.12" }), "0.2.9")).toBe(true);
    // a new upload of the version the row has stays a notice; one of a version below the row's does not
    expect(noticeable(status({ state: "new-upload", installedVersion: "0.2.10", candidateVersion: "0.2.10" }), "0.2.10")).toBe(true);
    expect(noticeable(status({ state: "new-upload", installedVersion: "0.2.10", candidateVersion: "0.2.10" }), "0.2.11")).toBe(false);
    // Nexus Mods numbers its uploads its own way (Weather Plus: 1.0.0 → 1.1.0 there, 1.4.2 in its manifest): the row's
    // version says nothing about them
    const nexus = status({ libraryRef: { kind: "kiln", id: "weather-plus", version: "1.4.2", hash: "kdh1:w" }, installedVersion: "1.0.0", candidateVersion: "1.1.0",
      source: { provider: "nexus", destination: "enshrouded/316/90316", origin: "receipt", authenticated: true } });
    expect(noticeable(nexus, "1.4.2")).toBe(true);
    // what could not be told is still said
    expect(noticeable(status({ state: "unknown", reason: "network", candidateVersion: null }), "0.2.10")).toBe(true);
  });
});

describe("the Library changes", () => {
  const entry = (version: string, hash = `kdh1:${version}`) => [{ kind: "kiln", id: "kiln-auto-loot", name: "Kiln Auto Loot", versions: [{ version, hash }] }];

  test("the update result is worked out again, once per change", async () => {
    const qc = new QueryClient();
    let runs = 0;
    const stop = watchLibrary(qc, () => { runs += 1; }, 0);
    const settle = () => new Promise((r) => setTimeout(r, 5));
    // the first read of the list is not a change
    qc.setQueryData(["library_list", null], entry("0.2.9"));
    await settle();
    expect(runs).toBe(0);
    // read again, the same: nothing to do
    qc.setQueryData(["library_list", null], entry("0.2.9"));
    await settle();
    expect(runs).toBe(0);
    // 0.2.10 imported (a drop, Add mod, the Library's import, a catalog install): worked out again
    qc.setQueryData(["library_list", null], [{ ...entry("0.2.10")[0], versions: [{ version: "0.2.10", hash: "kdh1:10" }, { version: "0.2.9", hash: "kdh1:0.2.9" }] }]);
    await settle();
    expect(runs).toBe(1);
    // the same version with other files (a take-over): again
    qc.setQueryData(["library_list", null], [{ ...entry("0.2.10")[0], versions: [{ version: "0.2.10", hash: "kdh1:other" }, { version: "0.2.9", hash: "kdh1:0.2.9" }] }]);
    await settle();
    expect(runs).toBe(2);
    // another query is not the Library
    qc.setQueryData(["profile_get", "ember-run"], { id: "ember-run" });
    await settle();
    expect(runs).toBe(2);
    stop();
    qc.setQueryData(["library_list", null], entry("0.2.12"));
    await settle();
    expect(runs).toBe(2);
  });

  // An update from the Catalog page: no Library list is read there, the Catalog's install records are. Their newest
  // versions and "update" flags are what a run reads again: those alone are no change (no run setting off another).
  test("the Catalog's install records changing is a change too; what a check says of them is not", async () => {
    const qc = new QueryClient();
    let runs = 0;
    const stop = watchLibrary(qc, () => { runs += 1; }, 0);
    const settle = () => new Promise((r) => setTimeout(r, 5));
    const rec = (gsid: string, version: string, update: boolean) => [{ modId: "316", name: "Weather Plus", fileId: "90316", fileName: "Weather Plus", versionId: `80${gsid}`,
      versionGsid: gsid, version, refs: [{ kind: "kiln", id: "weather-plus", version: "1.4.2", hash: `kdh1:${gsid}` }], installed: "2026-11-20T00:00:00Z", source: "nxm",
      info: { version, versionGsid: gsid, fileName: "Weather Plus", inLibrary: true, latestVersion: "1.1.0", latestGsid: "1016", updateAvailable: update } }];
    qc.setQueryData(["nexus_installed"], rec("1006", "1.0.0", true));
    await settle();
    expect(runs).toBe(0);
    // read again after a check: the update flag and the newest version are not the records
    qc.setQueryData(["nexus_installed"], rec("1006", "1.0.0", false).map((m) => ({ ...m, info: { ...m.info, latestVersion: "1.2.0", latestGsid: "1020" } })));
    await settle();
    expect(runs).toBe(0);
    // "Update to 1.1.0" in the Catalog: another upload installed
    qc.setQueryData(["nexus_installed"], rec("1016", "1.1.0", false));
    await settle();
    expect(runs).toBe(1);
    stop();
  });

  // The offline check is shared (checkModUpdates hands a second caller the running one's answer): a change that comes
  // while it runs would get an answer read before that change. One run at a time, and one more after it for whatever
  // changed meanwhile.
  test("a change while the work runs is worked out again after it, once", async () => {
    const qc = new QueryClient();
    let runs = 0;
    const done: [() => void, (e: Error) => void][] = [];
    const stop = watchLibrary(qc, () => { runs += 1; return new Promise<void>((ok, fail) => done.push([ok, fail])); }, 0);
    const settle = () => new Promise((r) => setTimeout(r, 5));
    qc.setQueryData(["library_list", null], entry("0.2.9"));
    qc.setQueryData(["library_list", null], entry("0.2.10"));
    await settle();
    expect(runs).toBe(1);
    // two drops while it runs
    qc.setQueryData(["library_list", null], entry("0.2.11"));
    await settle();
    qc.setQueryData(["library_list", null], entry("0.2.12"));
    await settle();
    expect(runs).toBe(1);
    done.shift()![0]();
    await settle();
    expect(runs).toBe(2);
    // a run that fails does not stop the watcher
    qc.setQueryData(["library_list", null], entry("0.2.13"));
    await settle();
    expect(runs).toBe(2);
    done.shift()![1](new Error("the check failed"));
    await settle();
    expect(runs).toBe(3);
    done.shift()![0]();
    await settle();
    expect(runs).toBe(3);
    // stopped while a run is waited for: nothing after it
    qc.setQueryData(["library_list", null], entry("0.2.14"));
    await settle();
    expect(runs).toBe(4);
    qc.setQueryData(["library_list", null], entry("0.2.15"));
    await settle();
    stop();
    done.shift()![0]();
    await settle();
    expect(runs).toBe(4);
  });
});
