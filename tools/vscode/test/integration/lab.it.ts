// SPDX-License-Identifier: MIT
// Lab suite (KILN_IT_LAB=<lab dir>): the extension against a real Kiln server in a dev lab. The workspace holds the
// repository's SampleTests/SampleLib; the lab has Kiln, Kiln.Admin and the loader samples deployed.
// Starts the lab through the extension when it is down (tools/lab/lab.sh), deploys + reloads + runs sample-tests
// through the Testing view, reloads through the control folder, talks KAP/1 to the real Kiln.Admin, checks kiln.log
// links, and stops the lab through the extension at the end.
import * as assert from "node:assert/strict";
import { ChildProcess, spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { ControlResult, sendControl } from "../../src/core/control";
import { DapMessage, DapReader, encodeDap } from "../../src/core/dap";
import { proxySpec, resolveTarget } from "../../src/core/debugTarget";
import { serverPids } from "../../src/core/servers";
import type { KilnApi } from "../../src/extension";
import { ConsoleTerminal } from "../../src/features/console";

const LAB = process.env.KILN_IT_LAB!;
/**
 * A real netcoredbg.exe, or "bundled" for the copy in dist/netcoredbg with no setting at all: set only with the user's
 * OK, and only ever for this lab. Without it those tests are skipped.
 */
const NETCOREDBG = process.env.KILN_IT_NETCOREDBG;
const BUNDLED = NETCOREDBG === "bundled";
const realAttach = NETCOREDBG ? test : test.skip;
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
const say = (s: string) => console.log(`    [lab] ${s}`);

async function until<T>(what: string, f: () => T | undefined | false | Promise<T | undefined | false>, ms = 30_000): Promise<T> {
  const t0 = Date.now();
  for (;;) {
    const v = await f();
    if (v) return v;
    if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(250);
  }
}

suite("Kiln extension against a lab", function () {
  this.timeout(600_000);
  let api: KilnApi;
  const server = () => api.servers.list().find((s) => path.resolve(s.dir) === path.resolve(LAB))!;
  const cfg = () => vscode.workspace.getConfiguration("kiln");
  const sourceFile = () => path.join(process.env.KILN_IT_REPO!, "mods", "samples", "SampleTests", "SampleTestsMod.cs");
  /** 0-based: the first statement of Imc_round_trip, which the test command below runs on the tick thread. */
  const bpLine = () => {
    const lines = fs.readFileSync(sourceFile(), "utf8").split("\n");
    const start = lines.findIndex((l) => l.includes("Imc_round_trip"));
    return start + lines.slice(start).findIndex((l) => l.includes("_replies.Clear()"));
  };
  /** `test` on the control folder; an auto-run after a reload may still be going, so it retries that answer. */
  async function runImcTest(): Promise<ControlResult> {
    for (;;) {
      const r = await sendControl(LAB, "test sample-tests Imc_round_trip", { timeoutMs: 240_000 });
      if (r.ok || !/already running/.test(r.message)) return r;
      await sleep(1000);
    }
  }
  /** The same server process, and it still answers on its tick thread. */
  async function assertAlive(label: string, pids: number[]): Promise<void> {
    assert.deepEqual(serverPids(server()), pids, `${label}: the server process is still the same one`);
    const r = await sendControl(LAB, "mods", { timeoutMs: 60_000 });
    assert.equal(r.ok, true, `${label}: the server answers on its tick thread`);
    say(`${label}: server pid ${pids.join(",")} still running, control answered "mods"`);
  }

  suiteSetup(async () => {
    api = await vscode.extensions.getExtension<KilnApi>("kiln.kiln-vscode")!.activate();
    const s = server();
    assert.ok(s, "the lab is a configured server");
    assert.equal(s.kind, "lab");
    await api.servers.select(s);
    api.servers.refresh();
    await sleep(1500);
    if (!api.servers.state(s)?.running) {
      say("lab is down: Kiln: Start Test Server (tools/lab/lab.sh start)");
      await vscode.commands.executeCommand("kiln.server.start", s);
    }
    await until("the lab running with a live KAP endpoint", async () => {
      api.servers.refresh();
      await sleep(1000);
      const st = api.servers.state(s);
      return st?.running && st.kap?.live ? st : undefined;
    }, 120_000);
    const st = api.servers.state(s)!;
    say(`running: pid ${st.pids.join(",")}, KAP ${st.kap!.host}:${st.kap!.port}`);
  });

  test("control folder: mods lists the loaded samples", async () => {
    const r = await until("mods via the control folder", async () => {
      try {
        const res = await sendControl(LAB, "mods", { timeoutMs: 10_000 });
        const list = (res.data as { id: string; state: string }[]) ?? [];
        return list.some((m) => m.id === "sample-tests" && m.state.toLowerCase() === "loaded") ? res : undefined;
      } catch {
        return undefined;
      }
    }, 120_000);
    const list = r.data as { id: string; version: string; state: string }[];
    say(`mods: ${list.filter((m) => m.state.toLowerCase() === "loaded").map((m) => `${m.id} ${m.version}`).join(", ")}`);
  });

  test("Testing view: deploy + reload + run sample-tests (real server)", async () => {
    await api.testing.discover();
    const item = api.testing.controller.items.get("sample-tests");
    assert.ok(item, "sample-tests discovered");
    const cts = new vscode.CancellationTokenSource();
    const t0 = Date.now();
    await api.testing.run(new vscode.TestRunRequest([item]), cts.token, true);
    const res = Object.fromEntries(api.testing.lastResults);
    say(`run in ${((Date.now() - t0) / 1000).toFixed(1)} s: ${JSON.stringify(res)}`);
    const outcomes = Object.values(res);
    assert.ok(outcomes.length >= 10, `${outcomes.length} results`);
    assert.ok(!outcomes.includes("fail") && !outcomes.includes("error"), JSON.stringify(res));
    assert.equal(res["sample-tests::SampleTestsMod.Skipped_example"], "skip");
    const report = JSON.parse(fs.readFileSync(path.join(LAB, "kiln", "logs", "tests", "sample-tests.json"), "utf8"));
    say(`report: ${report.passed} passed, ${report.failed} failed, ${report.skipped} skipped, started ${report.started}`);
    assert.ok(Date.parse(report.started) >= t0 - 5000, "the report is from this run");
  });

  test("single test: exactly one result", async () => {
    const cls = api.testing.controller.items.get("sample-tests")!.children.get("sample-tests::SampleTestsMod")!;
    const one = cls.children.get("sample-tests::SampleTestsMod.Imc_round_trip")!;
    await api.testing.run(new vscode.TestRunRequest([one]), new vscode.CancellationTokenSource().token, false);
    assert.deepEqual(Object.fromEntries(api.testing.lastResults), { "sample-tests::SampleTestsMod.Imc_round_trip": "pass" });
  });

  test("reload through the extension", async () => {
    const mod = api.mods.all().find((m) => m.id === "sample-tests")!;
    assert.equal(await api.mods.reload(mod), true);
  });

  test("KAP/1 console against the real Kiln.Admin", async () => {
    const term = new ConsoleTerminal(api.ctx, server());
    let out = "";
    term.onDidWrite((s) => (out += s));
    term.open();
    await until("banner", () => out.includes(" ops)"), 15_000);
    say(`banner: ${out.split("\r\n").find((l) => l.includes(" ops)"))?.replace(/\x1b\[[0-9;]*m/g, "")}`);
    term.handleInput(".op status\r");
    await until("status", () => /ok\S* \S*status/.test(out.replace(/\x1b\[[0-9;]*m/g, "")), 15_000);
    const n = out.length;
    term.handleInput("help\r");
    await until("help output", () => out.length > n + 40 && /\n/.test(out.slice(n)), 15_000);
    say(`help: ${out.slice(n).replace(/\x1b\[[0-9;]*m/g, "").split("\r\n").filter(Boolean).slice(1, 4).join(" | ")}`);
    term.handleInput(".op mods\r");
    await until("mods op", () => out.includes("sample-tests"), 15_000);
    term.handleInput("\x04");
  });

  test("kiln.log: highlighting language and links to the workspace mod", async () => {
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(path.join(LAB, "kiln", "logs", "kiln.log")));
    assert.equal(doc.languageId, "kiln-log");
    const links = await vscode.commands.executeCommand<vscode.DocumentLink[]>("vscode.executeLinkProvider", doc.uri);
    const mod = links.find((l) => l.target?.fsPath.includes(path.join("SampleTests")));
    assert.ok(mod, "a [mod:sample-tests] link to the workspace project");
    const tests = links.filter((l) => l.target?.fsPath.endsWith("SampleTestsMod.cs"));
    say(`${links.length} links; ${tests.length} PASS/FAIL lines link to SampleTestsMod.cs`);
    assert.ok(tests.length >= 9);
  });

  test("debug target: the lab's own wine, prefix and Windows pid (tasklist only, no debugger)", async () => {
    const pids = serverPids(server());
    const t = await resolveTarget(server(), { log: say });
    say(`target: Windows pid ${t.pid} (Linux ${pids.join(",")}), wine ${t.wine}, prefix ${t.prefix}`);
    assert.equal(t.prefix, `${LAB}.pfx`);
    assert.match(t.wine!, /\/bin\/wine$/);
    assert.ok(t.pid > 0);
    await assertAlive("after tasklist", pids);
  });

  test("debug: no netcoredbg: nothing attaches, the reason is shown, the server keeps running", async () => {
    const pids = serverPids(server());
    const target = vscode.ConfigurationTarget.Workspace;
    await cfg().update("debug.netcoredbg", "/nonexistent/netcoredbg.exe", target);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const seen: any[] = [];
    const tracker = vscode.debug.registerDebugAdapterTrackerFactory("kiln", { createDebugAdapterTracker: () => ({ onDidSendMessage: (m) => seen.push(m) }) });
    try {
      const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], { type: "kiln", request: "attach", name: "no netcoredbg", server: LAB });
      assert.equal(started, false);
      assert.deepEqual(seen, [], "refused before an adapter started");
      // the CLI's own backstop: --dap still speaks DAP when it cannot attach
      if ((await api.ctx.hasCli()) && (await api.ctx.exec(api.ctx.cliPath(), ["debug", "--help"], { timeoutMs: 5000 })).code === 0) {
        const r = await api.ctx.exec(api.ctx.cliPath(), ["debug", "--server", LAB, "--dap"], {
          env: { ...process.env, KILN_NETCOREDBG: "/nonexistent/netcoredbg.exe" },
          input: encodeDap({ seq: 1, type: "request", command: "initialize", arguments: {} }).toString(),
          timeoutMs: 30_000,
        });
        const frames: DapMessage[] = [];
        new DapReader((m) => frames.push(m)).push(Buffer.from(r.stdout));
        say(`kiln debug --dap without netcoredbg: success=${frames[0]?.success}: ${frames[0]?.message}`);
        assert.equal(frames[0]?.success, false);
        assert.match(frames[0]?.message ?? "", /netcoredbg/);
      }
    } finally {
      tracker.dispose();
      await cfg().update("debug.netcoredbg", undefined, target);
    }
    await until("no debug session", () => !vscode.debug.activeDebugSession);
    await assertAlive("after the refusal", pids);
  });

  async function vscodeSession(endStopped: boolean): Promise<void> {
    const pids = serverPids(server());
    const target = vscode.ConfigurationTarget.Workspace;
    await cfg().update("debug.netcoredbg", BUNDLED ? undefined : NETCOREDBG, target);
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const seen: any[] = [];
    const tracker = vscode.debug.registerDebugAdapterTrackerFactory("kiln", { createDebugAdapterTracker: () => ({ onDidSendMessage: (m) => seen.push(m) }) });
    const line = bpLine();
    const bp = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(sourceFile()), new vscode.Position(line, 0)));
    vscode.debug.addBreakpoints([bp]);
    let testRun: Promise<ControlResult> | undefined;
    const t0 = Date.now();
    try {
      const started = await vscode.debug.startDebugging(vscode.workspace.workspaceFolders![0], { type: "kiln", request: "attach", name: "real attach", server: LAB });
      assert.equal(started, true, JSON.stringify(seen.filter((m) => m.success === false)));
      const session = vscode.debug.activeDebugSession!;
      await until("the breakpoint to be verified", () => seen.some((m) => (m.command === "setBreakpoints" && m.body?.breakpoints?.some((b: { verified: boolean }) => b.verified)) || (m.event === "breakpoint" && m.body?.breakpoint?.verified)), 120_000);
      say(`attached, breakpoint verified at SampleTestsMod.cs:${line + 1} after ${((Date.now() - t0) / 1000).toFixed(1)} s`);
      testRun = runImcTest();
      const stop = await until("the breakpoint hit", () => seen.find((m) => m.event === "stopped"), 120_000);
      const threadId = stop.body.threadId;
      const st = await session.customRequest("stackTrace", { threadId, levels: 6 });
      const frames: { name: string; line: number; source?: { path?: string } }[] = st.stackFrames;
      say(`stopped (${stop.body.reason}); stack: ${frames.map((f) => `${f.name} (${path.basename(f.source?.path ?? "?")}:${f.line})`).join(" <- ")}`);
      say(`top frame source path as the debugger reports it: ${frames[0].source?.path}`);
      assert.match(frames[0].name, /Imc_round_trip/);
      assert.equal(frames[0].line, line + 1);
      assert.equal(frames[0].source?.path, sourceFile(), "the frame opens the workspace file");
      const scopes = await session.customRequest("scopes", { frameId: (st.stackFrames[0] as { id: number }).id });
      const vars = await session.customRequest("variables", { variablesReference: scopes.scopes[0].variablesReference });
      say(`locals: ${vars.variables.slice(0, 5).map((v: { name: string; value: string }) => `${v.name}=${v.value}`).join(", ")}`);
      const ev = await session.customRequest("evaluate", { expression: "1 + 41", frameId: (st.stackFrames[0] as { id: number }).id, context: "watch" });
      say(`evaluate 1 + 41 = ${ev.result}`);
      assert.equal(ev.result, "42");
      const field = await Promise.resolve(session.customRequest("evaluate", { expression: "_replies.Count", frameId: (st.stackFrames[0] as { id: number }).id, context: "watch" })).then((r) => r.result, (e) => `error: ${e.message}`);
      say(`evaluate _replies.Count (a field, in an async method's state machine) = ${field}`);
      vscode.debug.removeBreakpoints([bp]);
      if (!endStopped) {
        await sleep(1000);
        await session.customRequest("continue", { threadId });
        const r = await testRun;
        say(`after continue: test command ok=${r.ok}: ${r.message}`);
        assert.equal(r.ok, true, r.message);
      }
      await vscode.debug.stopDebugging(session);
      await until("the session to end", () => !vscode.debug.activeDebugSession, 150_000);
      say(`session ended (${endStopped ? "Stop while stopped at the breakpoint" : "Stop while running"})`);
      const r = await testRun;
      assert.equal(r.ok, true, r.message);
      await sleep(3000);
      await assertAlive("after detach", pids);
    } finally {
      vscode.debug.removeBreakpoints([bp]);
      tracker.dispose();
      if (vscode.debug.activeDebugSession) await vscode.debug.stopDebugging(vscode.debug.activeDebugSession);
      await until("the session to end", () => !vscode.debug.activeDebugSession, 150_000).catch((e) => say(`${e}`));
      await cfg().update("debug.netcoredbg", undefined, target);
    }
  }

  realAttach("real attach through VS Code: breakpoint, stack, locals, evaluate, continue, Stop", () => vscodeSession(false));
  realAttach("real attach through VS Code: Stop while stopped at a breakpoint", () => vscodeSession(true));

  /** A client of our own, so it can vanish the way a crashed window does. Never SIGKILL: it would take the server down. */
  async function vanishingClient(how: "stdin closed" | "SIGTERM"): Promise<void> {
    const pids = serverPids(server());
    const dbg = BUNDLED ? api.ctx.asset("dist", "netcoredbg", "netcoredbg.exe") : NETCOREDBG!;
    const useCli = (await api.ctx.hasCli()) && (await api.ctx.exec(api.ctx.cliPath(), ["debug", "--help"], { timeoutMs: 5000 })).code === 0;
    if (!useCli) await sendControl(LAB, "gc", { timeoutMs: 30_000 }); // what the extension does before the built-in proxy; the CLI does it itself
    const proc: ChildProcess = useCli
      ? spawn(api.ctx.cliPath(), ["debug", "--server", LAB, "--dap"], { env: { ...process.env, KILN_NETCOREDBG: dbg }, stdio: ["pipe", "pipe", "pipe"] })
      : spawn(process.execPath, [api.ctx.asset("dist", "dapProxy.js")], {
          env: { ...process.env, ELECTRON_RUN_AS_NODE: "1", KILN_DAP_SPEC: JSON.stringify(proxySpec(await resolveTarget(server()), dbg)) },
          stdio: ["pipe", "pipe", "pipe"],
        });
    say(`adapter: ${useCli ? "kiln debug --dap" : "built-in proxy"}`);
    const exited = new Promise<number | null>((res) => proc.on("close", (c) => res(c)));
    const msgs: DapMessage[] = [];
    const reader = new DapReader((m) => msgs.push(m));
    proc.stdout!.on("data", (c: Buffer) => reader.push(c));
    proc.stderr!.on("data", () => undefined);
    let seq = 1;
    const req = (command: string, args: object, ms = 120_000) => {
      const s = seq++;
      proc.stdin!.write(encodeDap({ seq: s, type: "request", command, arguments: args }));
      return until(`${command} response`, () => msgs.find((m) => m.type === "response" && m.request_seq === s), ms);
    };
    let testRun: Promise<ControlResult> | undefined;
    let vanished = false;
    try {
      await req("initialize", { adapterID: "kiln", linesStartAt1: true, columnsStartAt1: true, pathFormat: "path" });
      assert.equal((await req("attach", {})).success, true);
      await until("initialized", () => msgs.some((m) => m.event === "initialized"), 120_000);
      await req("setBreakpoints", { source: { path: sourceFile() }, breakpoints: [{ line: bpLine() + 1 }] });
      await req("configurationDone", {});
      await until("the breakpoint to be verified", () => msgs.some((m) => m.event === "breakpoint" && m.body?.breakpoint?.verified), 120_000);
      say("attached; breakpoint verified");
      testRun = runImcTest();
      await until("the breakpoint hit", () => msgs.some((m) => m.event === "stopped"), 120_000);
      say(`stopped at the breakpoint; the client now goes away (${how})`);
      vanished = true;
      if (how === "SIGTERM") proc.kill("SIGTERM");
      else proc.stdin!.end();
      const code = await Promise.race([exited, sleep(150_000).then(() => "still running after 150 s")]);
      say(`adapter exit: ${code}`);
      assert.equal(code, 0);
      const r = await testRun;
      say(`the server resumed: test command ok=${r.ok}: ${r.message}`);
      assert.equal(r.ok, true, r.message);
      await sleep(3000);
      await assertAlive(`after the client vanished (${how})`, pids);
    } finally {
      if (!vanished) {
        proc.stdin!.end(); // detaches; never a kill
        await Promise.race([exited, sleep(150_000)]);
      }
    }
  }

  realAttach("real attach: the client vanishes (stdin closes) while stopped at a breakpoint", () => vanishingClient("stdin closed"));
  realAttach("real attach: SIGTERM to the adapter while stopped at a breakpoint", () => vanishingClient("SIGTERM"));

  test("stop the lab through the extension (tools/lab/lab.sh stop)", async () => {
    await vscode.commands.executeCommand("kiln.server.stop", server());
    await until("stopped", async () => {
      api.servers.refresh();
      await sleep(1000);
      return api.servers.state(server())?.running === false;
    }, 120_000);
    say("lab stopped");
  });
});
