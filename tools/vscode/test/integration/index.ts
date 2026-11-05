// SPDX-License-Identifier: MIT
// Entry point inside the VS Code extension host: runs the integration suites with mocha.
import * as path from "node:path";
import Mocha from "mocha";

export function run(): Promise<void> {
  const mocha = new Mocha({ ui: "tdd", color: false, timeout: process.env.KILN_IT_SUITE === "lab" ? 600_000 : 60_000 });
  mocha.addFile(path.join(__dirname, process.env.KILN_IT_SUITE === "lab" ? "lab.it.js" : "extension.it.js"));
  return new Promise((resolve, reject) =>
    mocha.run((failures) => (failures ? reject(new Error(`${failures} integration test(s) failed`)) : resolve())),
  );
}
