// SPDX-License-Identifier: MIT
// `kiln lint --json` report: spans are UTF-8 byte offsets, line/col are 1-based
// with columns in code points. Codes: KL0xx syntax, KL1xx schema, KL2xx manifest, KL3xx ids, KL4xx game ids,
// KL5xx assets/cook, KL6xx licences, KL9xx internal.
import { ByteMapper, lineColToIndex } from "./utf8";

export type Severity = "error" | "warning" | "info" | "hint";

export interface Span {
  start: number;
  end: number;
}

export interface LintEdit {
  file: string;
  span: Span;
  newText: string;
}

export interface LintFix {
  title: string;
  edits: LintEdit[];
}

export interface LintDiagnostic {
  file: string;
  code: string;
  rule?: string;
  severity: Severity;
  message: string;
  span?: Span;
  line?: number;
  col?: number;
  endLine?: number;
  endCol?: number;
  pointer?: string;
  schema?: string;
  fixes?: LintFix[];
  related?: { file: string; span?: Span; message: string }[];
  docs?: string;
}

export interface LintReport {
  format: number;
  tool?: string;
  version?: string;
  ok: boolean;
  checked?: string[];
  summary?: { errors: number; warnings: number; infos: number; hints: number };
  catalog?: { path: string; side: string; build: string } | null;
  diagnostics: LintDiagnostic[];
}

const SEVERITIES = new Set(["error", "warning", "info", "hint"]);

/** Parses and sanity-checks the CLI's stdout. Throws with a short reason when it is not a lint report. */
export function parseLintReport(stdout: string): LintReport {
  const start = stdout.indexOf("{");
  if (start < 0) throw new Error("no JSON in kiln lint output");
  const r = JSON.parse(stdout.slice(start)) as LintReport;
  if (typeof r !== "object" || r === null || !Array.isArray(r.diagnostics)) {
    throw new Error("kiln lint output has no diagnostics[]");
  }
  if (r.format !== 1) throw new Error(`kiln lint report format ${r.format} (this extension reads 1)`);
  r.diagnostics = r.diagnostics.filter((d) => d && typeof d.file === "string" && typeof d.message === "string");
  for (const d of r.diagnostics) {
    if (!SEVERITIES.has(d.severity)) d.severity = "warning";
  }
  return r;
}

/** String index range of a diagnostic (or an edit) in `text`: the byte span when present, else line/col. */
export function rangeOf(
  text: string,
  d: { span?: Span; line?: number; col?: number; endLine?: number; endCol?: number },
  mapper?: ByteMapper,
): { start: number; end: number } {
  if (d.span && Number.isInteger(d.span.start) && Number.isInteger(d.span.end)) {
    const m = mapper ?? new ByteMapper(text);
    const a = m.index(d.span.start);
    const b = m.index(d.span.end);
    return { start: a, end: Math.max(a, b) };
  }
  if (d.line) {
    const a = lineColToIndex(text, d.line, d.col ?? 1);
    const b = d.endLine ? lineColToIndex(text, d.endLine, d.endCol ?? 1) : a;
    return { start: a, end: Math.max(a, b) };
  }
  return { start: 0, end: 0 };
}

/** Diagnostics grouped by file, each group sorted by span start (so one ByteMapper pass serves a file). */
export function byFile(r: LintReport): Map<string, LintDiagnostic[]> {
  const m = new Map<string, LintDiagnostic[]>();
  for (const d of r.diagnostics) {
    const list = m.get(d.file) ?? [];
    list.push(d);
    m.set(d.file, list);
  }
  for (const list of m.values()) list.sort((a, b) => (a.span?.start ?? 0) - (b.span?.start ?? 0));
  return m;
}
