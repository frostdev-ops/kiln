// SPDX-License-Identifier: MIT
// Integration tests in a real VS Code (xvfb, throwaway profile): the fixture workspace test/fixtures/ws, a fake
// server folder whose control folder is answered by an in-test stand-in for Kiln.Host, a fake `kiln lint`, the
// launcher's mock KAP/1 server, the bundled schemas and the synthetic catalog fixture.
import * as assert from "node:assert/strict";
import { ChildProcess, spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import type { KilnApi } from "../../src/extension";
import { ConsoleTerminal } from "../../src/features/console";

const WS = process.env.KILN_IT_WS!;
const SERVER = process.env.KILN_IT_SERVER!;
const REPO = process.env.KILN_IT_REPO!;
const MOD = path.join(WS, "FrostMod");
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function until<T>(what: string, f: () => T | undefined | false | Promise<T | undefined | false>, ms = 20_000): Promise<T> {
  const t0 = Date.now();
  for (;;) {
    const v = await f();
    if (v) return v;
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(150);
  }
}

async function open(file: string): Promise<vscode.TextDocument> {
  const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(file));
  await vscode.window.showTextDocument(doc);
  return doc;
}

function posOf(doc: vscode.TextDocument, needle: string, delta = 0): vscode.Position {
  const i = doc.getText().indexOf(needle);
  assert.ok(i >= 0, `${needle} not in ${doc.fileName}`);
  return doc.positionAt(i + delta);
}

function hoverText(h: vscode.Hover[]): string {
  return h.flatMap((x) => x.contents.map((c) => (typeof c === "string" ? c : "value" in c ? c.value : ""))).join("\n");
}

/** Answers kiln/control/*.cmd like Kiln.Host (reload, test, mods). */
function fakeKilnHost(server = SERVER, seen: string[] = []): vscode.Disposable {
  const ctl = path.join(server, "kiln", "control");
  fs.mkdirSync(ctl, { recursive: true });
  const log = path.join(server, "kiln", "logs", "kiln.log");
  const write = (name: string, res: object) => {
    fs.writeFileSync(path.join(ctl, `${name}.result.json.tmp`), JSON.stringify(res));
    fs.renameSync(path.join(ctl, `${name}.result.json.tmp`), path.join(ctl, `${name}.result.json`));
  };
  const t = setInterval(() => {
    for (const f of fs.readdirSync(ctl).filter((x) => x.endsWith(".cmd"))) {
      const command = fs.readFileSync(path.join(ctl, f), "utf8").trim();
      fs.rmSync(path.join(ctl, f));
      seen.push(command);
      const name = f.slice(0, -4);
      const [verb, id, filter] = command.split(/\s+/);
      if (verb === "reload") write(name, { command, ok: id === "frost-mod", message: id === "frost-mod" ? "reloaded frost-mod 0.3.0" : `mod ${id} is not loaded` });
      else if (verb === "test") {
        const all = [
          { name: "FrostTests.Blade_is_cold", outcome: "pass", ms: 1.2, message: null, stack: null },
          {
            name: "FrostTests.Shard_melts",
            outcome: "fail",
            ms: 40.5,
            message: "it did not melt",
            stack: `   at FrostMod.FrostTests.Shard_melts(TestContext t) in Z:${path.join(MOD, "FrostModTests.cs").replace(/\//g, "\\")}:line 14`,
          },
          { name: "FrostTests.Needs_a_player", outcome: "skip", ms: 0, message: "needs a player", stack: null },
        ].filter((r) => !filter || r.name.toLowerCase().includes(filter.toLowerCase()));
        const rep = { mod: id, version: "0.3.0", started: new Date().toISOString(), ms: 50, ok: !all.some((r) => r.outcome === "fail"),
          passed: all.filter((r) => r.outcome === "pass").length, failed: all.filter((r) => r.outcome === "fail").length,
          skipped: all.filter((r) => r.outcome === "skip").length, results: all };
        fs.mkdirSync(path.join(SERVER, "kiln", "logs", "tests"), { recursive: true });
        fs.writeFileSync(path.join(SERVER, "kiln", "logs", "tests", `${id}.json`), JSON.stringify(rep, null, 2));
        fs.appendFileSync(log, all.map((r) => `[${r.outcome === "fail" ? "E" : "I"} 01:00:00.000] [mod:${id}] ${r.outcome.toUpperCase()} ${r.name}\n`).join(""));
        write(name, { command, ok: rep.ok, message: `${rep.passed} passed, ${rep.failed} failed`, data: rep });
      } else write(name, { command, ok: true, message: "ok", data: [] });
    }
  }, 50);
  return { dispose: () => clearInterval(t) };
}

