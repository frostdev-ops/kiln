// SPDX-License-Identifier: MIT
import { spawn, SpawnOptions } from "node:child_process";

export interface RunResult {
  code: number | null;
  signal: NodeJS.Signals | null;
  stdout: string;
  stderr: string;
  /** spawn failed (ENOENT: the program is not there) */
  error?: NodeJS.ErrnoException;
  timedOut?: boolean;
}

export interface RunOptions {
  cwd?: string;
  env?: NodeJS.ProcessEnv;
  input?: string;
  timeoutMs?: number;
  onStdout?: (chunk: string) => void;
  onStderr?: (chunk: string) => void;
  /** aborts the process (SIGTERM) */
  signal?: AbortSignal;
}

/** Runs a program without a shell; never rejects (spawn errors come back in `error`). */
export function run(cmd: string, args: string[], o: RunOptions = {}): Promise<RunResult> {
  return new Promise((resolve) => {
    const so: SpawnOptions = { cwd: o.cwd, env: o.env ?? process.env, stdio: ["pipe", "pipe", "pipe"], windowsHide: true };
    let child;
    try {
      child = spawn(cmd, args, so);
    } catch (e) {
      resolve({ code: null, signal: null, stdout: "", stderr: "", error: e as NodeJS.ErrnoException });
      return;
    }
    let stdout = "";
    let stderr = "";
    let timedOut = false;
    let done = false;
    const finish = (r: RunResult) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      o.signal?.removeEventListener("abort", onAbort);
      resolve(r);
    };
    const timer = o.timeoutMs
      ? setTimeout(() => {
          timedOut = true;
          child.kill("SIGTERM");
        }, o.timeoutMs)
      : undefined;
    const onAbort = () => child.kill("SIGTERM");
    o.signal?.addEventListener("abort", onAbort);
    child.stdout!.setEncoding("utf8").on("data", (c: string) => {
      stdout += c;
      o.onStdout?.(c);
    });
    child.stderr!.setEncoding("utf8").on("data", (c: string) => {
      stderr += c;
      o.onStderr?.(c);
    });
    child.on("error", (e) => finish({ code: null, signal: null, stdout, stderr, error: e, timedOut }));
    child.on("close", (code, signal) => finish({ code, signal, stdout, stderr, timedOut }));
    child.stdin!.on("error", () => undefined);
    if (o.input !== undefined) child.stdin!.end(o.input);
    else child.stdin!.end();
  });
}

/** Quotes one argument for display (task labels, log lines). */
export function shellQuote(a: string): string {
  return /^[A-Za-z0-9_\-./:=@%+,]+$/.test(a) ? a : `'${a.replace(/'/g, `'\\''`)}'`;
}
