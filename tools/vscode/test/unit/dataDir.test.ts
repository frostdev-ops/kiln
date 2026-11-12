// SPDX-License-Identifier: MIT
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { defaultCatalogDir } from "../../src/core/catalog";
import { kilnDataDir, pointedDataDir } from "../../src/core/dataDir";
import { netcoredbgCandidates } from "../../src/core/debugTarget";

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kiln-vscode-datadir-"));

suite("the data folder, as the launcher finds it", () => {
  test("a moved data folder is followed through the pointer in the default root", () => {
    const xdg = tmp();
    const moved = path.join(tmp(), "Kiln");
    fs.mkdirSync(path.join(xdg, "kiln"), { recursive: true });
    fs.writeFileSync(path.join(xdg, "kiln", "data-folder.json"),
      JSON.stringify({ format: 1, path: moved, written: "2026-11-12T00:00:00Z", writer: "kiln-manage test" }));
    const env = { XDG_DATA_HOME: xdg };
    assert.equal(kilnDataDir(env, "linux"), moved);
    assert.equal(defaultCatalogDir(env), path.join(moved, "catalog"));
    assert.deepEqual(netcoredbgCandidates("", env), [path.join(moved, "tools", "netcoredbg", "netcoredbg.exe")]);
    // $KILN_DATA_DIR still wins over the pointer
    assert.equal(kilnDataDir({ ...env, KILN_DATA_DIR: "/d" }, "linux"), "/d");
  });

  test("no pointer, or one that cannot be used, means the default root", () => {
    const xdg = tmp();
    const root = path.join(xdg, "kiln");
    fs.mkdirSync(root, { recursive: true });
    assert.equal(kilnDataDir({ XDG_DATA_HOME: xdg }, "linux"), root);
    for (const bad of ["not json", JSON.stringify({ format: 2, path: "/elsewhere" }), JSON.stringify({ format: 1, path: "relative/dir" }),
      JSON.stringify({ format: 1 })]) {
      fs.writeFileSync(path.join(root, "data-folder.json"), bad);
      assert.equal(pointedDataDir(root), undefined, bad);
      assert.equal(kilnDataDir({ XDG_DATA_HOME: xdg }, "linux"), root, bad);
    }
    // a byte-order mark in front is accepted, as the launcher accepts it
    fs.writeFileSync(path.join(root, "data-folder.json"), "﻿" + JSON.stringify({ format: 1, path: "/moved/Kiln" }));
    assert.equal(pointedDataDir(root), "/moved/Kiln");
  });
});
