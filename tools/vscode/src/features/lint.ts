// SPDX-License-Identifier: MIT
// `kiln lint --json` on save (and optionally on type through --stdin) -> diagnostics "kiln lint" with quick fixes.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { matchFiles } from "../core/glob";
import { byFile, LintDiagnostic, LintFix, LintReport, parseLintReport, rangeOf } from "../core/lintReport";
import { ByteMapper } from "../core/utf8";
import { Ctx } from "../ctx";
import { Mods } from "./mods";

const SOURCE = "kiln lint";

function severity(s: string): vscode.DiagnosticSeverity {
  switch (s) {
    case "error":
      return vscode.DiagnosticSeverity.Error;
    case "info":
      return vscode.DiagnosticSeverity.Information;
    case "hint":
      return vscode.DiagnosticSeverity.Hint;
    default:
      return vscode.DiagnosticSeverity.Warning;
  }
}

function key(uri: vscode.Uri, d: { range: vscode.Range; code?: unknown; message: string }): string {
  const code = typeof d.code === "object" && d.code ? (d.code as { value: unknown }).value : d.code;
  return `${uri.toString()}|${d.range.start.line}:${d.range.start.character}-${d.range.end.line}:${d.range.end.character}|${String(code)}|${d.message}`;
}

export class Lint implements vscode.CodeActionProvider, vscode.Disposable {
  readonly collection = vscode.languages.createDiagnosticCollection("kiln-lint");
  private fixes = new Map<string, LintFix[]>();
  private timers = new Map<string, NodeJS.Timeout>();
  private running = new Map<string, AbortController>();
  private disposables: vscode.Disposable[] = [];
  private warnedNoCli = false;
  /** last report per linted path (tests read it) */
  readonly lastReports = new Map<string, LintReport>();

