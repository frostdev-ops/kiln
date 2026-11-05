// SPDX-License-Identifier: MIT
// What the debugger attaches to: the server's Wine prefix, the wine it runs under (same wine, so same wineserver),
// its Windows pid, and the netcoredbg to use. Read-only: /proc and `wine tasklist.exe` in the prefix.
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { ProxySpec, scrubWineEnv } from "./dap";
import { run } from "./proc";
import { expandHome, protectedReason, ServerDef, serverPids } from "./servers";

export const SERVER_IMAGE = "enshrouded_server.exe";

export class DebugRefused extends Error {
  constructor(
    readonly code: "refused" | "not_running" | "tool_missing" | "unsupported",
    message: string,
  ) {
    super(message);
  }
}

export interface DebugTarget {
  /** the server's Windows pid: what attach needs (not the Linux one) */
  pid: number;
  wine?: string;
  prefix?: string;
  /** the server's own WINE* settings, so the debugger joins the same wineserver */
  env: Record<string, string>;
}

/** Windows pids of an image in `tasklist /fo csv /nh` output. */
export function parseTasklist(text: string, image = SERVER_IMAGE): number[] {
  const out: number[] = [];
  for (const line of text.split(/\r?\n/)) {
    const m = /^"([^"]*)","(\d+)"/.exec(line.trim());
    if (m && m[1].toLowerCase() === image.toLowerCase()) out.push(Number(m[2]));
  }
  return out;
}

/**
 * The wine that started a process, from its /proc/<pid>/exe: the preloader sits below the build's bin/wine
 * (<GE>/files/lib/wine/i386-unix/wine64-preloader, /usr/lib/wine/wine64-preloader). bin/wine first: GE-Proton also has a
 * lib/wine/i386-unix/wine next to the preloader, which is not what started it. Same lookup as `kiln debug`.
 */
export function wineAbove(exe: string, usable: (p: string) => boolean = isExecutable): string | undefined {
  for (let d = path.dirname(exe); ; d = path.dirname(d)) {
    const w = path.join(d, "bin", "wine");
    if (usable(w)) return w;
    if (d === path.dirname(d)) break;
  }
  const beside = path.join(path.dirname(exe), "wine");
  return usable(beside) ? beside : undefined;
}

function isExecutable(p: string): boolean {
  try {
    fs.accessSync(p, fs.constants.X_OK);
    return fs.statSync(p).isFile();
  } catch {
    return false;
  }
}

function procEnv(pid: number): Record<string, string> {
  try {
    const env: Record<string, string> = {};
    for (const kv of fs.readFileSync(`/proc/${pid}/environ`, "utf8").split("\0")) {
      const i = kv.indexOf("=");
      if (i > 0) env[kv.slice(0, i)] = kv.slice(i + 1);
    }
    return env;
  } catch {
    return {};
  }
}

const realpath = (p: string) => {
  try {
    return fs.realpathSync(p);
  } catch {
    return path.resolve(p);
  }
};

/** Why this server must not get a debugger: its folder, its prefix or its running process in a Steam library. */
export function debugRefusal(def: ServerDef, pids: number[] = []): string | undefined {
  const places = [def.dir, realpath(def.dir)];
  if (def.prefix) places.push(def.prefix, realpath(def.prefix));
  for (const pid of pids) {
    const pfx = procEnv(pid).WINEPREFIX;
    if (pfx) places.push(pfx, realpath(pfx));
    try {
      places.push(fs.readlinkSync(`/proc/${pid}/cwd`));
    } catch {
      /* gone */
    }
  }
  for (const p of places) {
    const why = protectedReason(p);
    if (why) return why;
  }
  return undefined;
}

/**
 * The server's wine settings a client must share to join its wineserver (a sync mode mismatch aborts). An allowlist:
 * a running wine process also has per-process internals in its environment, and copying them breaks the client
 * (WINESERVERSOCKET names a file descriptor only that process has: "wine: Bad server socket").
 */
const CARRIED_WINE_ENV = ["WINEARCH", "WINEESYNC", "WINEFSYNC", "WINENTSYNC", "WINEDLLPATH", "WINEDLLOVERRIDES"];

export interface TargetOptions {
  /** kiln.debug.wine */
  wine?: string;
  /** the Linux pids of the server (default: from /proc) */
  hostPids?: number[];
  log?: (s: string) => void;
}

/**
 * Resolves a running server; throws DebugRefused with a sentence for the user. No wine runs until its process has been
 * found: a stopped server gets no stray wineserver, and no other server's prefix is ever entered.
 */
