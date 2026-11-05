// SPDX-License-Identifier: MIT
// The built-in debug adapter: a small stdio DAP proxy between VS Code and netcoredbg (run as dist/dapProxy.js with the
// spec in $KILN_DAP_SPEC). `kiln debug --dap` does the same job when the CLI is installed.
//
// It is a process of its own, not code in the extension host, because the debugger must outlive whatever ends the
// session: a window reload or an extension host crash only closes our stdin, and we can still detach cleanly.
// netcoredbg runs in its own process group and is never signalled from here: netcoredbg dying while attached takes the
// server down (core/dap.ts). Hence no kill anywhere in this file. A SIGKILL to netcoredbg itself cannot be handled
// (see docs/kiln/modding/vscode.md).
import { ChildProcess, spawn } from "node:child_process";
import { DapGuard, DapMessage, DapReader, encodeDap, ProxySpec, scrubWineEnv } from "./core/dap";

const DISCONNECT_TIMEOUT_MS = Number(process.env.KILN_DAP_DISCONNECT_TIMEOUT_MS) || 120_000;
const DEBUGGER_EXIT_TIMEOUT_MS = 20_000;

const send = (m: DapMessage) => {
  try {
    process.stdout.write(encodeDap(m));
  } catch {
    /* the client is gone; detaching does not depend on it */
  }
};
process.stdout.on("error", () => undefined);
process.stdin.on("error", () => undefined);

let child: ChildProcess | undefined;
const childAlive = () => !!child && child.exitCode === null && child.signalCode === null;

const spec = JSON.parse(process.env.KILN_DAP_SPEC || "{}") as Partial<ProxySpec>;
const guard = new DapGuard(
  {
    toClient: send,
    toAdapter: (m) => {
      if (childAlive()) child!.stdin!.write(encodeDap(m));
    },
  },
  spec.pid,
);

/** Closing netcoredbg's stdin after the detach ends it; if it lingers we leave it alone and go. */
function endDebugger(): void {
  child?.stdin?.end();
  setTimeout(() => process.exit(0), DEBUGGER_EXIT_TIMEOUT_MS);
}
guard.onDetached = endDebugger;

let closing = false;
async function shutdown(why: string): Promise<void> {
  if (closing) return;
  closing = true;
  if (!childAlive()) process.exit(0);
  if (guard.mustDisconnect) {
    process.stderr.write(`kiln dap proxy: ${why}: detaching from the server\n`);
    const detached = await Promise.race([
      guard.disconnect(),
      new Promise<boolean>((r) => setTimeout(() => r(false), DISCONNECT_TIMEOUT_MS)),
    ]);
    if (!detached) process.stderr.write(`kiln dap proxy: no answer to disconnect after ${DISCONNECT_TIMEOUT_MS} ms; the server may go down\n`);
    if (detached) return; // onDetached already ends the debugger
  }
  endDebugger();
}

for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"] as const) process.on(sig, () => void shutdown(sig));
process.on("uncaughtException", (e) => {
  process.stderr.write(`kiln dap proxy: ${e.stack ?? e}\n`);
  void shutdown("internal error");
});

if (!spec.command) {
  guard.failure = "Kiln debug proxy: no debugger command ($KILN_DAP_SPEC).";
} else {
  // never a window on the user's desktop; nothing of VS Code's Node mode for the debugger
  const env: NodeJS.ProcessEnv = { ...scrubWineEnv({ ...process.env }), ...spec.env };
  for (const k of ["ELECTRON_RUN_AS_NODE", "KILN_DAP_SPEC"]) delete env[k];
  try {
    child = spawn(spec.command, spec.args ?? [], { env, cwd: spec.cwd, stdio: ["pipe", "pipe", "pipe"], detached: true, windowsHide: true });
  } catch (e) {
    guard.failure = `Could not start the debugger (${spec.command}): ${(e as Error).message}`;
  }
}

if (child) {
  child.on("error", (e) => {
    guard.failure = `Could not start the debugger (${spec.command}): ${e.message}`;
    guard.adapterExited(guard.failure);
    if (closing) process.exit(0);
  });
  child.stdin!.on("error", () => undefined);
  const fromAdapter = new DapReader(
    (m) => guard.fromAdapter(m),
    (e) => process.stderr.write(`kiln dap proxy: from the debugger: ${e.message}\n`),
  );
  child.stdout!.on("data", (c: Buffer) => fromAdapter.push(c));
  child.stderr!.setEncoding("utf8").on("data", (text: string) => {
    process.stderr.write(text);
    guard.event("output", { category: "console", output: text });
  });
  child.on("close", (code, signal) => {
    if (guard.failure) return; // it never started: stay and answer every request with the reason
    const how = signal ? `signal ${signal}` : `code ${code}`;
    const attached = guard.mustDisconnect;
    guard.adapterExited(`The debugger exited (${how}) before answering.`);
    if (attached) {
      guard.event("output", { category: "important", output: `The debugger exited (${how}) while attached without detaching: the server has probably gone down too. Check kiln.log.\n` });
    }
    if (!guard.detached) guard.event("terminated");
    process.exit(0);
  });
}

const fromClient = new DapReader(
  (m) => guard.fromClient(m),
  (e) => process.stderr.write(`kiln dap proxy: from the client: ${e.message}\n`),
);
process.stdin.on("data", (c: Buffer) => fromClient.push(c));
process.stdin.on("end", () => void shutdown("the client closed the session"));
process.stdin.on("close", () => void shutdown("the client closed the session"));