  constructor(
    private readonly ctx: Ctx,
    private readonly mods: Mods,
    private readonly schemaMap: { fileMatch: string[] }[],
  ) {
    this.disposables.push(
      this.collection,
      vscode.languages.registerCodeActionsProvider([{ language: "json" }, { language: "jsonc" }], this, {
        providedCodeActionKinds: [vscode.CodeActionKind.QuickFix],
      }),
      vscode.workspace.onDidSaveTextDocument((d) => {
        if (this.ctx.setting("lint.onSave", true) && this.wants(d)) void this.lintDocument(d, false);
      }),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (!this.ctx.setting("lint.onType", false) || !this.wants(e.document) || !e.contentChanges.length) return;
        const k = e.document.uri.toString();
        clearTimeout(this.timers.get(k));
        this.timers.set(k, setTimeout(() => void this.lintDocument(e.document, true), 600));
      }),
      vscode.workspace.onDidOpenTextDocument((d) => {
        if (this.ctx.setting("lint.onSave", true) && this.wants(d) && !this.collection.has(d.uri)) void this.lintDocument(d, false);
      }),
      vscode.commands.registerCommand("kiln.lint", async (m?: { dir: string }) => {
        const mod = m ?? (await this.mods.pickMod("lint"));
        if (mod) await this.lintPaths([mod.dir], true);
      }),
    );
  }

  dispose(): void {
    for (const t of this.timers.values()) clearTimeout(t);
    for (const d of this.disposables) d.dispose();
  }

  /** Kiln files: inside a mod folder, or matched by the schema mapping. */
  wants(d: vscode.TextDocument): boolean {
    if (d.uri.scheme !== "file") return false;
    if (d.languageId !== "json" && d.languageId !== "jsonc") return false;
    const f = d.uri.fsPath;
    const rel = vscode.workspace.asRelativePath(d.uri, false);
    if (this.schemaMap.some((s) => matchFiles(s.fileMatch, [f, rel]))) return true;
    return !!this.mods.modFor(f);
  }

  async lintDocument(d: vscode.TextDocument, fromBuffer: boolean): Promise<LintReport | undefined> {
    if (!(await this.ctx.hasCli())) return undefined;
    const f = d.uri.fsPath;
    this.running.get(f)?.abort();
    const ac = new AbortController();
    this.running.set(f, ac);
    const args = ["lint", "--json", ...(fromBuffer ? ["--stdin", "--stdin-path", f] : [f])];
    const r = await this.ctx.exec(this.ctx.cliPath(), args, {
      cwd: path.dirname(f),
      input: fromBuffer ? d.getText() : undefined,
      timeoutMs: 30_000,
      signal: ac.signal,
    });
    if (ac.signal.aborted) return undefined;
    this.running.delete(f);
    return this.apply(r.stdout, r.code, r.stderr, [f], fromBuffer ? d : undefined);
  }

  async lintPaths(paths: string[], interactive: boolean): Promise<LintReport | undefined> {
    if (!(await this.ctx.hasCli())) {
      if (interactive || !this.warnedNoCli) {
        this.warnedNoCli = true;
        vscode.window.showInformationMessage(
          "Kiln: `kiln lint` needs the kiln CLI (cargo install --path crates/kiln-cli, or set kiln.cli.path). Schema validation works without it.",
        );
      }
      return undefined;
    }
    const r = await this.ctx.exec(this.ctx.cliPath(), ["lint", "--json", ...paths], { timeoutMs: 120_000 });
    const rep = await this.apply(r.stdout, r.code, r.stderr, paths);
    if (interactive && rep) {
      const s = rep.summary;
      vscode.window.setStatusBarMessage(
        `kiln lint: ${s ? `${s.errors} error(s), ${s.warnings} warning(s)` : `${rep.diagnostics.length} diagnostic(s)`}`,
        6000,
      );
      if (rep.diagnostics.length) vscode.commands.executeCommand("workbench.actions.view.problems");
    }
    return rep;
  }

  /** Report -> diagnostics. Files under a linted path that the report does not mention are cleared. */
  async apply(stdout: string, code: number | null, stderr: string, linted: string[], buffer?: vscode.TextDocument): Promise<LintReport | undefined> {
    if (code === 2 || !stdout.trim()) {
      this.ctx.out.warn(`kiln lint failed (exit ${code}): ${stderr.trim()}`);
      return undefined;
    }
    let rep: LintReport;
    try {
      rep = parseLintReport(stdout);
    } catch (e) {
      this.ctx.out.warn(`kiln lint: ${(e as Error).message}`);
      return undefined;
    }
    for (const p of linted) this.lastReports.set(p, rep);
    const groups = byFile(rep);
    // clear what was linted
    const linkedRoots = linted.map((p) => path.resolve(p));
    this.collection.forEach((uri) => {
      const f = uri.fsPath;
      if (linkedRoots.some((r) => f === r || f.startsWith(r + path.sep)) && !groups.has(f)) {
        this.collection.delete(uri);
        this.dropFixes(uri);
      }
    });
    const root = this.ctx.kilnRoot();
    for (const [file, list] of groups) {
      const uri = vscode.Uri.file(file);
      const text = buffer && buffer.uri.fsPath === file ? buffer.getText() : await this.textOf(uri);
      if (text === undefined) continue;
      const mapper = new ByteMapper(text);
      const posAt = indexToPos(text);
      this.dropFixes(uri);
      const diags = list.map((d) => {
        const r = rangeOf(text, d, mapper);
        const diag = new vscode.Diagnostic(new vscode.Range(posAt(r.start), posAt(r.end)), d.message, severity(d.severity));
        diag.source = SOURCE;
        diag.code = this.codeOf(d, root);
        if (d.related?.length) {
          diag.relatedInformation = d.related.map((x) => {
            const rel = this.rangeIn(x.file, x);
            return new vscode.DiagnosticRelatedInformation(new vscode.Location(vscode.Uri.file(x.file), rel), x.message);
          });
        }
        if (d.fixes?.length) this.fixes.set(key(uri, diag), d.fixes);
        return diag;
      });
      this.collection.set(uri, diags);
    }
    return rep;
  }

  private codeOf(d: LintDiagnostic, root?: string): vscode.Diagnostic["code"] {
    if (d.docs && root) {
      const [file, frag] = d.docs.split("#");
      const p = path.join(root, file);
      if (fs.existsSync(p)) return { value: d.code, target: vscode.Uri.file(p).with({ fragment: frag ?? "" }) };
    }
    return d.rule ? `${d.code} ${d.rule}` : d.code;
  }

  private dropFixes(uri: vscode.Uri) {
    const prefix = uri.toString() + "|";
    for (const k of this.fixes.keys()) if (k.startsWith(prefix)) this.fixes.delete(k);
  }

  private async textOf(uri: vscode.Uri): Promise<string | undefined> {
    const open = vscode.workspace.textDocuments.find((t) => t.uri.toString() === uri.toString());
    if (open) return open.getText();
    try {
      return await fs.promises.readFile(uri.fsPath, "utf8");
    } catch {
      return undefined;
    }
  }

  private rangeIn(file: string, d: Parameters<typeof rangeOf>[1]): vscode.Range {
    let text = "";
    try {
      const open = vscode.workspace.textDocuments.find((t) => t.uri.fsPath === file);
      text = open ? open.getText() : fs.readFileSync(file, "utf8");
    } catch {
      return new vscode.Range(0, 0, 0, 0);
    }
    const r = rangeOf(text, d);
    const posAt = indexToPos(text);
    return new vscode.Range(posAt(r.start), posAt(r.end));
  }

  async provideCodeActions(doc: vscode.TextDocument, _range: vscode.Range, context: vscode.CodeActionContext): Promise<vscode.CodeAction[]> {
    const out: vscode.CodeAction[] = [];
    for (const diag of context.diagnostics) {
      if (diag.source !== SOURCE) continue;
      const fixes = this.fixes.get(key(doc.uri, diag));
      if (!fixes) continue;
      for (const fx of fixes) {
        const a = new vscode.CodeAction(fx.title, vscode.CodeActionKind.QuickFix);
        a.diagnostics = [diag];
        const we = new vscode.WorkspaceEdit();
        for (const e of fx.edits) {
          const uri = vscode.Uri.file(e.file || doc.uri.fsPath);
          const text = uri.toString() === doc.uri.toString() ? doc.getText() : await this.textOf(uri);
          if (text === undefined) continue;
          const r = rangeOf(text, { span: e.span });
          const posAt = indexToPos(text);
          we.replace(uri, new vscode.Range(posAt(r.start), posAt(r.end)), e.newText);
        }
        a.edit = we;
        if (fixes.length === 1) a.isPreferred = true;
        out.push(a);
      }
    }
    return out;
  }
}

/** String index -> Position for a text (line starts computed once). */
export function indexToPos(text: string): (i: number) => vscode.Position {
  const starts = [0];
  for (let i = text.indexOf("\n"); i >= 0; i = text.indexOf("\n", i + 1)) starts.push(i + 1);
  return (i: number) => {
    let lo = 0;
    let hi = starts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (starts[mid] <= i) lo = mid;
      else hi = mid - 1;
    }
    return new vscode.Position(lo, i - starts[lo]);
  };
}
