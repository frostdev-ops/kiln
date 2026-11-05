// SPDX-License-Identifier: MIT
// node esbuild.mjs [--watch] [--tests] [--production]
import * as esbuild from "esbuild";
import fs from "node:fs";
import path from "node:path";

const watch = process.argv.includes("--watch");
const tests = process.argv.includes("--tests");
const production = process.argv.includes("--production");

const common = {
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node20",
  sourcemap: !production,
  minify: production,
  external: ["vscode"],
  // jsonc-parser's UMD build requires its parts dynamically; its ESM build bundles
  mainFields: ["module", "main"],
  logLevel: "warning",
};

const builds = [
  { ...common, entryPoints: ["src/extension.ts"], outfile: "dist/extension.js" },
  { ...common, entryPoints: ["src/dapProxy.ts"], outfile: "dist/dapProxy.js" },
];

if (tests) {
  const find = (dir) =>
    fs.existsSync(dir)
      ? fs.readdirSync(dir).filter((f) => f.endsWith(".ts")).map((f) => path.join(dir, f))
      : [];
  builds.push({ ...common, entryPoints: find("test/unit"), outdir: "out/test/unit" });
  builds.push({ ...common, entryPoints: find("test/integration"), outdir: "out/test/integration", external: ["vscode", "mocha"] });
  builds.push({ ...common, entryPoints: ["test/runIntegration.ts"], outfile: "out/test/runIntegration.js" });
}

if (watch) {
  for (const b of builds) (await esbuild.context(b)).watch();
  console.log("esbuild: watching");
} else {
  await Promise.all(builds.map((b) => esbuild.build(b)));
}
