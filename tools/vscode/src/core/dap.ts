// SPDX-License-Identifier: MIT
// Debug Adapter Protocol: framing, and the guard between VS Code and netcoredbg (no VS Code imports, unit-tested).
//
// Why a guard: netcoredbg attaches with DebugActiveProcess semantics, so a debugger that goes away without a clean
// detach takes the debuggee down with it. The debuggee is a game server. So:
// - disconnect always carries terminateDebuggee:false, whatever the client asked for;
// - launch, terminate, restart and terminateThreads never reach netcoredbg (error responses), and the initialize
//   response stops advertising them, so VS Code's Stop goes straight to disconnect;
// - attach gets the server's Windows pid; another non-zero pid is an error, not rewritten (same rule as `kiln debug --dap`);
// - DapGuard.disconnect() lets the proxy detach on its own when the client goes away.

export interface DapMessage {
  seq?: number;
  type: string;
  command?: string;
  event?: string;
  request_seq?: number;
  success?: boolean;
  message?: string;
  // the protocol's arguments and bodies are per-command JSON
  arguments?: any; // eslint-disable-line @typescript-eslint/no-explicit-any
  body?: any; // eslint-disable-line @typescript-eslint/no-explicit-any
}

/** How dapProxy starts the debugger (passed as JSON in $KILN_DAP_SPEC). */
export interface ProxySpec {
  command: string;
  args: string[];
  env?: Record<string, string>;
  cwd?: string;
  /** the server's Windows pid */
  pid?: number;
}

/** Removes what would point a wine call at another runner, prefix or display (the spec then sets what it needs). */
export function scrubWineEnv(env: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  for (const k of ["WINEPREFIX", "WINESERVER", "WINELOADER", "WINESERVERSOCKET", "WINEDLLPATH", "WINEARCH", "WINEDLLOVERRIDES", "WINEDEBUG", "STEAM_COMPAT_DATA_PATH", "STEAM_COMPAT_CLIENT_INSTALL_PATH", "DISPLAY", "WAYLAND_DISPLAY"]) {
    delete env[k];
  }
  return env;
}

/**
 * The answer of the control command `gc`, "<n> unloaded mod context(s) still alive": a warning when old copies of a
 * reloaded mod stay loaded. Breakpoints can bind to such a copy and never stop (same rule as `kiln debug`).
 */
export function staleContextsWarning(gcMessage: string): string | undefined {
  const n = Number(gcMessage.trim().split(/\s+/)[0]);
  return n > 0 ? `${gcMessage}: old copies of a reloaded mod are still loaded. Breakpoints may not stop, and a session in this state can take the server down. Restart the server before debugging.` : undefined;
}

export function encodeDap(m: DapMessage): Buffer {
  const body = Buffer.from(JSON.stringify(m), "utf8");
  return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "ascii"), body]);
}

/** Reassembles Content-Length frames from arbitrary chunks (lengths are bytes, so it works on Buffers). */
export class DapReader {
  private buf: Buffer = Buffer.alloc(0);

  constructor(
    private readonly onMessage: (m: DapMessage) => void,
    private readonly onError: (e: Error) => void = () => undefined,
  ) {}

  push(chunk: Buffer): void {
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : chunk;
    for (;;) {
      const end = this.buf.indexOf("\r\n\r\n");
      if (end < 0) return;
      const header = this.buf.subarray(0, end).toString("ascii");
      const m = /^content-length:\s*(\d+)\s*$/im.exec(header);
      if (!m) {
        this.onError(new Error(`bad DAP header: ${header.slice(0, 200)}`));
        this.buf = this.buf.subarray(end + 4);
        continue;
      }
      const start = end + 4;
      const stop = start + Number(m[1]);
      if (this.buf.length < stop) return;
      const body = this.buf.subarray(start, stop).toString("utf8");
      this.buf = this.buf.subarray(stop);
      try {
        this.onMessage(JSON.parse(body) as DapMessage);
      } catch (e) {
        this.onError(e as Error);
      }
    }
  }
}

const REFUSED: Record<string, string> = {
  launch: "Kiln only attaches to a running server (request: attach).",
  terminate: "Kiln never terminates the server from the debugger: disconnecting detaches and the server keeps running.",
  restart: "Kiln never restarts the server from the debugger: use Kiln: Stop/Start Test Server.",
  terminateThreads: "Kiln never terminates server threads from the debugger.",
};

export interface GuardPorts {
  toAdapter(m: DapMessage): void;
  toClient(m: DapMessage): void;
}

export class DapGuard {
  /** Our own messages use their own range, so they never collide with the client's or the adapter's seq. */
  private ownSeq = 1_000_000;
  private ownDisconnect?: number;
  private clientDisconnect?: number;
  private waiters: ((detached: boolean) => void)[] = [];
  /** client requests netcoredbg has not answered yet */
  private pending = new Map<number, string>();
  private attachRequested = false;
  private adapterGone = false;

