// SPDX-License-Identifier: MIT
// A stand-in for netcoredbg --interpreter=vscode (tests only). Speaks enough DAP for an attach session, advertises what
// the real one must not be allowed to do, and records everything as JSON lines in $KILN_MOCK_DAP_LOG, including the one
// outcome that matters: "debuggee-killed", written when it goes away attached without a clean disconnect (what
// DebugActiveProcess does to a real server). KILN_MOCK_DAP_SLOW_DISCONNECT_MS delays the disconnect response,
// KILN_MOCK_DAP_CRASH=attach dies right after attaching.
const fs = require("node:fs");

const LOG = process.env.KILN_MOCK_DAP_LOG;
const log = (o) => LOG && fs.appendFileSync(LOG, JSON.stringify(o) + "\n");
log({
  kind: "start",
  argv: process.argv.slice(2),
  pgidIsOwn: (() => {
    try {
      return fs.readFileSync(`/proc/${process.pid}/stat`, "utf8").split(") ")[1].split(" ")[2] === String(process.pid);
    } catch {
      return null;
    }
  })(),
  env: Object.fromEntries(["WINEPREFIX", "WINEDEBUG", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "DISPLAY", "WAYLAND_DISPLAY", "ELECTRON_RUN_AS_NODE", "KILN_NETCOREDBG"].map((k) => [k, process.env[k] ?? null])),
});

let seq = 1;
let attached = false;
let detached = false;
let breakpoints = [];
let bpPath;
const send = (m) => {
  const body = Buffer.from(JSON.stringify({ seq: seq++, ...m }));
  process.stdout.write(`Content-Length: ${body.length}\r\n\r\n`);
  process.stdout.write(body);
};
const event = (name, body) => send({ type: "event", event: name, body });
const ok = (req, body) => send({ type: "response", request_seq: req.seq, command: req.command, success: true, body });

function gone(reason) {
  if (attached && !detached) log({ kind: "debuggee-killed", reason });
  log({ kind: "exit", reason });
  process.exit(0);
}
process.on("SIGTERM", () => gone("SIGTERM"));
process.on("SIGINT", () => gone("SIGINT"));
process.on("SIGHUP", () => gone("SIGHUP"));

function handle(req) {
  log({ kind: "request", command: req.command, arguments: req.arguments });
  switch (req.command) {
    case "initialize":
      return ok(req, { supportsConfigurationDoneRequest: true, supportTerminateDebuggee: true, supportsTerminateRequest: true, supportsRestartRequest: true });
    case "attach":
      attached = true;
      ok(req);
      if (process.env.KILN_MOCK_DAP_CRASH === "attach") return gone("crash");
      return event("initialized");
    case "setBreakpoints":
      breakpoints = req.arguments.breakpoints ?? [];
      bpPath = req.arguments.source?.path ?? bpPath;
      return ok(req, { breakpoints: breakpoints.map((b, i) => ({ id: i + 1, verified: true, line: b.line })) });
    case "configurationDone":
      ok(req);
      if (breakpoints.length) event("stopped", { reason: "breakpoint", threadId: 1, allThreadsStopped: true });
      return undefined;
    case "threads":
      return ok(req, { threads: [{ id: 1, name: "tick" }] });
    case "stackTrace":
      return ok(req, {
        stackFrames: [{ id: 100, name: "FrostMod.OnPre()", line: breakpoints[0]?.line ?? 1, column: 1, source: { path: bpPath, name: "x.cs" } }],
        totalFrames: 1,
      });
    case "scopes":
      return ok(req, { scopes: [{ name: "Locals", variablesReference: 7, expensive: false }] });
    case "variables":
      return ok(req, { variables: [{ name: "_steps", value: "1457", variablesReference: 0 }] });
    case "evaluate":
      return ok(req, { result: "1457", variablesReference: 0 });
    case "continue":
      ok(req, { allThreadsContinued: true });
      return event("continued", { threadId: 1, allThreadsContinued: true });
    case "disconnect": {
      const finish = () => {
        if (req.arguments?.terminateDebuggee !== false) log({ kind: "debuggee-killed", reason: `disconnect terminateDebuggee=${req.arguments?.terminateDebuggee}` });
        detached = true;
        log({ kind: "detached" });
        ok(req);
      };
      const slow = Number(process.env.KILN_MOCK_DAP_SLOW_DISCONNECT_MS);
      return slow ? void setTimeout(finish, slow) : finish();
    }
    case "launch":
    case "terminate":
    case "restart":
      log({ kind: "debuggee-killed", reason: `${req.command} reached the debugger` });
      return ok(req);
    default:
      return ok(req, {});
  }
}

let buf = Buffer.alloc(0);
process.stdin.on("data", (c) => {
  buf = Buffer.concat([buf, c]);
  for (;;) {
    const end = buf.indexOf("\r\n\r\n");
    if (end < 0) return;
    const n = Number(/content-length:\s*(\d+)/i.exec(buf.subarray(0, end).toString())[1]);
    if (buf.length < end + 4 + n) return;
    const msg = JSON.parse(buf.subarray(end + 4, end + 4 + n).toString());
    buf = buf.subarray(end + 4 + n);
    handle(msg);
  }
});
process.stdin.on("end", () => gone("stdin closed"));
