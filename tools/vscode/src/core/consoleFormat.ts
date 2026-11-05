// SPDX-License-Identifier: MIT
// Terminal formatting for the KAP/1 console and test output (ANSI), and `.op` argument parsing.
import { parseLogLine } from "./logLine";

export const C = {
  reset: "\x1b[0m",
  dim: "\x1b[2m",
  red: "\x1b[31m",
  green: "\x1b[32m",
  yellow: "\x1b[33m",
  cyan: "\x1b[36m",
  ember: "\x1b[38;2;255;106;26m",
  bold: "\x1b[1m",
};

export function colorLogLine(s: string): string {
  const p = parseLogLine(s);
  let line = s.replace(/\b(PASS)\b/g, `${C.green}$1${C.reset}`).replace(/\b(FAIL)\b/g, `${C.red}$1${C.reset}`);
  if (p?.level === "E") line = `${C.red}${line}${C.reset}`;
  else if (p?.level === "W") line = `${C.yellow}${line}${C.reset}`;
  else if (p?.level === "D" || p?.level === "T") line = `${C.dim}${line}${C.reset}`;
  return line;
}

/** "a=1 b=x c={"j":2}" or a JSON object -> args. */
export function parseOpArgs(rest: string): Record<string, unknown> {
  const t = rest.trim();
  if (!t) return {};
  if (t.startsWith("{")) return JSON.parse(t);
  const out: Record<string, unknown> = {};
  const rx = /([A-Za-z_][\w.]*)=("(?:[^"\\]|\\.)*"|\{[^}]*\}|\[[^\]]*\]|\S+)/g;
  for (const m of t.matchAll(rx)) {
    const v = m[2];
    try {
      out[m[1]] = JSON.parse(v);
    } catch {
      out[m[1]] = v;
    }
  }
  return out;
}
