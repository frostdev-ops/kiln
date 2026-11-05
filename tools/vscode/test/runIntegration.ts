// SPDX-License-Identifier: MIT
// Runs the integration tests in the installed VS Code (no download), headless and isolated:
//   xvfb-run -a node out/test/runIntegration.js        (npm run test:integration does this)
// - refuses to run outside xvfb-run, so it never opens a window on the user's desktop;
// - throwaway --user-data-dir / --extensions-dir under $KILN_VSCODE_SCRATCH (default: a temp dir), so the user's
//   VS Code profile, settings and extensions are never read or written;
// - no Wayland, no session D-Bus, no keyring.
import { runTests } from "@vscode/test-electron";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

const EXT = path.resolve(__dirname, "../..");
const REPO = path.resolve(EXT, "../..");

function copyDir(a: string, b: string) {
  fs.mkdirSync(b, { recursive: true });
  for (const e of fs.readdirSync(a, { withFileTypes: true })) {
    const s = path.join(a, e.name);
    const d = path.join(b, e.name);
    if (e.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

async function main() {
  if (!process.env.DISPLAY || !/xvfb-run/.test(process.env.XAUTHORITY ?? "")) {
    console.error("runIntegration: run me under xvfb-run (xvfb-run -a node out/test/runIntegration.js); refusing to open a window on this desktop");
    process.exit(2);
  }
  for (const k of ["WAYLAND_DISPLAY", "XDG_SESSION_TYPE", "DBUS_SESSION_BUS_ADDRESS", "GNOME_KEYRING_CONTROL", "SSH_AUTH_SOCK", "ELECTRON_RUN_AS_NODE", "VSCODE_IPC_HOOK_CLI", "VSCODE_PID", "TERM_PROGRAM"]) {
    delete process.env[k];
  }
  process.env.DBUS_SESSION_BUS_ADDRESS = "disabled:";
  const exe = process.env.KILN_VSCODE_EXE || "/usr/share/code/code";
  if (!fs.existsSync(exe)) {
    console.error(`runIntegration: no VS Code at ${exe} (set KILN_VSCODE_EXE)`);
    process.exit(2);
  }
  const scratch = process.env.KILN_VSCODE_SCRATCH || fs.mkdtempSync(path.join(os.tmpdir(), "kiln-vscode-it-"));
  const ws = path.join(scratch, "ws");
  fs.rmSync(ws, { recursive: true, force: true });
  copyDir(path.join(EXT, "test", "fixtures", "ws"), ws);
  const server = path.join(ws, "server");
  fs.writeFileSync(path.join(server, "enshrouded_server.exe"), "");
  const log = path.join(server, "kiln", "logs", "kiln.log");
  fs.writeFileSync(log, fs.readFileSync(log, "utf8").replace(/WSDIR/g, ws));
  // a fake `kiln` CLI: `--version` and `lint --json` with the real linter's output for this case (KL103, the span and
  // the fix cover the key's quotes), enough for the
  // lint wiring; every other command exits 2 so the extension's fallbacks are what the other tests exercise
  const cli = path.join(scratch, "fake-kiln");
  fs.writeFileSync(
    cli,
    `#!${process.execPath}
const fs = require("fs"), a = process.argv.slice(2);
if (a[0] === "--version") { console.log("kiln 0.0.0-fake"); process.exit(0); }
if (a[0] !== "lint") { console.error("fake kiln: only lint"); process.exit(2); }
const stdin = a.includes("--stdin"); const file = stdin ? a[a.indexOf("--stdin-path") + 1] : a.filter((x) => !x.startsWith("--")).slice(1)[0];
const text = stdin ? fs.readFileSync(0, "utf8") : fs.readFileSync(file, "utf8");
const diags = []; const at = text.indexOf('"dependecies"');
if (file.endsWith("kiln.mod.json") && at >= 0) {
  const s = Buffer.byteLength(text.slice(0, at)), e = s + Buffer.byteLength('"dependecies"');
  diags.push({ file, code: "KL103", rule: "unknown-key", severity: "warning", message: '"dependecies": unknown key (Kiln.Host ignores it and logs a warning); did you mean "dependencies"?',
    span: { start: s, end: e }, pointer: "/dependecies", fixes: [{ title: 'Rename to "dependencies"', edits: [{ file, span: { start: s, end: e }, newText: '"dependencies"' }] }] });
}
console.log(JSON.stringify({ format: 1, tool: "kiln-lint", ok: !diags.length, checked: [file], summary: { errors: 0, warnings: diags.length, infos: 0, hints: 0 }, catalog: null, diagnostics: diags }));
process.exit(0);
`,
    { mode: 0o755 },
  );
  // the debugger's stand-ins: a wine that answers tasklist and "runs" a mock netcoredbg (test/fixtures/dap), and a
  // `kiln` with a debug command. Everything they do goes to one log the tests read.
  const dapLog = path.join(scratch, "mock-dap.log");
  fs.rmSync(dapLog, { force: true });
  const mockDbg = path.join(EXT, "test", "fixtures", "dap", "mock-netcoredbg.js");
  const mockNetcoredbg = path.join(scratch, "netcoredbg.exe");
  fs.writeFileSync(mockNetcoredbg, "");
  const mockWine = path.join(scratch, "mock-wine");
  fs.writeFileSync(
    mockWine,
    `#!${process.execPath}
const fs = require("fs"), a = process.argv.slice(2), log = ${JSON.stringify(dapLog)};
fs.appendFileSync(log, JSON.stringify({ kind: "wine", argv: a, prefix: process.env.WINEPREFIX ?? null }) + "\\n");
if (/tasklist/i.test(a[0] ?? "")) {
  process.stdout.write('"svchost.exe","20","Console","1","1,000 K"\\r\\n"enshrouded_server.exe","4242","Console","1","9,000,000 K"\\r\\n');
  process.exit(0);
}
if (a[0] !== ${JSON.stringify(mockNetcoredbg)}) { console.error("mock wine: unexpected " + a.join(" ")); process.exit(3); }
process.env.KILN_MOCK_DAP_LOG = log;
require(${JSON.stringify(mockDbg)});
`,
    { mode: 0o755 },
  );
  const fakeKilnDebug = path.join(scratch, "fake-kiln-debug");
  fs.writeFileSync(
    fakeKilnDebug,
    `#!${process.execPath}
const a = process.argv.slice(2);
if (a[0] === "--version") { console.log("kiln 0.0.0-fake-debug"); process.exit(0); }
if (a[0] !== "debug") process.exit(2);
if (a.includes("--help")) process.exit(0);
if (a.includes("--json")) {
  // like the real one: {ok:false,...} and exit 1 when it would refuse (here: when the server folder has this marker)
  const refuse = require("fs").existsSync(require("path").join(a[a.indexOf("--server") + 1], "fake-refuse"));
  console.log(JSON.stringify(refuse ? { ok: false, error: { code: "tool_missing", message: "fake: no netcoredbg" } } : { ok: true, pid: 4242 }));
  process.exit(refuse ? 1 : 0);
}
process.env.KILN_MOCK_DAP_LOG = ${JSON.stringify(dapLog)};
require(${JSON.stringify(mockDbg)});
`,
    { mode: 0o755 },
  );
  fs.mkdirSync(path.join(ws, ".vscode"), { recursive: true });
  fs.writeFileSync(
    path.join(ws, ".vscode", "settings.json"),
    JSON.stringify(
      {
        "kiln.root": REPO,
        "kiln.cli.path": cli,
        "kiln.servers": [{ name: "fake", dir: server, kind: "folder" }],
        "kiln.server.default": "fake",
        "kiln.server.discover": false,
        "kiln.catalog.path": path.join(REPO, "crates", "kiln-manage", "src", "catalog", "testdata"),
        "kiln.tests.timeoutSeconds": 20,
        "security.workspace.trust.enabled": false,
        "extensions.autoUpdate": false,
        "telemetry.telemetryLevel": "off",
        "update.mode": "none",
      },
      null,
      2,
    ),
  );
  let target = ws;
  let suite = "ws";
  const lab = process.env.KILN_IT_LAB;
  if (lab) {
    // the lab suite: the repository's SampleTests + SampleLib as a multi-root workspace (settings in the
    // .code-workspace file in the scratch dir, so nothing is written into the repository)
    suite = "lab";
    const out = process.env.KILN_OUT || path.join(REPO, ".out", "vscode");
    target = path.join(scratch, "lab.code-workspace");
    fs.writeFileSync(
      target,
      JSON.stringify(
        {
          folders: [{ path: path.join(REPO, "mods", "samples", "SampleTests") }, { path: path.join(REPO, "mods", "samples", "SampleLib") }],
          settings: {
            "kiln.root": REPO,
            "kiln.cli.path": process.env.KILN_IT_REAL_CLI || path.join(scratch, "no-kiln-cli"),
            "kiln.servers": [{ name: path.basename(lab), dir: lab, kind: "lab" }],
            "kiln.server.default": path.basename(lab),
            "kiln.server.discover": false,
            "kiln.dotnet.buildArgs": ["-c", "Debug", "--artifacts-path", path.join(out, "dotnet")], // breakpoints bind in Debug builds only
            "kiln.tests.timeoutSeconds": 120,
            "security.workspace.trust.enabled": false,
            "telemetry.telemetryLevel": "off",
            "update.mode": "none",
          },
        },
        null,
        2,
      ),
    );
    process.env.KILN_OUT = out;
  }
  const code = await runTests({
    vscodeExecutablePath: exe,
    extensionDevelopmentPath: EXT,
    extensionTestsPath: path.join(EXT, "out", "test", "integration", "index.js"),
    launchArgs: [
      target,
      "--user-data-dir", path.join(scratch, "user-data"),
      "--extensions-dir", path.join(scratch, "extensions"),
      "--disable-extensions",
      "--disable-workspace-trust",
      "--skip-welcome",
      "--skip-release-notes",
      "--disable-telemetry",
      "--disable-updates",
      "--password-store=basic",
      "--ozone-platform=x11",
      "--disable-gpu",
      "--no-sandbox",
    ],
    extensionTestsEnv: { KILN_IT_SUITE: suite, KILN_IT_WS: ws, KILN_IT_SERVER: server, KILN_IT_REPO: REPO, KILN_IT_CLI: cli, KILN_IT_LAB: lab ?? "", KILN_IT_DAP_LOG: dapLog, KILN_IT_MOCK_WINE: mockWine, KILN_IT_MOCK_NETCOREDBG: mockNetcoredbg, KILN_IT_FAKE_KILN_DEBUG: fakeKilnDebug },
  });
  process.exit(code);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
