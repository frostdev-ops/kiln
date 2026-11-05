// SPDX-License-Identifier: MIT
// The control folder (docs/kiln/modding/lifecycle.md, crates/kiln-manage/src/control.rs): write
// kiln/control/<name>.tmp, rename it to <name>.cmd; Kiln.Host polls it on the tick thread and writes
// <name>.result.json(.tmp -> rename), so a result file that exists is complete. Commands: mods, load <folder>,
// unload <id>, reload <id>, reload-test <id> <n>, test <id> [filter], gc.
import * as fs from "node:fs";
import * as path from "node:path";

export interface ControlResult {
  command: string;
  ok: boolean;
  message: string;
  data?: unknown;
  time?: string;
}

export class ControlError extends Error {
  constructor(
    message: string,
    readonly code: "no_kiln" | "timeout" | "not_picked_up" | "bad_result",
  ) {
    super(message);
  }
}

let seq = 0;
export function requestName(tag = "vscode"): string {
  return `${tag}-${process.pid}-${Date.now().toString(36)}-${(seq++).toString(36)}`;
}

export interface SendOptions {
  timeoutMs?: number;
  pollMs?: number;
  signal?: AbortSignal;
  /** sub-folder of kiln/control (the console channel is "console") */
  channel?: string;
  name?: string;
}

/** Sends one command and waits for its result (deleting the result file afterwards). */
export async function sendControl(serverDir: string, command: string, o: SendOptions = {}): Promise<ControlResult> {
  const kiln = path.join(serverDir, "kiln");
  if (!fs.existsSync(kiln)) throw new ControlError(`${serverDir} has no kiln/ folder (deploy Kiln first)`, "no_kiln");
  const dir = path.join(kiln, "control", o.channel ?? "");
  fs.mkdirSync(dir, { recursive: true });
  const name = o.name ?? requestName();
  const tmp = path.join(dir, `${name}.tmp`);
  const cmd = path.join(dir, `${name}.cmd`);
  const res = path.join(dir, `${name}.result.json`);
  fs.writeFileSync(tmp, command.trim() + "\n");
  fs.renameSync(tmp, cmd);
  const deadline = Date.now() + (o.timeoutMs ?? 15_000);
  const poll = o.pollMs ?? 100;
  for (;;) {
    if (fs.existsSync(res)) {
      let raw = "";
      try {
        raw = fs.readFileSync(res, "utf8");
      } finally {
        fs.rmSync(res, { force: true });
      }
      try {
        return JSON.parse(raw) as ControlResult;
      } catch {
        throw new ControlError(`${res}: not JSON: ${raw.slice(0, 200)}`, "bad_result");
      }
    }
    if (o.signal?.aborted || Date.now() > deadline) {
      const picked = !fs.existsSync(cmd);
      fs.rmSync(cmd, { force: true });
      if (o.signal?.aborted) throw new ControlError(`'${command}' canceled`, "timeout");
      throw new ControlError(
        picked
          ? `'${command}': Kiln took the command but wrote no result within ${Math.round((o.timeoutMs ?? 15_000) / 1000)} s`
          : `'${command}': nobody picked it up (is the server running with Kiln? the control folder is polled from world steps)`,
        picked ? "timeout" : "not_picked_up",
      );
    }
    await new Promise((r) => setTimeout(r, poll));
  }
}

/** kiln/logs/tests/<mod>.json (Kiln.Testing's report). */
export interface TestReport {
  mod: string;
  version?: string;
  started?: string;
  ms?: number;
  ok: boolean;
  passed: number;
  failed: number;
  skipped: number;
  filteredBy?: string;
  results: { name: string; outcome: "pass" | "fail" | "skip" | "timeout"; ms?: number; message?: string | null; stack?: string | null }[];
}

export function testReportPath(serverDir: string, modId: string): string {
  return path.join(serverDir, "kiln", "logs", "tests", `${modId}.json`);
}