async function noCli() {
  await vscode.workspace.getConfiguration("kiln").update("cli.path", path.join(WS, "no-such-kiln"), vscode.ConfigurationTarget.Workspace);
  await sleep(200);
}

suite("Kiln extension", () => {
  let api: KilnApi;

  suiteSetup(async () => {
    const ext = vscode.extensions.getExtension<KilnApi>("kiln.kiln-vscode");
    assert.ok(ext, "extension present");
    api = await ext.activate();
    assert.ok(api.servers, "API exported");
  });

  test("every contributed command is registered", async () => {
    const pkg = vscode.extensions.getExtension("kiln.kiln-vscode")!.packageJSON;
    const have = new Set(await vscode.commands.getCommands(true));
    const missing = pkg.contributes.commands.map((c: { command: string }) => c.command).filter((c: string) => !have.has(c));
    assert.deepEqual(missing, []);
  });

  test("languages: .kimp, kiln.log, kiln.mod.json as JSONC", async () => {
    assert.equal((await open(path.join(MOD, "impact", "chill.kimp"))).languageId, "kimp");
    assert.equal((await open(path.join(SERVER, "kiln", "logs", "kiln.log"))).languageId, "kiln-log");
    assert.equal((await open(path.join(MOD, "kiln.mod.json"))).languageId, "jsonc");
  });

  test("bundled schema validates kiln.mod.json (JSON language service)", async () => {
    const file = path.join(MOD, "kiln.mod.json");
    const doc = await open(file);
    const ed = vscode.window.activeTextEditor!;
    await ed.edit((b) => b.insert(posOf(doc, '"apiVersion"'), '"side": "sideways",\n  '));
    const diags = await until("a schema diagnostic for side", () => {
      const d = vscode.languages.getDiagnostics(doc.uri).filter((x) => x.source !== "kiln lint" && /sideways|server|client|both|enum|Value is not accepted/i.test(x.message));
      return d.length ? d : undefined;
    });
    assert.ok(diags.length >= 1);
    // comments and the trailing comma are fine (allowComments/allowTrailingCommas from the dialect)
    assert.ok(!vscode.languages.getDiagnostics(doc.uri).some((x) => /comment|trailing comma/i.test(x.message)), "no comment/comma complaints");
    await vscode.commands.executeCommand("workbench.action.files.revert");
  });

  test("kiln lint on save: diagnostic at the byte span, quick fix applies", async () => {
    const file = path.join(MOD, "kiln.mod.json");
    const doc = await open(file);
    await doc.save();
    await vscode.commands.executeCommand("workbench.action.files.save");
    await api.lint.lintDocument(doc, false);
    const d = await until("kiln lint diagnostic", () => vscode.languages.getDiagnostics(doc.uri).find((x) => x.source === "kiln lint"));
    assert.equal(doc.getText(d.range), '"dependecies"');
    assert.equal(d.severity, vscode.DiagnosticSeverity.Warning);
    const actions = await vscode.commands.executeCommand<vscode.CodeAction[]>("vscode.executeCodeActionProvider", doc.uri, d.range);
    const fix = actions.find((a) => a.title === 'Rename to "dependencies"');
    assert.ok(fix?.edit, "quick fix offered");
    await vscode.workspace.applyEdit(fix.edit);
    assert.ok(doc.getText().includes('"dependencies"'));
    await vscode.commands.executeCommand("workbench.action.files.revert");
  });

  test("game ids: completion and hover from the catalog", async () => {
    assert.ok(api.catalog.catalog, "catalog loaded");
    const doc = await open(path.join(MOD, "content", "pack.json"));
    const list = await vscode.commands.executeCommand<vscode.CompletionList>("vscode.executeCompletionItemProvider", doc.uri, posOf(doc, '"item": ""', 9));
    const labels = list.items.map((i) => (typeof i.label === "string" ? i.label : i.label.label));
    assert.ok(labels.includes("Test_Frost_Blade"), `completion has the fixture item (${labels.slice(0, 5).join(", ")})`);
    const h = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, '"Test_Frost_Blade"', 3));
    const text = hoverText(h);
    assert.match(text, /Frost Blade/);
    assert.match(text, /data:image\/webp;base64,/, "icon inlined");
    const key = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, '"Test_Ice_Shard"', 3));
    assert.match(hoverText(key), /Ice Shard|Test_Ice_Shard/);
    assert.ok(api.catalog.getChildren().length >= 5, "explorer sections");
  });

  test("VFX blocks: the schema through its reference, completion, hover, budget, snippets", async () => {
    const doc = await open(path.join(MOD, "blocks", "fx.json"));
    assert.equal(doc.languageId, "jsonc");
    // the section's schema is another file: the JSON language service has to follow the $ref
    const typo = await until("a schema diagnostic on a VFX field", () => vscode.languages.getDiagnostics(doc.uri).find((d) => d.source !== "kiln lint" && /lifetimes/.test(d.message))).catch((e) => {
      throw new Error(`${e.message}; the document has: ${JSON.stringify(vscode.languages.getDiagnostics(doc.uri).map((d) => [d.source, d.range.start.line, d.message]))}`);
    });
    assert.equal(typo.range.start.line, doc.positionAt(doc.getText().indexOf('"lifetimes"')).line);
    assert.ok(!vscode.languages.getDiagnostics(doc.uri).some((d) => d.source !== "kiln lint" && /comment/i.test(d.message)), "comments are fine in blocks/*.json");
    const labels = async (at: vscode.Position) => {
      const list = await vscode.commands.executeCommand<vscode.CompletionList>("vscode.executeCompletionItemProvider", doc.uri, at);
      return list.items.map((i) => (typeof i.label === "string" ? i.label : i.label.label));
    };
    const fields = await labels(posOf(doc, '"burst": 24', 1));
    assert.ok(fields.includes("drag"), `the schema completes a particle's fields (${fields.slice(0, 6).join(", ")})`);
    const refs = await labels(posOf(doc, '"empty": { "vfx": ""', 18));
    assert.ok(refs.includes("frost_burst"), `a block of this mod (${refs.slice(0, 6).join(", ")})`);
    assert.ok(refs.includes("game:Explosion_Test_Frost"), "a game VFX from the catalog");
    const params = await labels(posOf(doc, '"": 1', 1));
    assert.ok(params.includes("color") && params.includes("targetPosition"), `a game VFX's parameters (${params.slice(0, 8).join(", ")})`);
    const hover = async (needle: string, d = 2) => hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, needle, d)));
    const onGame = await hover('"game:Explosion_Test_Frost"');
    assert.match(onGame, /runs 10 s/);
    assert.match(onGame, /`radius` float = 2/);
    assert.match(await hover('"game:Attack_Test_Frostling_Slash"'), /Explosion_Test_Frost/, "any name that finds it");
    assert.match(await hover('"000003ea-7e57'), /use `game:<guid>`/, "a label says so");
    assert.match(await hover('"game:No_Such_Vfx"'), /not in the catalog/);
    const onBlock = await hover('"frost_burst": {');
    assert.match(onBlock, /2 particle blocks and a light: 5 of a VFX's 55 entries/);
    assert.match(onBlock, /VFX: 2 of the 2,045 left for all mods \(2,048, the game uses 3\)/, "the catalog's count: 3 in the fixture");
    assert.match(onBlock, /Mesh memory: 280 of 241,844 bytes/);
    assert.match(onBlock, /Sounds in VFX: 1 of 10/);
    assert.match(await hover('"vfx": "frost_burst"', 10), /VFX block/, "a reference to a block shows the block");
    const cs = await vscode.workspace.openTextDocument({ language: "csharp", content: 'var v = VfxId.Game("Attack_Test_Frostling_Slash");' });
    const onCode = hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", cs.uri, new vscode.Position(0, 25)));
    assert.match(onCode, /Explosion_Test_Frost/, "a name in C#");
    const snippets = await labels(posOf(doc, '"effects": {', 0));
    assert.ok(["vfx-burst", "vfx-trail", "vfx-light", "vfx-flipbook", "vfx-game", "vfx-parameters"].every((s) => snippets.includes(s)), `snippets (${snippets.filter((s) => s.startsWith("vfx")).join(", ")})`);
  });

  test("Impact: hover, completion, signature help, definition, diagnostics", async () => {
    const doc = await open(path.join(MOD, "impact", "chill.kimp"));
    const h = hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, "getSelf", 2)));
    assert.match(h, /getSelf\(/);
    const mod = hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, "ecall frost-mod.Chill", 9)));
    assert.match(mod, /Applies the chill buff/, "mod native from impact/natives.json");
    const cfg = hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, '"frost-mod.ChillSeconds"', 4)));
    assert.match(cfg, /0x[0-9a-f]{8}/, "a string config id hashes");
    const op = hoverText(await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", doc.uri, posOf(doc, "rvm", 1)));
    assert.match(op, /yield/);
    const sig = await vscode.commands.executeCommand<vscode.SignatureHelp>("vscode.executeSignatureHelpProvider", doc.uri, posOf(doc, "$secs)", 1));
    assert.equal(sig.activeParameter, 1);
    assert.match(sig.signatures[0].label, /frost-mod\.Chill\(target/);
    const ed = vscode.window.activeTextEditor!;
    const line = doc.lineCount - 1;
    await ed.edit((b) => b.insert(new vscode.Position(line, 0), "        ecall appl"));
    const comp = await vscode.commands.executeCommand<vscode.CompletionList>("vscode.executeCompletionItemProvider", doc.uri, new vscode.Position(line, 18));
    assert.ok(comp.items.some((i) => (typeof i.label === "string" ? i.label : i.label.label) === "applyDamage"));
    await vscode.commands.executeCommand("workbench.action.files.revert");
    const def = await vscode.commands.executeCommand<vscode.Location[]>("vscode.executeDefinitionProvider", doc.uri, posOf(doc, "br loop", 4));
    assert.equal(def[0].range.start.line, posOf(doc, "loop:").line);
    const broken = await open(path.join(MOD, "impact", "broken.kimp"));
    const d = await until("assembler diagnostic", () => vscode.languages.getDiagnostics(broken.uri).find((x) => x.source === "impact asm"));
    assert.equal(d.range.start.line, 6);
    assert.match(d.message, /takes 1 argument/);
    const okDiags = vscode.languages.getDiagnostics(doc.uri).filter((x) => x.severity === vscode.DiagnosticSeverity.Error);
    assert.deepEqual(okDiags.map((x) => x.message), [], "chill.kimp assembles");
  });

  test("tasks: build/deploy/pack per mod (no CLI: dotnet)", async () => {
    await noCli();
    const tasks = await vscode.tasks.fetchTasks({ type: "kiln" });
    const names = tasks.map((t) => t.name);
    for (const n of ["build frost-mod", "deploy frost-mod -> fake", "pack frost-mod"]) assert.ok(names.includes(n), `${n} in ${names.join(", ")}`);
    const deploy = tasks.find((t) => t.name === "deploy frost-mod -> fake")!;
    const ex = deploy.execution as vscode.ProcessExecution;
    assert.ok(ex.args.includes("-t:KilnDeploy") && ex.args.includes(`-p:KilnServer=${SERVER}`), ex.args.join(" "));
    assert.deepEqual(deploy.problemMatchers, ["$kiln-sdk", "$kiln-sdk-file", "$kiln-sdk-project", "$kiln-dotnet"]);
  });

  test("control folder: reload; Kiln.Testing: discovery and a run with pass/fail/skip", async () => {
    await noCli();
    const host = fakeKilnHost();
    try {
      const mod = api.mods.all().find((m) => m.id === "frost-mod")!;
      assert.equal(await api.mods.reload(mod), true);
      await api.testing.discover();
      const item = api.testing.controller.items.get("frost-mod");
      assert.ok(item, "mod item");
      const cls = item.children.get("frost-mod::FrostTests")!;
      assert.equal(cls.children.size, 3);
      const cts = new vscode.CancellationTokenSource();
      await api.testing.run(new vscode.TestRunRequest([item]), cts.token, false);
      assert.deepEqual(Object.fromEntries(api.testing.lastResults), {
        "frost-mod::FrostTests.Blade_is_cold": "pass",
        "frost-mod::FrostTests.Shard_melts": "fail",
        "frost-mod::FrostTests.Needs_a_player": "skip",
      });
      const one = cls.children.get("frost-mod::FrostTests.Blade_is_cold")!;
      await api.testing.run(new vscode.TestRunRequest([one]), cts.token, false);
      assert.deepEqual(Object.fromEntries(api.testing.lastResults), { "frost-mod::FrostTests.Blade_is_cold": "pass" });
    } finally {
      host.dispose();
    }
  });

  test("kiln.log links: mod tag -> workspace manifest, stack frame -> file:line, test name -> test", async () => {
    const doc = await open(path.join(SERVER, "kiln", "logs", "kiln.log"));
    const links = await vscode.commands.executeCommand<vscode.DocumentLink[]>("vscode.executeLinkProvider", doc.uri);
    const targets = links.map((l) => l.target?.toString() ?? "");
    const files = links.map((l) => `${l.target?.fsPath}#${l.target?.fragment}`);
    assert.ok(targets.some((t) => t.endsWith("FrostMod/kiln.mod.json")), "mod link");
    assert.ok(files.some((t) => t.endsWith("FrostModTests.cs#L14")), `stack link in ${files.join(" ")}`);
    assert.ok(files.some((t) => /FrostModTests\.cs#L9,\d+$/.test(t)), `PASS FrostTests.Blade_is_cold -> its method (${files.join(" ")})`);
    const ch = await api.log.follow(api.servers.selected());
    assert.ok(ch?.name.includes("fake"));
  });

  test("KAP/1 console against the mock Kiln.Admin", async function () {
    const mock = path.join(REPO, "tools", "launcher", "mock-admin.py");
    if (!fs.existsSync(mock)) this.skip();
    const dir = fs.mkdtempSync(path.join(WS, "kap-"));
    const proc: ChildProcess = spawn("python3", [mock, "serve", "--port", "0", "--dir", dir], { stdio: "ignore" });
    try {
      await until("endpoint.json", () => fs.existsSync(path.join(dir, "kiln", "data", "kiln-admin", "endpoint.json")), 10_000);
      const term = new ConsoleTerminal(api.ctx, { name: "mock", dir, kind: "folder", source: "settings" });
      let out = "";
      term.onDidWrite((s) => (out += s));
      term.open();
      await until("banner", () => out.includes("ops)"));
      term.handleInput("help\r");
      await until("command output", () => /\/\w+ - /.test(out) || out.includes("(mock) ran"));
      term.handleInput(".op ping\r");
      await until("op result", () => /ok.*ping/.test(out));
      term.handleInput(".op nope_op\r");
      await until("unknown op", () => out.includes("unknown_op"));
      term.handleInput("\x04");
    } finally {
      proc.kill("SIGTERM");
    }
  });

  // debug: the adapter wiring against a mock netcoredbg under a mock wine (test/fixtures/dap). A stand-in process plays
  // the running server (api.debug.hostPids), so nothing here ever looks like a real server to other tools.
  const DAP_LOG = process.env.KILN_IT_DAP_LOG!;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const dapLog = (from = 0): any[] => {
    try {
      return fs.readFileSync(DAP_LOG, "utf8").split("\n").filter(Boolean).slice(from).map((l) => JSON.parse(l));
    } catch {
      return [];
    }
  };
  const kilnCfg = () => vscode.workspace.getConfiguration("kiln");
  const attachTo = (server: string) =>
    vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], { type: "kiln", request: "attach", name: `attach ${server}`, server });

  async function withMockLab(fn: (lab: string, control: string[]) => Promise<void>): Promise<void> {
    const lab = path.join(WS, "labs", "mocklab");
    fs.mkdirSync(path.join(lab, "kiln"), { recursive: true });
    fs.writeFileSync(path.join(lab, "enshrouded_server.exe"), "");
    const control: string[] = [];
    const kilnHost = fakeKilnHost(lab, control);
    const host = spawn("sleep", ["300"], { cwd: lab, env: { ...process.env, WINEPREFIX: `${lab}.pfx` }, stdio: "ignore" });
    const hostPids = api.debug.hostPids;
    const servers = kilnCfg().get("servers");
    const target = vscode.ConfigurationTarget.Workspace;
    api.debug.hostPids = (d) => (d.dir === lab ? [host.pid!] : []);
    await kilnCfg().update("servers", [{ name: "fake", dir: SERVER }, { name: "mocklab", dir: lab, kind: "lab" }], target);
    await kilnCfg().update("debug.wine", process.env.KILN_IT_MOCK_WINE, target);
    await kilnCfg().update("debug.netcoredbg", process.env.KILN_IT_MOCK_NETCOREDBG, target);
    try {
      await fn(lab, control);
    } finally {
      kilnHost.dispose();
      api.debug.hostPids = hostPids;
      await kilnCfg().update("servers", servers, target);
      await kilnCfg().update("debug.wine", undefined, target);
      await kilnCfg().update("debug.netcoredbg", undefined, target);
      host.kill("SIGTERM");
    }
  }

  async function stopAndWait(session: vscode.DebugSession): Promise<void> {
    await vscode.debug.stopDebugging(session);
    await until("the session to end", () => !vscode.debug.activeDebugSession);
  }

  test("debug: attach through the built-in proxy: breakpoint, stack, evaluate, Stop detaches", async () => {
    await withMockLab(async (lab, control) => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const seen: any[] = [];
      const tracker = vscode.debug.registerDebugAdapterTrackerFactory("kiln", { createDebugAdapterTracker: () => ({ onDidSendMessage: (m) => seen.push(m) }) });
      const src = path.join(MOD, "FrostModTests.cs");
      const bp = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(src), new vscode.Position(8, 0)));
      vscode.debug.addBreakpoints([bp]);
      const mark = dapLog().length;
      try {
        assert.equal(await attachTo("mocklab"), true);
        const session = vscode.debug.activeDebugSession!;
        assert.equal(session.type, "kiln");
        await until("the breakpoint stop", () => seen.some((m) => m.type === "event" && m.event === "stopped"));
        const caps = seen.find((m) => m.type === "response" && m.command === "initialize").body;
        assert.deepEqual([caps.supportTerminateDebuggee, caps.supportsTerminateRequest, caps.supportsRestartRequest], [false, false, false]);
        const st = await session.customRequest("stackTrace", { threadId: 1 });
        assert.equal(st.stackFrames[0].source.path, src);
        assert.equal(st.stackFrames[0].line, 9);
        assert.equal((await session.customRequest("evaluate", { expression: "_steps", frameId: 100, context: "watch" })).result, "1457");
        await assert.rejects(Promise.resolve(session.customRequest("terminate")), /never terminates/);
        assert.equal(await attachTo("mocklab"), false, "one debugger per server");
        await stopAndWait(session);
        await until("the mock debugger to exit", () => dapLog(mark).some((l) => l.kind === "exit"));
      } finally {
        vscode.debug.removeBreakpoints([bp]);
        tracker.dispose();
      }
      assert.ok(control.includes("gc"), "old copies of reloaded mods are collected before attaching");
      const log = dapLog(mark);
      const tasklist = log.find((l) => l.kind === "wine" && /tasklist/.test(l.argv[0]));
      assert.equal(tasklist.prefix, `${lab}.pfx`);
      const start = log.find((l) => l.kind === "start");
      assert.deepEqual(start.argv.slice(-1), ["--interpreter=vscode"]);
      assert.equal(start.env.WINEPREFIX, `${lab}.pfx`);
      assert.equal(start.env.WINEDEBUG, "-all");
      assert.equal(start.env.DOTNET_SYSTEM_GLOBALIZATION_INVARIANT, "1");
      assert.equal(start.env.DISPLAY, null, "no window on any desktop");
      assert.equal(start.env.ELECTRON_RUN_AS_NODE, null);
      assert.equal(start.pgidIsOwn, true, "netcoredbg in its own process group");
      assert.equal(log.find((l) => l.command === "attach").arguments.processId, 4242, "the Windows pid from tasklist");
      assert.equal(log.find((l) => l.command === "disconnect").arguments.terminateDebuggee, false);
      assert.deepEqual(log.filter((l) => l.kind === "debuggee-killed"), []);
      assert.ok(log.some((l) => l.kind === "detached"));
    });
  });

  test("debug: Steam library server, stopped server and missing netcoredbg are refused before anything starts", async () => {
    await withMockLab(async () => {
      const steam = path.join(WS, "steamapps", "common", "EnshroudedServer");
      fs.mkdirSync(path.join(steam, "kiln"), { recursive: true });
      fs.writeFileSync(path.join(steam, "enshrouded_server.exe"), "");
      const link = path.join(WS, "labs", "link-to-steam");
      fs.rmSync(link, { force: true });
      fs.symlinkSync(steam, link);
      const target = vscode.ConfigurationTarget.Workspace;
      const servers = kilnCfg().get<object[]>("servers")!;
      await kilnCfg().update("servers", [...servers, { name: "steam", dir: steam }, { name: "steam-link", dir: link }], target);
      const mark = dapLog().length;
      const pids = api.debug.hostPids;
      api.debug.hostPids = () => [];
      assert.equal(await attachTo("steam"), false, "by name");
      assert.equal(await attachTo(steam), false, "by folder");
      assert.equal(await attachTo("steam-link"), false, "through a symlink");
      assert.equal(await attachTo("mocklab"), false, "not running");
      assert.deepEqual(dapLog(mark), [], "no wine ran for any of them");
      api.debug.hostPids = pids;
      await kilnCfg().update("debug.netcoredbg", path.join(WS, "no-such-netcoredbg.exe"), target);
      assert.equal(await attachTo("mocklab"), false, "no netcoredbg");
      assert.ok(!dapLog(mark).some((l) => l.kind === "start"), "no debugger started");
      assert.equal(vscode.debug.activeDebugSession, undefined);
    });
  });

  test("debug: the kiln CLI is the adapter when it has `kiln debug`", async () => {
    await withMockLab(async (lab) => {
      const target = vscode.ConfigurationTarget.Workspace;
      const cli = kilnCfg().get<string>("cli.path");
      await kilnCfg().update("debug.wine", undefined, target); // an explicit wine stays with the built-in proxy
      await kilnCfg().update("cli.path", process.env.KILN_IT_FAKE_KILN_DEBUG, target);
      const mark = dapLog().length;
      try {
        const marker = path.join(lab, "fake-refuse");
        fs.writeFileSync(marker, "");
        assert.equal(await attachTo("mocklab"), false, "kiln debug --json refused: no adapter starts");
        assert.deepEqual(dapLog(mark), []);
        fs.rmSync(marker);
        assert.equal(await attachTo("mocklab"), true);
        const start = await until("the CLI adapter", () => dapLog(mark).find((l) => l.kind === "start"));
        assert.deepEqual(start.argv, ["debug", "--server", lab, "--dap"]);
        assert.equal(start.env.KILN_NETCOREDBG, process.env.KILN_IT_MOCK_NETCOREDBG, "the CLI gets the debugger the extension found");
        await stopAndWait(vscode.debug.activeDebugSession!);
        assert.ok(!dapLog(mark).some((l) => l.kind === "wine"), "the CLI resolves the target itself");
      } finally {
        await kilnCfg().update("cli.path", cli, target);
      }
    });
  });

  test("servers: a folder inside a Steam library is refused for writes", async () => {
    const steam = path.join(WS, "steamapps", "common", "EnshroudedServer");
    fs.mkdirSync(path.join(steam, "kiln"), { recursive: true });
    await vscode.workspace.getConfiguration("kiln").update("servers", [{ name: "fake", dir: SERVER }, { name: "steam", dir: steam }], vscode.ConfigurationTarget.Workspace);
    const s = api.servers.list().find((x) => x.name === "steam")!;
    await api.servers.select(s);
    assert.equal(await api.servers.require("deploy"), undefined);
    assert.ok(await api.servers.require("follow kiln.log of", false));
    await api.servers.select(api.servers.list().find((x) => x.name === "fake"));
  });
});
