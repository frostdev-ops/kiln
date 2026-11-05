// SPDX-License-Identifier: MIT
// kiln.log lines: `[L HH:MM:SS.mmm] [tag] text`, L = T|D|I|W|E. Tags: kiln, host, mod:<id>, service:<dll>, ...
// Links: mod tags, `in <file>:line N` stack frames, Wine paths (Z:\home\...), absolute POSIX paths, kiln/... paths.

export interface LogLine {
  level: string;
  time: string;
  tag: string;
  text: string;
}

const LINE_RX = /^\[([TDIWE]) (\d\d:\d\d:\d\d\.\d{3})\] \[([^\]]+)\] (.*)$/;

export function parseLogLine(s: string): LogLine | undefined {
  const m = LINE_RX.exec(s);
  return m ? { level: m[1], time: m[2], tag: m[3], text: m[4] } : undefined;
}

export type LinkTarget =
  | { kind: "mod"; id: string }
  | { kind: "file"; path: string; line?: number; col?: number }
  | { kind: "test"; mod?: string; name: string };

export interface LogLink {
  start: number;
  end: number;
  target: LinkTarget;
}

/** Wine drive path (Z:\home\x\y.cs) -> POSIX. Z: is the Unix root in Wine's default prefix. */
export function winToPosix(p: string): string {
  const m = /^([A-Za-z]):\\(.*)$/.exec(p);
  if (!m) return p;
  const rest = m[2].replace(/\\/g, "/");
  return m[1].toUpperCase() === "Z" ? "/" + rest : `${m[1].toUpperCase()}:/${rest}`;
}

const MOD_TAG_RX = /\[mod:([a-z0-9][a-z0-9._-]*)\]/g;
// " in /path/File.cs:line 116" (.NET stack frames; the path may be a Wine path)
const STACK_RX = /\bin ((?:[A-Za-z]:\\|\/)[^:\n]*?\.(?:cs|fs|vb|cpp|h|hpp|c)):line (\d+)/g;
// Z:\home\user\...\file.ext (stops at whitespace, quotes, brackets and trailing punctuation)
const WINPATH_RX = /\b[A-Za-z]:\\[^\s"'()<>\[\]|]+/g;
// /home/... or /srv/... absolute paths with an optional :line[:col]
const POSIX_RX = /(?<![\w.~])\/(?:home|srv|tmp|mnt|opt|usr|var|run)\/[^\s"'()<>\[\]|:,;]+(?::(\d+)(?::(\d+))?)?/g;
// PASS/FAIL/SKIP <name> (Kiln.Testing)
const TEST_RX = /\b(PASS|FAIL|SKIP) ([A-Za-z_][\w.<>`-]*[\w>])/g;

function trimPunct(s: string): string {
  return s.replace(/[.,;:)\]]+$/, "");
}

export function findLinks(line: string): LogLink[] {
  const out: LogLink[] = [];
  const taken: [number, number][] = [];
  const free = (a: number, b: number) => !taken.some(([x, y]) => a < y && b > x);
  const add = (start: number, end: number, target: LinkTarget) => {
    if (end <= start || !free(start, end)) return;
    taken.push([start, end]);
    out.push({ start, end, target });
  };
  const parsed = parseLogLine(line);
  const mod = parsed?.tag.startsWith("mod:") ? parsed.tag.slice(4) : undefined;
  for (const m of line.matchAll(STACK_RX)) {
    const p = m[1];
    const s = m.index! + 3;
    add(s, s + p.length + 6 + m[2].length, { kind: "file", path: winToPosix(p), line: Number(m[2]) });
  }
  for (const m of line.matchAll(WINPATH_RX)) {
    const p = trimPunct(m[0]);
    add(m.index!, m.index! + p.length, { kind: "file", path: winToPosix(p) });
  }
  for (const m of line.matchAll(POSIX_RX)) {
    let text = m[0];
    let ln: number | undefined;
    let col: number | undefined;
    if (m[1]) {
      ln = Number(m[1]);
      col = m[2] ? Number(m[2]) : undefined;
      text = text.slice(0, text.length - (m[1].length + 1 + (m[2] ? m[2].length + 1 : 0)));
    }
    const p = trimPunct(text);
    const len = m[1] ? m[0].length : p.length;
    add(m.index!, m.index! + len, { kind: "file", path: p, line: ln, col });
  }
  for (const m of line.matchAll(MOD_TAG_RX)) {
    add(m.index! + 5, m.index! + 5 + m[1].length, { kind: "mod", id: m[1] });
  }
  for (const m of line.matchAll(TEST_RX)) {
    const s = m.index! + m[1].length + 1;
    add(s, s + m[2].length, { kind: "test", mod, name: m[2] });
  }
  return out.sort((a, b) => a.start - b.start);
}

/** "kiln.log" level filter for following: E > W > I > D > T. */
export function levelAtLeast(level: string, min: string): boolean {
  const order = "TDIWE";
  return order.indexOf(level) >= order.indexOf(min);
}