  /** The debugger answered a disconnect with success: the server is no longer tied to it. */
  detached = false;
  private modules = new Set<string>();
  /** Set when there is no debugger: every request is answered with this. */
  failure?: string;
  onDetached: () => void = () => undefined;

  constructor(
    private readonly ports: GuardPorts,
    private readonly pid?: number,
  ) {}

  /** Is the server tied to the debugger (attach was sent, no successful disconnect)? */
  get mustDisconnect(): boolean {
    return this.attachRequested && !this.detached && !this.adapterGone;
  }

  fromClient(m: DapMessage): void {
    if (m.type !== "request") {
      this.ports.toAdapter(m);
      return;
    }
    const cmd = m.command ?? "";
    if (this.failure) {
      if (cmd === "disconnect") this.answer(m, true);
      else this.answer(m, false, this.failure);
      return;
    }
    if (Object.hasOwn(REFUSED, cmd)) {
      this.answer(m, false, REFUSED[cmd]);
      return;
    }
    if (cmd === "attach") {
      const want = Number(m.arguments?.processId ?? 0);
      if (want && this.pid && want !== this.pid) {
        this.answer(m, false, `This server's process is ${this.pid}, not ${want}: leave processId out of the configuration.`);
        return;
      }
      m = { ...m, arguments: { ...m.arguments, ...(this.pid ? { processId: this.pid } : {}) } };
      this.attachRequested = true;
    } else if (cmd === "disconnect") {
      const { restart: _restart, suspendDebuggee: _suspend, ...rest } = m.arguments ?? {};
      m = { ...m, arguments: { ...rest, terminateDebuggee: false } };
      this.clientDisconnect = m.seq;
    }
    if (m.seq !== undefined) this.pending.set(m.seq, cmd);
    this.ports.toAdapter(m);
  }

  fromAdapter(m: DapMessage): void {
    if (m.type === "response") {
      if (m.request_seq !== undefined) this.pending.delete(m.request_seq);
      if (m.request_seq === this.ownDisconnect) {
        this.settleDisconnect(m.success === true);
        return; // the client never asked for it
      }
      if (m.request_seq === this.clientDisconnect) {
        this.ports.toClient(m);
        this.settleDisconnect(m.success === true);
        return;
      }
      if (m.command === "initialize") {
        m = {
          ...m,
          body: {
            ...m.body,
            supportTerminateDebuggee: false,
            supportSuspendDebuggee: false,
            supportsTerminateRequest: false,
            supportsRestartRequest: false,
            supportsTerminateThreadsRequest: false,
          },
        };
      }
    }
    this.ports.toClient(m);
    // a module that comes a second time is a reload: netcoredbg keeps breakpoints on the first copy
    if (m.type === "event" && m.event === "module" && m.body?.reason === "new" && typeof m.body.module?.name === "string") {
      const name: string = m.body.module.name;
      if (this.modules.has(name)) {
        this.event("output", { category: "important", output: `${name} was reloaded: breakpoints in it no longer stop. Disconnect and attach again.\n` });
      }
      this.modules.add(name);
    }
  }

  /** Sends a disconnect of our own (or joins the client's); resolves true once the debugger has detached. */
  disconnect(): Promise<boolean> {
    if (this.detached) return Promise.resolve(true);
    if (this.adapterGone) return Promise.resolve(false);
    return new Promise((resolve) => {
      this.waiters.push(resolve);
      if (this.clientDisconnect !== undefined && this.pending.has(this.clientDisconnect)) return;
      if (this.ownDisconnect !== undefined && this.pending.has(this.ownDisconnect)) return;
      this.ownDisconnect = this.ownSeq++;
      this.pending.set(this.ownDisconnect, "disconnect");
      this.ports.toAdapter({ seq: this.ownDisconnect, type: "request", command: "disconnect", arguments: { terminateDebuggee: false } });
    });
  }

  /** The debugger process is gone: answer what it left open, so the client shows `why` rather than hanging. */
  adapterExited(why: string): void {
    this.adapterGone = true;
    for (const [seq, command] of this.pending) {
      if (seq !== this.ownDisconnect) this.answer({ seq, type: "request", command }, false, why);
    }
    this.pending.clear();
    this.settleDisconnect(false);
  }

  /** Sends an event to the client (console output, terminated). */
  event(event: string, body?: DapMessage["body"]): void {
    this.ports.toClient({ seq: this.ownSeq++, type: "event", event, ...(body === undefined ? {} : { body }) });
  }

  private settleDisconnect(ok: boolean): void {
    const newlyDetached = ok && !this.detached;
    if (ok) this.detached = true;
    const w = this.waiters;
    this.waiters = [];
    for (const f of w) f(this.detached);
    if (newlyDetached) this.onDetached();
  }

  private answer(req: DapMessage, success: boolean, message?: string): void {
    this.ports.toClient({
      seq: this.ownSeq++,
      type: "response",
      request_seq: req.seq,
      command: req.command,
      success,
      ...(message ? { message } : {}),
    });
  }
}