export async function resolveTarget(def: ServerDef, o: TargetOptions = {}): Promise<DebugTarget> {
  const pids = o.hostPids ?? serverPids(def);
  const why = debugRefusal(def, pids);
  if (why) throw new DebugRefused("refused", `will not attach a debugger to ${def.name}: ${why}`);
  if (process.platform === "win32") {
    const r = await run("tasklist.exe", ["/fo", "csv", "/nh"], { timeoutMs: 20_000 });
    const pid = parseTasklist(r.stdout)[0];
    if (!pid) throw new DebugRefused("not_running", `${def.name} is not running (no ${SERVER_IMAGE} in tasklist).`);
    return { pid, env: {} };
  }
  if (!pids.length) throw new DebugRefused("not_running", `${def.name} is not running: start it first.`);
  if (pids.length > 1) throw new DebugRefused("refused", `${pids.length} server processes belong to ${def.name} (${pids.join(", ")}); stop the extra ones.`);
  const cwd = (() => {
    try {
      return fs.readlinkSync(`/proc/${pids[0]}/cwd`);
    } catch {
      return "";
    }
  })();
  if (realpath(cwd) !== realpath(def.dir)) throw new DebugRefused("refused", `the server process ${pids[0]} was not started from ${def.dir} (its folder is ${cwd || "unknown"}).`);
  const host = procEnv(pids[0]);
  const prefix = host.WINEPREFIX ?? def.prefix;
  if (!prefix) throw new DebugRefused("unsupported", `the Wine prefix of ${def.name} is unknown (the server process has no WINEPREFIX).`);
  let wine = o.wine ? expandHome(o.wine) : undefined;
  if (!wine) {
    try {
      wine = wineAbove(fs.readlinkSync(`/proc/${pids[0]}/exe`));
    } catch {
      /* gone */
    }
  }
  if (!wine) throw new DebugRefused("tool_missing", `cannot tell which wine runs ${def.name} (set kiln.debug.wine).`);
  if (!fs.existsSync(wine)) throw new DebugRefused("tool_missing", `no wine at ${wine} (kiln.debug.wine).`);
  const env: Record<string, string> = {};
  for (const k of CARRIED_WINE_ENV) if (host[k] !== undefined) env[k] = host[k];
  Object.assign(env, { WINEPREFIX: prefix, WINEDEBUG: "-all" });
  const r = await run(wine, ["tasklist.exe", "/fo", "csv", "/nh"], { env: wineEnv(env), timeoutMs: 30_000 });
  if (r.error || r.code !== 0) o.log?.(`tasklist in ${prefix}: ${r.error?.message ?? `exit ${r.code}`} ${r.stderr.trim().slice(0, 500)}`);
  const found = parseTasklist(r.stdout);
  if (found.length !== 1) throw new DebugRefused("not_running", `expected one ${SERVER_IMAGE} in ${prefix}, found ${found.length}.`);
  return { pid: found[0], wine, prefix, env };
}

/** The environment of a wine command: never a window on the user's desktop. */
export function wineEnv(extra: Record<string, string>): NodeJS.ProcessEnv {
  return { ...scrubWineEnv({ ...process.env }), ...extra };
}

/**
 * kiln.debug.netcoredbg, then $KILN_NETCOREDBG (an explicit path never falls through), then
 * <Kiln data dir>/tools/netcoredbg/netcoredbg.exe as `kiln debug` looks it up, then the copy bundled with the extension.
 */
export function netcoredbgCandidates(setting: string | undefined, env: NodeJS.ProcessEnv = process.env, bundled?: string): string[] {
  const explicit = setting || env.KILN_NETCOREDBG;
  if (explicit) return [expandHome(explicit)];
  const data =
    env.KILN_DATA_DIR ||
    (process.platform === "win32"
      ? path.join(env.LOCALAPPDATA ?? os.homedir(), "Kiln")
      : path.join(env.XDG_DATA_HOME || path.join(os.homedir(), ".local", "share"), "kiln"));
  return [path.join(data, "tools", "netcoredbg", "netcoredbg.exe"), ...(bundled ? [bundled] : [])];
}

export function findNetcoredbg(setting: string | undefined, env: NodeJS.ProcessEnv = process.env, bundled?: string): string {
  const c = netcoredbgCandidates(setting, env, bundled);
  const found = c.find((p) => fs.existsSync(p));
  if (!found) {
    throw new DebugRefused("tool_missing", `no netcoredbg at ${c.join(" or ")}. The .vsix bundles it; from source, run npm run fetch:netcoredbg in tools/vscode, or set kiln.debug.netcoredbg.`);
  }
  return found;
}

/** What the built-in proxy runs: netcoredbg in the server's prefix, under the server's wine. */
export function proxySpec(t: DebugTarget, netcoredbg: string): ProxySpec {
  const dbgArgs = [netcoredbg, "--interpreter=vscode"];
  const env = { ...t.env, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: "1" };
  const cwd = path.dirname(netcoredbg);
  return t.wine ? { command: t.wine, args: dbgArgs, env, cwd, pid: t.pid } : { command: netcoredbg, args: dbgArgs.slice(1), env, cwd, pid: t.pid };
}
