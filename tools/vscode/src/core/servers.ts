// SPDX-License-Identifier: MIT
// Test servers: a server folder (enshrouded_server.exe + kiln/), started and stopped through kilnctl
// (~/.config/kiln/instances/<name>.toml) or tools/lab/lab.sh (Kiln repository labs), or not at all (folder).
// Servers inside a Steam library (steamapps/common, steamapps/compatdata) are read-only here: the extension follows
// their logs and opens their console, but never deploys to, reloads, tests on, starts or stops them.
import * as fs from "node:fs";
import * as net from "node:net";
import * as os from "node:os";
import * as path from "node:path";

export type ServerKind = "folder" | "kilnctl" | "lab";

export interface ServerDef {
  name: string;
  dir: string;
  kind: ServerKind;
  /** kilnctl instance / lab name (default: name) */
  instance?: string;
  /** Wine prefix, when known (kilnctl, labs) */
  prefix?: string;
  source: "settings" | "kilnctl" | "lab";
}

export function expandHome(p: string): string {
  return p === "~" ? os.homedir() : p.startsWith("~/") ? path.join(os.homedir(), p.slice(2)) : p;
}

/** Why a folder must not be changed by the extension, or undefined when it may. */
export function protectedReason(dir: string): string | undefined {
  const p = path.resolve(expandHome(dir)).replace(/\\/g, "/");
  if (/\/steamapps\/(common|compatdata)(\/|$)/i.test(p)) {
    return "it is inside a Steam library (the Steam client's game or server install); the extension only reads it";
  }
  return undefined;
}

/** Reads `key = "value"` / `key = 123` lines of a TOML file's top level (all kilnctl instance files need). */
export function readTomlTop(text: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith("[")) break;
    const m = /^([A-Za-z0-9_-]+)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|'([^']*)'|([^#\s]+))/.exec(line);
    if (m) out[m[1]] = m[2] !== undefined ? m[2].replace(/\\(.)/g, "$1") : (m[3] ?? m[4]);
  }
  return out;
}

export function kilnctlInstancesDir(): string {
  const cfg = process.env.XDG_CONFIG_HOME || path.join(os.homedir(), ".config");
  return path.join(cfg, "kiln", "instances");
}

export function discoverKilnctl(dir = kilnctlInstancesDir()): ServerDef[] {
  let files: string[] = [];
  try {
    files = fs.readdirSync(dir).filter((f) => f.endsWith(".toml"));
  } catch {
    return [];
  }
  const out: ServerDef[] = [];
  for (const f of files) {
    try {
      const t = readTomlTop(fs.readFileSync(path.join(dir, f), "utf8"));
      if (!t.dir) continue;
      const name = f.slice(0, -5);
      out.push({ name, dir: expandHome(t.dir), kind: "kilnctl", instance: name, prefix: t.prefix ? expandHome(t.prefix) : `${expandHome(t.dir)}.pfx`, source: "kilnctl" });
    } catch {
      /* unreadable instance file */
    }
  }
  return out.sort((a, b) => a.name.localeCompare(b.name));
}

export const LABS_DIR = path.join(os.homedir(), "srv", "enshrouded", "labs");

export function discoverLabs(dir = LABS_DIR): ServerDef[] {
  let names: string[] = [];
  try {
    names = fs.readdirSync(dir);
  } catch {
    return [];
  }
  return names
    .filter((n) => /^[a-z0-9-]+$/.test(n) && fs.existsSync(path.join(dir, n, "enshrouded_server.exe")))
    .sort()
    .map((n) => ({ name: n, dir: path.join(dir, n), kind: "lab" as const, instance: n, prefix: path.join(dir, `${n}.pfx`), source: "lab" as const }));
}

export function hasKiln(dir: string): boolean {
  return fs.existsSync(path.join(dir, "kiln"));
}

/**
 * Pids of `enshrouded_serv` processes that belong to this server: WINEPREFIX equal to its prefix (how lab.sh and
 * kilnctl find theirs), or the process's cwd inside its folder. Linux /proc only; read-only.
 */
export function serverPids(def: ServerDef): number[] {
  if (process.platform !== "linux") return [];
  const pids: number[] = [];
  let entries: string[] = [];
  try {
    entries = fs.readdirSync("/proc").filter((e) => /^\d+$/.test(e));
  } catch {
    return [];
  }
  const dir = path.resolve(def.dir);
  for (const e of entries) {
    try {
      const comm = fs.readFileSync(`/proc/${e}/comm`, "utf8").trim();
      if (comm !== "enshrouded_serv") continue;
      if (def.prefix) {
        const env = fs.readFileSync(`/proc/${e}/environ`, "utf8").split("\0");
        if (env.includes(`WINEPREFIX=${def.prefix}`)) {
          pids.push(Number(e));
          continue;
        }
      }
      const cwd = fs.readlinkSync(`/proc/${e}/cwd`);
      if (cwd === dir || cwd.startsWith(dir + path.sep)) pids.push(Number(e));
    } catch {
      /* gone or not ours */
    }
  }
  return pids;
}

/** Does something accept TCP connections there (KAP endpoint liveness: refused = stale endpoint.json)? */
export function probeTcp(host: string, port: number, timeoutMs = 400): Promise<boolean> {
  return new Promise((resolve) => {
    const s = net.createConnection({ host, port });
    const done = (v: boolean) => {
      s.destroy();
      resolve(v);
    };
    s.setTimeout(timeoutMs, () => done(false));
    s.once("connect", () => done(true));
    s.once("error", () => done(false));
  });
}

export interface ServerState {
  kiln: boolean;
  running: boolean;
  pids: number[];
  kap?: { host: string; port: number; live: boolean };
  protected?: string;
}

export async function serverState(def: ServerDef): Promise<ServerState> {
  const pids = serverPids(def);
  const st: ServerState = { kiln: hasKiln(def.dir), running: pids.length > 0, pids, protected: protectedReason(def.dir) };
  const ep = path.join(def.dir, "kiln", "data", "kiln-admin", "endpoint.json");
  try {
    const e = JSON.parse(fs.readFileSync(ep, "utf8"));
    if (e.port) {
      const live = await probeTcp(e.host || "127.0.0.1", e.port);
      st.kap = { host: e.host || "127.0.0.1", port: e.port, live };
      if (live) st.running = true;
    }
  } catch {
    /* no endpoint */
  }
  return st;
}
