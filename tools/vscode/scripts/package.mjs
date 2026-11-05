// SPDX-License-Identifier: MIT
// Builds a local .vsix: $KILN_OUT/kiln-vscode-<version>.vsix (KILN_OUT default: <repo>/.out/vscode).
// Local only: never `vsce publish`, never the Marketplace or Open VSX.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ext = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const run = (cmd, args) => execFileSync(cmd, args, { cwd: ext, stdio: "inherit" });
run(process.execPath, ["scripts/sync.mjs"]);
run(process.execPath, ["esbuild.mjs", "--production"]);
run(process.execPath, ["scripts/fetch-netcoredbg.mjs"]); // the pinned debugger goes into dist/netcoredbg
const { version } = JSON.parse(fs.readFileSync(path.join(ext, "package.json"), "utf8"));
const out = process.env.KILN_OUT || path.resolve(ext, "..", "..", ".out", "vscode");
fs.mkdirSync(out, { recursive: true });
const vsix = path.join(out, `kiln-vscode-${version}.vsix`);
run(path.join(ext, "node_modules", ".bin", "vsce"), ["package", "--no-dependencies", "--allow-missing-repository", "--skip-license", "--out", vsix]);
console.log(vsix);
