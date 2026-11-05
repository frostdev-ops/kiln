// SPDX-License-Identifier: MIT
// The debugger's safety rules, headless: DAP framing, the guard's policy, target resolution, and dist/dapProxy.js as a
// real process around the mock netcoredbg (test/fixtures/dap), including every way a session can end.
import * as assert from "node:assert/strict";
import { ChildProcess, spawn, spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { DapGuard, DapMessage, DapReader, encodeDap, ProxySpec, staleContextsWarning } from "../../src/core/dap";
import { DebugRefused, debugRefusal, findNetcoredbg, netcoredbgCandidates, parseTasklist, proxySpec, resolveTarget, wineAbove } from "../../src/core/debugTarget";
import type { ServerDef } from "../../src/core/servers";

const EXT = path.resolve(__dirname, "../../..");
const PROXY = path.join(EXT, "dist", "dapProxy.js");
const MOCK = path.join(EXT, "test", "fixtures", "dap", "mock-netcoredbg.js");
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kiln-vscode-dap-"));
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

suite("DAP framing", () => {
  test("frames split anywhere, several in a chunk, multi-byte bodies", () => {
    const got: DapMessage[] = [];
    const r = new DapReader((m) => got.push(m));
    const a = encodeDap({ seq: 1, type: "event", event: "output", body: { output: "é🔥" } });
    const b = encodeDap({ seq: 2, type: "event", event: "stopped" });
    const all = Buffer.concat([a, b]);
    for (let i = 0; i < all.length; i += 7) r.push(all.subarray(i, i + 7));
    assert.equal(got.length, 2);
    assert.equal(got[0].body.output, "é🔥");
    assert.equal(got[1].event, "stopped");
  });
  test("a line that is not a frame is skipped and reported", () => {
    const got: DapMessage[] = [];
    const errs: Error[] = [];
    const r = new DapReader((m) => got.push(m), (e) => errs.push(e));
    r.push(Buffer.concat([Buffer.from("wine: some noise\r\n\r\n"), encodeDap({ seq: 3, type: "event", event: "x" })]));
    assert.equal(got.length, 1);
    assert.equal(errs.length, 1);
  });
});

suite("DAP guard", () => {
  function setup(pid: number | undefined = 4242) {
    const toAdapter: DapMessage[] = [];
    const toClient: DapMessage[] = [];
    return { g: new DapGuard({ toAdapter: (m) => toAdapter.push(m), toClient: (m) => toClient.push(m) }, pid), toAdapter, toClient };
  }
  const req = (seq: number, command: string, args?: object): DapMessage => ({ seq, type: "request", command, arguments: args });

  test("attach gets the server's pid when it is missing or 0", () => {
    for (const args of [undefined, {}, { processId: 0 }, { processId: 4242 }]) {
      const { g, toAdapter } = setup();
      g.fromClient(req(1, "attach", args));
      assert.equal(toAdapter[0].arguments.processId, 4242);
      assert.ok(g.mustDisconnect);
    }
  });
  test("attach to any other pid is an error, not rewritten", () => {
    const { g, toAdapter, toClient } = setup();
    g.fromClient(req(1, "attach", { processId: 99 }));
    assert.equal(toAdapter.length, 0);
    assert.equal(toClient[0].success, false);
    assert.match(toClient[0].message!, /4242/);
    assert.equal(g.mustDisconnect, false);
  });
  test("every disconnect keeps the debuggee", () => {
    for (const args of [undefined, {}, { terminateDebuggee: true }, { terminateDebuggee: true, restart: true, suspendDebuggee: true }]) {
      const { g, toAdapter } = setup();
      g.fromClient(req(1, "disconnect", args));
      assert.deepEqual(toAdapter[0].arguments, { terminateDebuggee: false });
    }
  });
  test("what could start or end a process never reaches the debugger", () => {
    for (const cmd of ["launch", "terminate", "restart", "terminateThreads"]) {
      const { g, toAdapter, toClient } = setup();
      g.fromClient(req(1, cmd, {}));
      assert.equal(toAdapter.length, 0, cmd);
      assert.equal(toClient[0].success, false, cmd);
      assert.equal(toClient[0].request_seq, 1);
    }
  });
  test("a request named like an Object.prototype member passes through", () => {
    const { g, toAdapter } = setup();
    g.fromClient(req(1, "constructor", {}));
    assert.equal(toAdapter.length, 1);
  });
  test("the initialize response stops advertising them", () => {
    const { g, toClient } = setup();
    g.fromAdapter({ seq: 1, type: "response", request_seq: 1, command: "initialize", success: true, body: { supportsTerminateRequest: true, supportTerminateDebuggee: true, supportsRestartRequest: true, supportsConfigurationDoneRequest: true } });
    const b = toClient[0].body;
    assert.deepEqual([b.supportsTerminateRequest, b.supportTerminateDebuggee, b.supportSuspendDebuggee, b.supportsRestartRequest, b.supportsTerminateThreadsRequest], [false, false, false, false, false]);
    assert.equal(b.supportsConfigurationDoneRequest, true);
  });
  test("our own disconnect: sent once, its response stays private, detached on success only", async () => {
    const { g, toAdapter, toClient } = setup();
    g.fromClient(req(1, "attach", {}));
    const p = g.disconnect();
    const q = g.disconnect();
    const own = toAdapter.filter((m) => m.command === "disconnect");
    assert.equal(own.length, 1);
    assert.equal(own[0].arguments.terminateDebuggee, false);
    g.fromAdapter({ seq: 2, type: "response", request_seq: own[0].seq, command: "disconnect", success: true });
    assert.deepEqual(await Promise.all([p, q]), [true, true]);
    assert.equal(g.mustDisconnect, false);
    assert.equal(toClient.length, 0);
  });
  test("a failed disconnect is not a detach; the client's own one is joined, not repeated", async () => {
    const { g, toAdapter, toClient } = setup();
    g.fromClient(req(1, "attach", {}));
    g.fromClient(req(2, "disconnect", {}));
    const p = g.disconnect();
    assert.equal(toAdapter.filter((m) => m.command === "disconnect").length, 1);
    g.fromAdapter({ seq: 3, type: "response", request_seq: 2, command: "disconnect", success: false, message: "no" });
    assert.equal(await p, false);
    assert.equal(g.mustDisconnect, true);
    assert.equal(toClient.length, 1);
  });
  test("a module that comes a second time is a reload: forwarded, and the client is told", () => {
    const { g, toClient } = setup();
    const mod = (name: string, reason = "new"): DapMessage => ({ seq: 1, type: "event", event: "module", body: { reason, module: { name } } });
    g.fromAdapter(mod("SampleTests"));
    g.fromAdapter(mod("SampleLib"));
    g.fromAdapter(mod("SampleTests", "removed"));
    assert.equal(toClient.length, 3);
    g.fromAdapter(mod("SampleTests"));
    assert.equal(toClient.length, 5);
    assert.equal(toClient[3].event, "module");
    assert.equal(toClient[4].body.category, "important");
    assert.match(toClient[4].body.output, /SampleTests was reloaded/);
  });
  test("the answer of gc: a warning only when old copies stay", () => {
    assert.equal(staleContextsWarning("0 unloaded mod context(s) still alive"), undefined);
    assert.match(staleContextsWarning("2 unloaded mod context(s) still alive")!, /Restart the server/);
    assert.equal(staleContextsWarning("collected"), undefined);
  });
  test("no debugger: every request gets the reason, disconnect succeeds", () => {
    const { g, toAdapter, toClient } = setup();
    g.failure = "no netcoredbg";
    g.fromClient(req(1, "initialize", {}));
    g.fromClient(req(2, "attach", {}));
    g.fromClient(req(3, "disconnect", {}));
    assert.equal(toAdapter.length, 0);
    assert.deepEqual(toClient.map((m) => [m.success, m.message]), [[false, "no netcoredbg"], [false, "no netcoredbg"], [true, undefined]]);
  });
  test("the debugger gone: open requests are answered, disconnect does not wait", async () => {
    const { g, toClient } = setup();
    g.fromClient(req(1, "attach", {}));
    g.adapterExited("it exited");
    assert.equal(toClient[0].success, false);
    assert.equal(toClient[0].request_seq, 1);
    assert.equal(g.mustDisconnect, false);
    assert.equal(await g.disconnect(), false);
  });
});

suite("debug target", () => {
  const def = (dir: string, more: Partial<ServerDef> = {}): ServerDef => ({ name: "t", dir, kind: "lab", prefix: `${dir}.pfx`, source: "settings", ...more });

  test("tasklist: only the server's image, any case, CRLF", () => {
    const text = '"svchost.exe","20","Console","1","1,000 K"\r\n"ENSHROUDED_SERVER.exe","4242","Console","1","9,000 K"\r\nINFO: nothing\r\n';
    assert.deepEqual(parseTasklist(text), [4242]);
    assert.deepEqual(parseTasklist("INFO: No tasks are running which match the specified criteria.\r\n"), []);
  });
  test("wine is bin/wine above the preloader, never the one beside it in GE-Proton's lib", () => {
    const has = (...yes: string[]) => (p: string) => yes.includes(p);
    const ge = "/ge/files";
    assert.equal(wineAbove(`${ge}/lib/wine/i386-unix/wine64-preloader`, has(`${ge}/bin/wine`, `${ge}/lib/wine/i386-unix/wine`)), `${ge}/bin/wine`);
    assert.equal(wineAbove("/usr/lib/wine/wine64-preloader", has("/usr/bin/wine")), "/usr/bin/wine");
    assert.equal(wineAbove("/opt/w/wine-preloader", has("/opt/w/wine")), "/opt/w/wine");
    assert.equal(wineAbove("/usr/bin/sleep", has()), undefined);
  });
  test("netcoredbg lookup: setting, $KILN_NETCOREDBG, data folder; an explicit path never falls through", () => {
    assert.deepEqual(netcoredbgCandidates("/a/n.exe", { KILN_NETCOREDBG: "/b/n.exe" }), ["/a/n.exe"]);
    assert.deepEqual(netcoredbgCandidates("", { KILN_NETCOREDBG: "/b/n.exe" }), ["/b/n.exe"]);
    assert.deepEqual(netcoredbgCandidates("", { KILN_DATA_DIR: "/d" }), [path.join("/d", "tools", "netcoredbg", "netcoredbg.exe")]);
    assert.deepEqual(netcoredbgCandidates("", { XDG_DATA_HOME: "/x" }), [path.join("/x", "kiln", "tools", "netcoredbg", "netcoredbg.exe")]);
    assert.throws(() => findNetcoredbg("/no/such/netcoredbg.exe", {}), (e: DebugRefused) => e.code === "tool_missing");
  });
  test("the bundled netcoredbg comes last, and an explicit path still never falls through to it", () => {
    const d = tmp();
    const bundled = path.join(d, "bundled.exe");
    fs.writeFileSync(bundled, "");
    const none = path.join(d, "no-data-dir");
    assert.equal(findNetcoredbg("", { KILN_DATA_DIR: none }, bundled), bundled);
    assert.throws(() => findNetcoredbg(path.join(d, "missing.exe"), { KILN_DATA_DIR: none }, bundled), (e: DebugRefused) => e.code === "tool_missing");
    assert.throws(() => findNetcoredbg("", { KILN_NETCOREDBG: path.join(d, "missing.exe"), KILN_DATA_DIR: none }, bundled), (e: DebugRefused) => e.code === "tool_missing");
    const installed = path.join(d, "data", "tools", "netcoredbg", "netcoredbg.exe");
    fs.mkdirSync(path.dirname(installed), { recursive: true });
    fs.writeFileSync(installed, "");
    assert.equal(findNetcoredbg("", { KILN_DATA_DIR: path.join(d, "data") }, bundled), installed);
  });
  test("the fetch script's pin refuses any other file", () => {
    const d = tmp();
    const bogus = path.join(d, "netcoredbg-win64.zip");
    fs.writeFileSync(bogus, "not the release");
    const r = spawnSync(process.execPath, [path.join(EXT, "scripts", "fetch-netcoredbg.mjs"), "--check-zip", bogus], { encoding: "utf8" });
    assert.equal(r.status, 1);
    assert.match(r.stderr, /expected 3c410a45/);
  });
  test("Steam libraries are refused by folder, prefix and symlink", () => {
    const steam = "/home/x/.local/share/Steam/steamapps/common/EnshroudedServer";
    assert.ok(debugRefusal(def(steam)));
    assert.ok(debugRefusal(def("/srv/x", { prefix: "/g/SteamLibrary/steamapps/compatdata/2278520/pfx" })));
    const d = tmp();
    const real = path.join(d, "steamapps", "common", "S");
    fs.mkdirSync(real, { recursive: true });
    fs.symlinkSync(real, path.join(d, "link"));
    assert.ok(debugRefusal(def(path.join(d, "link"), { prefix: undefined })));
    assert.equal(debugRefusal(def(path.join(d, "lab"))), undefined);
  });
  test("a running process in a Steam library's prefix is refused whatever the configured folder says", async () => {
    const d = tmp();
    const dir = path.join(d, "lab");
    fs.mkdirSync(dir);
    const host = spawn("sleep", ["30"], { cwd: dir, env: { ...process.env, WINEPREFIX: "/g/steamapps/compatdata/2278520/pfx" }, stdio: "ignore" });
    try {
      await sleep(100);
      await assert.rejects(resolveTarget(def(dir), { hostPids: [host.pid!] }), (e: DebugRefused) => e.code === "refused");
    } finally {
      host.kill("SIGTERM");
    }
  });
  test("a stopped server: refused before any wine runs", async () => {
    const d = tmp();
    const marker = path.join(d, "wine-ran");
    const wine = path.join(d, "wine");
    fs.writeFileSync(wine, `#!/bin/sh\ntouch ${marker}\n`, { mode: 0o755 });
    await assert.rejects(resolveTarget(def(path.join(d, "lab")), { wine, hostPids: [] }), (e: DebugRefused) => e.code === "not_running");
    assert.equal(fs.existsSync(marker), false);
  });
  test("two server processes, or one from another folder: refused before any wine runs", async () => {
    const d = tmp();
    const dir = path.join(d, "lab");
    const other = path.join(d, "other");
    fs.mkdirSync(dir);
    fs.mkdirSync(other);
    const marker = path.join(d, "wine-ran");
    const wine = path.join(d, "wine");
    fs.writeFileSync(wine, `#!/bin/sh\ntouch ${marker}\n`, { mode: 0o755 });
    const elsewhere = spawn("sleep", ["30"], { cwd: other, stdio: "ignore" });
    try {
      await sleep(100);
      await assert.rejects(resolveTarget(def(dir), { wine, hostPids: [elsewhere.pid!] }), (e: DebugRefused) => e.code === "refused");
      await assert.rejects(resolveTarget(def(dir), { wine, hostPids: [elsewhere.pid!, 1] }), (e: DebugRefused) => e.code === "refused");
      assert.equal(fs.existsSync(marker), false);
    } finally {
      elsewhere.kill("SIGTERM");
    }
  });
  test("a running server: its prefix, the wine we were given, the pid from tasklist in that prefix", async () => {
    const d = tmp();
    const dir = path.join(d, "lab");
    fs.mkdirSync(dir);
    const seen = path.join(d, "seen");
    const wine = path.join(d, "wine");
    fs.writeFileSync(wine, `#!/bin/sh\necho "$WINEPREFIX $WINEDEBUG \${DISPLAY:-nodisplay} $1" > ${seen}\nprintf '"enshrouded_server.exe","32","Console","1","1 K"\\r\\n'\n`, { mode: 0o755 });
    const host = spawn("sleep", ["30"], { cwd: dir, env: { ...process.env, WINEPREFIX: `${dir}.pfx`, WINEESYNC: "1", WINESERVERSOCKET: "16", WINELOADERNOEXEC: "1" }, stdio: "ignore" });
    try {
      await sleep(100);
      const t = await resolveTarget(def(dir), { wine, hostPids: [host.pid!] });
      assert.equal(t.pid, 32);
      assert.equal(t.prefix, `${dir}.pfx`);
      assert.equal(t.env.WINEESYNC, "1", "the server's wine settings carry over");
      assert.equal(t.env.WINESERVERSOCKET, undefined, "its per-process internals do not (wine: Bad server socket)");
      assert.equal(t.env.WINELOADERNOEXEC, undefined);
      assert.equal(fs.readFileSync(seen, "utf8").trim(), `${dir}.pfx -all nodisplay tasklist.exe`);
      const spec = proxySpec(t, "/t/netcoredbg.exe");
      assert.equal(spec.command, wine);
      assert.deepEqual(spec.args, ["/t/netcoredbg.exe", "--interpreter=vscode"]);
      assert.equal(spec.env!.DOTNET_SYSTEM_GLOBALIZATION_INVARIANT, "1");
      assert.equal(spec.pid, 32);
      assert.equal(spec.cwd, "/t");
    } finally {
      host.kill("SIGTERM");
    }
  });
});

suite("dapProxy process", function () {
  this.timeout(30_000);
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  type LogLine = any;

  class Run {
    readonly log = path.join(tmp(), "dap.log");
    readonly msgs: DapMessage[] = [];
    readonly proc: ChildProcess;
    readonly exited: Promise<number | null>;
    private seq = 1;

    constructor(spec: Partial<ProxySpec> = {}, debuggerEnv: Record<string, string> = {}) {
      const full = { command: process.execPath, args: [MOCK], pid: 4242, ...spec, env: { KILN_MOCK_DAP_LOG: this.log, ...debuggerEnv, ...spec.env } };
      this.proc = spawn(process.execPath, [PROXY], { env: { ...process.env, ...debuggerEnv, KILN_DAP_SPEC: JSON.stringify(full), DISPLAY: ":99" }, stdio: ["pipe", "pipe", "pipe"] });
      const r = new DapReader((m) => this.msgs.push(m));
      this.proc.stdout!.on("data", (c: Buffer) => r.push(c));
      this.proc.stderr!.on("data", () => undefined);
      this.exited = new Promise((res) => this.proc.on("close", (code) => res(code)));
    }
    send(command: string, args?: object): number {
      const seq = this.seq++;
      this.proc.stdin!.write(encodeDap({ seq, type: "request", command, arguments: args }));
      return seq;
    }
    async until<T>(what: string, f: () => T | undefined | false, ms = 10_000): Promise<T> {
      const t0 = Date.now();
      for (;;) {
        const v = f();
        if (v) return v;
        if (Date.now() - t0 > ms) throw new Error(`timed out waiting for ${what}: ${JSON.stringify(this.msgs).slice(0, 600)}`);
        await sleep(20);
      }
    }
    req(command: string, args?: object): Promise<DapMessage> {
      const seq = this.send(command, args);
      return this.until(`${command} response`, () => this.msgs.find((m) => m.type === "response" && m.request_seq === seq));
    }
    lines(): LogLine[] {
      try {
        return fs.readFileSync(this.log, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
      } catch {
        return [];
      }
    }
    async attached(): Promise<void> {
      await this.req("initialize", { adapterID: "kiln" });
      assert.equal((await this.req("attach", {})).success, true);
      await this.until("initialized", () => this.msgs.some((m) => m.event === "initialized"));
    }
    /** The outcome every path must have: a clean detach with terminateDebuggee:false, the debuggee alive. */
    assertDetached(): void {
      const l = this.lines();
      assert.deepEqual(l.filter((x) => x.kind === "debuggee-killed"), [], "the debuggee survives");
      const d = l.filter((x) => x.command === "disconnect");
      assert.equal(d.length, 1);
      assert.equal(d[0].arguments.terminateDebuggee, false);
      const kinds = l.map((x) => x.kind);
      assert.ok(kinds.indexOf("detached") >= 0 && kinds.indexOf("detached") < kinds.indexOf("exit"), `detached before exit: ${kinds.join(" ")}`);
      assert.equal(l.find((x) => x.kind === "exit").reason, "stdin closed", "ended by us closing its stdin, never by a signal");
    }
  }

  const runs: Run[] = [];
  const start = (spec?: Partial<ProxySpec>, env?: Record<string, string>) => {
    const r = new Run(spec, env);
    runs.push(r);
    return r;
  };
  teardown(async () => {
    for (const r of runs.splice(0)) {
      r.proc.stdin!.end();
      await r.exited;
    }
  });

  test("a whole session: capabilities patched, pid filled, Stop detaches, proxy exits 0", async () => {
    const r = start();
    const init = await r.req("initialize", {});
    assert.equal(init.body.supportsTerminateRequest, false);
    assert.equal(init.body.supportTerminateDebuggee, false);
    assert.equal(init.body.supportsConfigurationDoneRequest, true, "the rest passes through");
    await r.req("attach", { processId: 0 });
    assert.equal((await r.req("terminate", {})).success, false);
    assert.equal((await r.req("launch", {})).success, false);
    assert.equal((await r.req("attach", { processId: 7 })).success, false);
    assert.equal((await r.req("disconnect", { terminateDebuggee: true })).success, true);
    assert.equal(await r.exited, 0);
    r.assertDetached();
    const l = r.lines();
    assert.equal(l.filter((x) => x.command === "attach").length, 1, "the bad attach never reached it");
    assert.equal(l.find((x) => x.command === "attach").arguments.processId, 4242);
    const s = l.find((x) => x.kind === "start");
    assert.equal(s.pgidIsOwn, true);
    assert.equal(s.env.DISPLAY, null);
    assert.equal(s.env.ELECTRON_RUN_AS_NODE, null);
  });

  test("the client vanishes (stdin closes) while attached: detaches and waits for the answer", async () => {
    const r = start({}, { KILN_MOCK_DAP_SLOW_DISCONNECT_MS: "400" });
    await r.attached();
    r.proc.stdin!.end();
    assert.equal(await r.exited, 0);
    r.assertDetached();
  });

  for (const sig of ["SIGTERM", "SIGINT", "SIGHUP"] as const) {
    test(`${sig} to the proxy while attached: detaches, the debugger never sees the signal`, async () => {
      const r = start({}, { KILN_MOCK_DAP_SLOW_DISCONNECT_MS: "200" });
      await r.attached();
      r.proc.kill(sig);
      assert.equal(await r.exited, 0);
      r.assertDetached();
    });
  }

  test("stdin closes during a client disconnect: one disconnect, not two", async () => {
    const r = start({}, { KILN_MOCK_DAP_SLOW_DISCONNECT_MS: "300" });
    await r.attached();
    r.send("disconnect", {});
    await r.until("disconnect at the debugger", () => r.lines().some((x) => x.command === "disconnect"));
    r.proc.stdin!.end();
    assert.equal(await r.exited, 0);
    r.assertDetached();
  });

  test("not attached: the proxy goes without a disconnect", async () => {
    const r = start();
    await r.req("initialize", {});
    r.proc.stdin!.end();
    assert.equal(await r.exited, 0);
    assert.deepEqual(r.lines().filter((x) => x.command === "disconnect" || x.kind === "debuggee-killed"), []);
  });

  test("no answer to disconnect: it gives up after the timeout rather than hanging", async () => {
    const r = start({}, { KILN_MOCK_DAP_SLOW_DISCONNECT_MS: "60000", KILN_DAP_DISCONNECT_TIMEOUT_MS: "300" });
    await r.attached();
    r.proc.kill("SIGTERM");
    assert.equal(await Promise.race([r.exited, sleep(10_000).then(() => "hung")]), 0);
  });

  test("the debugger dies attached: the client is told and the session ends", async () => {
    const r = start({}, { KILN_MOCK_DAP_CRASH: "attach" });
    await r.req("initialize", {});
    r.send("attach", {});
    assert.equal(await r.exited, 0);
    assert.ok(r.msgs.some((m) => m.event === "terminated"));
    assert.ok(r.msgs.some((m) => m.event === "output" && /gone down/.test(m.body.output)), "the console says what it means for the server");
  });

  test("the debugger cannot start: every request gets the reason, until the client goes", async () => {
    const r = start({ command: "/no/such/wine", args: ["netcoredbg.exe"] });
    const init = await r.req("initialize", {});
    assert.equal(init.success, false);
    assert.match(init.message!, /\/no\/such\/wine/);
    const attach = await r.req("attach", {});
    assert.equal(attach.success, false);
    assert.equal((await r.req("disconnect", {})).success, true);
    r.proc.stdin!.end();
    assert.equal(await r.exited, 0);
  });
});
