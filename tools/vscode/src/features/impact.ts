// SPDX-License-Identifier: MIT
// Impact assembly (.kimp / .kpatch): hover, completion, signature help, symbols, definitions, diagnostics through
// tools/impact's assembler + verifier (python/kimp_check.py), and the cook / disassemble / hash commands.
import * as crypto from "node:crypto";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import {
  codePart,
  DIRECTIVES,
  ecallAt,
  FRAME_OPS,
  fnv1a32,
  guidBytes,
  hex32,
  ImpactData,
  ImpactIndex,
  Native,
  OPCODES,
  scanSymbols,
  STREAMS,
  wordAt,
} from "../core/impactData";
import { modForFile } from "../core/project";
import { Ctx } from "../ctx";

const SEL: vscode.DocumentSelector = { language: "kimp" };

interface CheckResult {
  ok: boolean;
  kind: string;
  error: { line: number | null; message: string } | null;
  findings: { level: string; code: string; section: string | null; pc: number | null; line: number | null; msg: string }[];
  maxDepth: Record<string, number>;
  program: { guid: string; id: string } | null;
}

export function nativeSignature(n: Native): string {
  return `${n.name}(${n.args.map((a) => `${a.dir === "out" ? "out " : a.dir === "inout" ? "inout " : ""}${a.name}: ${a.types.slice(0, 3).join("|") || "?"}`).join(", ")})`;
}

export function nativeMarkdown(n: Native, errors: Record<string, string> = {}): vscode.MarkdownString {
  const md = new vscode.MarkdownString();
  md.appendCodeblock(nativeSignature(n), "kimp");
  const facts = [`hash \`${n.hash}\``, `argc ${n.argc}`, n.origin, n.node && n.node !== n.name ? `node ${n.node}` : "", n.query ? "query native (site in queryCallIds)" : "", n.programs ? `${n.programs} vanilla programs` : ""];
  md.appendMarkdown(facts.filter(Boolean).join(" · ") + "\n\n");
  if (n.doc) md.appendMarkdown(n.doc + "\n\n");
  if (n.returns) md.appendMarkdown(`**returns** ${n.returns}${n.suspends ? ` · **suspends** ${n.suspends === true ? "yes" : n.suspends}` : ""}\n\n`);
  n.args.forEach((a, i) => {
    md.appendMarkdown(`- \`args[${i}]\` ${a.dir} **${a.name}** ${a.types.length ? `\`${a.types.slice(0, 4).join(" | ")}\`` : ""}${a.raw ? " (raw integer ok)" : ""}${a.doc ? ` — ${a.doc}` : ""}\n`);
  });
  if (Object.keys(errors).length && n.origin === "vanilla") md.appendMarkdown(`\nResult byte: 0 ok, 1–3 suspend, ≥ 4 error.`);
  return md;
}

export class ImpactFeature implements vscode.Disposable {
  private base: ImpactData;
  private modCache = new Map<string, { key: string; data: ImpactData }>();
  readonly diagnostics = vscode.languages.createDiagnosticCollection("kiln-impact");
  private timers = new Map<string, NodeJS.Timeout>();
  private gen = new Map<string, number>();
  private disposables: vscode.Disposable[] = [];

  constructor(private readonly ctx: Ctx) {
    const index = JSON.parse(fs.readFileSync(ctx.asset("data", "impact", "index.json"), "utf8")) as ImpactIndex;
    this.base = new ImpactData(index);
    this.disposables.push(
      this.diagnostics,
      vscode.languages.registerHoverProvider(SEL, { provideHover: (d, p) => this.hover(d, p) }),
      vscode.languages.registerCompletionItemProvider(SEL, { provideCompletionItems: (d, p) => this.complete(d, p) }, "$", " ", "(", ","),
      vscode.languages.registerSignatureHelpProvider(SEL, { provideSignatureHelp: (d, p) => this.signature(d, p) }, "(", ","),
      vscode.languages.registerDocumentSymbolProvider(SEL, { provideDocumentSymbols: (d) => this.symbols(d) }),
      vscode.languages.registerDefinitionProvider(SEL, { provideDefinition: (d, p) => this.definition(d, p) }),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (e.document.languageId === "kimp" && this.ctx.setting<string>("impact.diagnostics", "onType") === "onType") this.schedule(e.document, 400);
      }),
      vscode.workspace.onDidSaveTextDocument((d) => {
        if (d.languageId === "kimp" && this.ctx.setting<string>("impact.diagnostics", "onType") !== "off") this.schedule(d, 0);
      }),
      vscode.workspace.onDidOpenTextDocument((d) => {
        if (d.languageId === "kimp" && this.ctx.setting<string>("impact.diagnostics", "onType") !== "off") this.schedule(d, 0);
      }),
      vscode.workspace.onDidCloseTextDocument((d) => this.diagnostics.delete(d.uri)),
      vscode.commands.registerCommand("kiln.newImpactProgram", () => this.newProgram()),
      vscode.commands.registerCommand("kiln.impact.cook", () => this.cook()),
      vscode.commands.registerCommand("kiln.impact.disassemble", (ref?: string) => this.disassemble(ref)),
      vscode.commands.registerCommand("kiln.impact.hash", () => this.hash()),
    );
    for (const d of vscode.workspace.textDocuments) if (d.languageId === "kimp") this.schedule(d, 0);
  }

  dispose(): void {
    for (const t of this.timers.values()) clearTimeout(t);
    for (const d of this.disposables) d.dispose();
  }

  /** natives.json files that apply to a document: next to it, the mod's impact/natives.json, cook.json's list. */
  nativesFiles(doc: vscode.TextDocument): string[] {
    if (doc.uri.scheme !== "file") return [];
    const f = doc.uri.fsPath;
    const out = new Set<string>();
    const near = path.join(path.dirname(f), "natives.json");
    if (fs.existsSync(near)) out.add(near);
    const mod = modForFile(f);
    if (mod) {
      const mi = path.join(mod.dir, "impact", "natives.json");
      if (fs.existsSync(mi)) out.add(mi);
      if (mod.impactCook) {
        try {
          const cook = JSON.parse(fs.readFileSync(mod.impactCook, "utf8"));
          for (const n of cook.natives ?? []) {
            const p = path.resolve(path.dirname(mod.impactCook), n);
            if (fs.existsSync(p)) out.add(p);
          }
        } catch {
          /* a broken cook.json: `kiln lint` reports it */
        }
      }
    }
    return [...out];
  }

  data(doc: vscode.TextDocument): ImpactData {
    const files = this.nativesFiles(doc);
    if (!files.length) return this.base;
    const key = files.map((f) => `${f}@${fs.statSync(f).mtimeMs}`).join("|");
    const c = this.modCache.get(doc.uri.toString());
    if (c?.key === key) return c.data;
    let d = this.base;
    for (const f of files) {
      try {
        d = d.withModNatives(JSON.parse(fs.readFileSync(f, "utf8")), `mod (${path.basename(path.dirname(path.dirname(f)))})`);
      } catch (e) {
        this.ctx.out.warn(`${f}: ${(e as Error).message}`);
      }
    }
    this.modCache.set(doc.uri.toString(), { key, data: d });
    return d;
  }

  // ---- diagnostics

  private schedule(doc: vscode.TextDocument, ms: number) {
    const k = doc.uri.toString();
    clearTimeout(this.timers.get(k));
    this.timers.set(k, setTimeout(() => void this.check(doc), ms));
  }

  async check(doc: vscode.TextDocument): Promise<CheckResult | undefined> {
    const root = this.ctx.kilnRoot();
    if (!root) return undefined;
    const k = doc.uri.toString();
    const g = (this.gen.get(k) ?? 0) + 1;
    this.gen.set(k, g);
    const args = [this.ctx.asset("python", "kimp_check.py"), "--impact", path.join(root, "tools", "impact"), "--path", doc.uri.fsPath || doc.fileName];
    for (const f of this.nativesFiles(doc)) args.push("--natives", f);
    const r = await this.ctx.exec(this.ctx.python(), args, { input: doc.getText(), timeoutMs: 20_000 });
    if (this.gen.get(k) !== g || doc.isClosed) return undefined;
    let res: CheckResult;
    try {
      res = JSON.parse(r.stdout);
    } catch {
      this.ctx.out.warn(`kimp check: ${r.error?.message ?? r.stderr.trim().slice(-500)}`);
      return undefined;
    }
    const diags: vscode.Diagnostic[] = [];
    const lineRange = (ln: number | null) => {
      const l = Math.max(0, Math.min(doc.lineCount - 1, (ln ?? 1) - 1));
      const t = doc.lineAt(l);
      return new vscode.Range(l, t.firstNonWhitespaceCharacterIndex, l, t.text.length);
    };
    if (res.error) {
      const d = new vscode.Diagnostic(lineRange(res.error.line), res.error.message, vscode.DiagnosticSeverity.Error);
      d.source = "impact asm";
      diags.push(d);
    }
    for (const f of res.findings) {
      const sev = f.level === "error" ? vscode.DiagnosticSeverity.Error : f.level === "warning" ? vscode.DiagnosticSeverity.Warning : vscode.DiagnosticSeverity.Information;
      const d = new vscode.Diagnostic(lineRange(f.line ?? this.programLine(doc)), f.msg, sev);
      d.source = "impact lint";
      d.code = f.code;
      diags.push(d);
    }
    this.diagnostics.set(doc.uri, diags);
    return res;
  }

  private programLine(doc: vscode.TextDocument): number {
    for (let i = 0; i < Math.min(doc.lineCount, 50); i++) if (/^\s*program\b/.test(doc.lineAt(i).text)) return i + 1;
    return 1;
  }

  // ---- language features

  hover(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | undefined {
    const line = doc.lineAt(pos.line).text;
    const code = codePart(line);
    if (pos.character > code.length) return undefined;
    const w = wordAt(code, pos.character);
    if (!w) return undefined;
    const range = new vscode.Range(pos.line, w.start, pos.line, w.end);
    const data = this.data(doc);
    const t = w.text;
    const ec = ecallAt(line, pos.character);
    const head = /^\s*(?:[A-Za-z_][\w.]*:\s*)?(\S+)/.exec(code)?.[1]?.toLowerCase();
    // native name after ecall
    const em = /^(\s*(?:[A-Za-z_][\w.]*:\s*)?ecall\s+)[^\s(]+/i.exec(code);
    if (em && w.start === em[1].length) {
      const n = data.native(t);
      return new vscode.Hover(n ? nativeMarkdown(n, data.index.errors) : new vscode.MarkdownString(`unknown native \`${t}\`: declare it in impact/natives.json (or \`argc n\`)`), range);
    }
    if (t.startsWith("$")) {
      const sym = scanSymbols(doc.getText()).vars.get(t.slice(1));
      const md = new vscode.MarkdownString();
      if (sym) {
        md.appendCodeblock(`var $${t.slice(1)} ${sym.type} ${sym.rest}`.trim(), "kimp");
        const ty = data.types.get(sym.type);
        if (ty) md.appendMarkdown(`\`${ty.qualified ?? ty.name}\` · ${ty.size ?? "?"} bytes · hash \`${ty.hash}\``);
      } else md.appendMarkdown(doc.fileName.endsWith(".kpatch") ? `\`${t}\`: a variable of the program this patch goes into` : `\`${t}\`: not declared`);
      if (ec) md.appendMarkdown(this.argNote(data, ec));
      return new vscode.Hover(md, range);
    }
    const op = data.opcodes.get(t.toLowerCase());
    if (op && head === t.toLowerCase()) {
      const md = new vscode.MarkdownString();
      md.appendCodeblock(op.syntax ?? op.name, "kimp");
      md.appendMarkdown(op.doc + (FRAME_OPS.has(op.name) ? "\n\n**Rejected by the verifier (E_FRAME).**" : ""));
      return new vscode.Hover(md, range);
    }
    const dir = data.directives.get(t);
    if (dir && head === t) {
      const md = new vscode.MarkdownString();
      md.appendCodeblock(dir.syntax ?? dir.name, "kimp");
      md.appendMarkdown(dir.doc);
      return new vscode.Hover(md, range);
    }
    const ty = data.types.get(t);
    if (ty) return new vscode.Hover(new vscode.MarkdownString(`impact type \`${ty.name}\` = \`${ty.qualified ?? ""}\` · ${ty.size ?? "?"} bytes, align ${ty.align ?? "?"} · hash \`${ty.hash}\` · ${ty.uses ?? 0} vanilla variables`), range);
    const cfgCtx = /\bconfig\s+("?)$/.exec(code.slice(0, w.start));
    if (cfgCtx) {
      if (cfgCtx[1]) {
        // config "mymod.Radius": FNV-1a of the string; the data pack sets the same id
        const id = hex32(fnv1a32(t));
        const known = data.config(id);
        return new vscode.Hover(new vscode.MarkdownString(`config id \`${id}\` = FNV-1a("${t}")${known ? ` · also used by vanilla (${known.aliases[0]})` : " · new: your data pack sets it"}`), range);
      }
      const c = data.config(t);
      if (c) return new vscode.Hover(new vscode.MarkdownString(`config \`${c.id}\` · aliases ${c.aliases.map((a) => `\`${a}\``).join(", ")} · types ${c.types.join(", ")} · ${c.programs ?? 0} vanilla programs`), range);
    }
    const lab = scanSymbols(doc.getText()).labels.get(t);
    if (lab) return new vscode.Hover(new vscode.MarkdownString(`label \`${t}\` (${lab.section}, line ${lab.line + 1})`), range);
    if (/^0x[0-9a-f]+$/i.test(t)) {
      const v = parseInt(t, 16);
      const n = data.native(t);
      return new vscode.Hover(new vscode.MarkdownString(`${t} = ${v}${n ? ` · native **${n.name}**` : ""}${data.config(t) ? ` · config ${data.config(t)!.aliases[0]}` : ""}`), range);
    }
    if (/^[0-9a-f]{8}-/i.test(t) && /^\s*program\b/.test(code)) {
      return new vscode.Hover(new vscode.MarkdownString(`program id \`${hex32(fnv1a32(guidBytes(t)))}\` (FNV-1a of the GUID's 16 bytes: how the game finds the program)`), range);
    }
    return undefined;
  }

  private argNote(data: ImpactData, ec: { native: string; arg: number }): string {
    const n = data.native(ec.native);
    const a = n?.args[ec.arg];
    return a ? `\n\nhere: \`args[${ec.arg}]\` ${a.dir} **${a.name}** ${a.types.length ? `\`${a.types.join(" | ")}\`` : ""}` : "";
  }

  complete(doc: vscode.TextDocument, pos: vscode.Position): vscode.CompletionItem[] | undefined {
    const line = doc.lineAt(pos.line).text;
    const before = line.slice(0, pos.character);
    if (before.includes(";")) return undefined;
    const data = this.data(doc);
    const syms = scanSymbols(doc.getText());
    const vars = () =>
      [...syms.vars].map(([name, v]) => {
        const it = new vscode.CompletionItem(`$${name}`, vscode.CompletionItemKind.Variable);
        it.detail = v.type;
        const dollar = before.lastIndexOf("$");
        if (dollar >= 0 && /^\$[\w.:]*$/.test(before.slice(dollar))) it.range = new vscode.Range(pos.line, dollar, pos.line, pos.character);
        return it;
      });
    const ec = ecallAt(line, pos.character);
    if (ec) {
      const items = vars();
      const u = new vscode.CompletionItem("_", vscode.CompletionItemKind.Keyword);
      u.detail = "unconnected (optional input falls back to its default)";
      items.push(u);
      return items;
    }
    const inHeader = !/^\s*(code|shutdown):/m.test(doc.getText(new vscode.Range(0, 0, pos.line, 0)));
    const ecallName = /^\s*(?:[A-Za-z_][\w.]*:\s*)?ecall\s+([\w.]*)$/i.exec(before);
    if (ecallName) {
      return data.nativeList.map((n) => {
        const it = new vscode.CompletionItem({ label: n.name, description: n.origin === "vanilla" ? `argc ${n.argc}` : n.origin }, vscode.CompletionItemKind.Function);
        it.insertText = new vscode.SnippetString(`${n.name}(${n.args.map((a, i) => `\${${i + 1}:${a.dir === "out" ? "$" : "_"}}`).join(", ")})`);
        it.detail = nativeSignature(n);
        it.documentation = nativeMarkdown(n);
        it.sortText = (n.origin === "vanilla" ? "1" : "0") + n.name.toLowerCase();
        return it;
      });
    }
    const branch = /^\s*(?:[A-Za-z_][\w.]*:\s*)?br[tf]?\s+(\w*)$/i.exec(before);
    if (branch) return [...syms.labels.keys()].map((l) => new vscode.CompletionItem(l, vscode.CompletionItemKind.Reference));
    if (/^\s*var\s+\$\S+\s+[\w.]*$/.test(before)) {
      return [...data.types.values()].map((t) => {
        const it = new vscode.CompletionItem({ label: t.name, description: `${t.size ?? "?"} B` }, vscode.CompletionItemKind.TypeParameter);
        it.detail = t.qualified;
        it.sortText = String(1e6 - (t.uses ?? 0)).padStart(7, "0");
        return it;
      });
    }
    if (/\bconfig\s+[\w.]*$/.test(before)) {
      const seen = new Set<string>();
      const out: vscode.CompletionItem[] = [];
      for (const c of data.index.configs) {
        const a = c.aliases[0];
        if (!a || seen.has(a)) continue;
        seen.add(a);
        const it = new vscode.CompletionItem({ label: a, description: c.types.join(", ") }, vscode.CompletionItemKind.Constant);
        it.detail = `config ${c.id} · ${c.programs ?? 0} programs`;
        it.sortText = String(1e6 - (c.programs ?? 0)).padStart(7, "0");
        out.push(it);
      }
      return out;
    }
    if (/^\s*streams\s+/.test(before)) return STREAMS.map((s) => new vscode.CompletionItem(s, vscode.CompletionItemKind.EnumMember));
    if (/^\s*(?:gload|gstore|iconst|copy)\s+[$\w.]*$/i.test(before) || /\$[\w.:]*$/.test(before)) return vars();
    // first word: an instruction (or a directive / section in the header)
    if (/^\s*(?:[A-Za-z_][\w.]*:\s+)?[\w.]*$/.test(before)) {
      const out: vscode.CompletionItem[] = [];
      for (const o of OPCODES) {
        if (FRAME_OPS.has(o.name)) continue;
        const it = new vscode.CompletionItem(o.name, vscode.CompletionItemKind.Keyword);
        it.detail = o.syntax ?? o.name;
        it.documentation = o.doc;
        out.push(it);
      }
      if (inHeader) {
        for (const d of DIRECTIVES) {
          const it = new vscode.CompletionItem(d.name, vscode.CompletionItemKind.Property);
          it.detail = d.syntax ?? d.name;
          it.documentation = d.doc;
          out.push(it);
        }
      }
      for (const s of ["code:", "shutdown:"]) out.push(new vscode.CompletionItem(s, vscode.CompletionItemKind.Module));
      return out;
    }
    return undefined;
  }

  signature(doc: vscode.TextDocument, pos: vscode.Position): vscode.SignatureHelp | undefined {
    const ec = ecallAt(doc.lineAt(pos.line).text, pos.character);
    if (!ec) return undefined;
    const n = this.data(doc).native(ec.native);
    if (!n) return undefined;
    const sig = new vscode.SignatureInformation(nativeSignature(n), new vscode.MarkdownString(n.doc ?? ""));
    sig.parameters = n.args.map(
      (a, i) =>
        new vscode.ParameterInformation(
          `${a.dir === "out" ? "out " : a.dir === "inout" ? "inout " : ""}${a.name}: ${a.types.slice(0, 3).join("|") || "?"}`,
          new vscode.MarkdownString(`\`args[${i}]\` ${a.types.join(" | ")}${a.raw ? " (raw integer ok)" : ""}${a.doc ? `\n\n${a.doc}` : ""}`),
        ),
    );
    const help = new vscode.SignatureHelp();
    help.signatures = [sig];
    help.activeSignature = 0;
    help.activeParameter = Math.min(ec.arg, Math.max(0, n.args.length - 1));
    return help;
  }

  symbols(doc: vscode.TextDocument): vscode.DocumentSymbol[] {
    const s = scanSymbols(doc.getText());
    const out: vscode.DocumentSymbol[] = [];
    for (const [name, v] of s.vars) {
      const r = new vscode.Range(v.line, 0, v.line, doc.lineAt(v.line).text.length);
      out.push(new vscode.DocumentSymbol(`$${name}`, v.type, vscode.SymbolKind.Variable, r, new vscode.Range(v.line, v.col, v.line, v.col + name.length + 1)));
    }
    for (const [name, l] of s.labels) {
      const r = new vscode.Range(l.line, l.col, l.line, l.col + name.length);
      out.push(new vscode.DocumentSymbol(name, l.section, vscode.SymbolKind.Key, r, r));
    }
    return out;
  }

  definition(doc: vscode.TextDocument, pos: vscode.Position): vscode.Location | undefined {
    const w = wordAt(codePart(doc.lineAt(pos.line).text), pos.character);
    if (!w) return undefined;
    const s = scanSymbols(doc.getText());
    if (w.text.startsWith("$")) {
      const v = s.vars.get(w.text.slice(1));
      if (v) return new vscode.Location(doc.uri, new vscode.Position(v.line, v.col));
    }
    const l = s.labels.get(w.text);
    if (l) return new vscode.Location(doc.uri, new vscode.Position(l.line, l.col));
    return undefined;
  }

  // ---- commands

  private async newProgram() {
    const ed = vscode.window.activeTextEditor;
    const mod = ed?.document.uri.scheme === "file" ? modForFile(ed.document.uri.fsPath) : undefined;
    const name = await vscode.window.showInputBox({ title: "New Impact program: file name (without .kimp)", value: "my_program", validateInput: (v) => (/^[A-Za-z0-9_.-]+$/.test(v) ? undefined : "letters, digits, _ . -") });
    if (!name) return;
    const guid = crypto.randomUUID();
    const text =
      `.kimp 1\nprogram ${guid}     ; fixed forever (the game finds programs by FNV-1a of it)\nstack 256\nstreams none\n\n` +
      `var $self   ecs.EntityId\n\ncode:\n        ecall getSelf($self)\n        pop\n        halt\n`;
    if (mod) {
      const file = path.join(mod.dir, "impact", `${name}.kimp`);
      if (fs.existsSync(file)) {
        vscode.window.showErrorMessage(`${file} exists`);
        return;
      }
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, text);
      await vscode.window.showTextDocument(vscode.Uri.file(file));
      vscode.window.showInformationMessage(`Kiln: add { "op": "assemble", "src": "${name}.kimp", "stem": "${name}" } to ${mod.id}'s impact/cook.json steps.`);
    } else {
      const doc = await vscode.workspace.openTextDocument({ language: "kimp", content: text });
      await vscode.window.showTextDocument(doc);
    }
  }

  private async cook() {
    const root = this.ctx.kilnRoot();
    if (!root) {
      vscode.window.showErrorMessage("Kiln: cooking needs the Kiln checkout (kiln.root) for tools/impact.");
      return;
    }
    const files = await vscode.workspace.findFiles("**/impact/cook.json", "**/{bin,obj,.out}/**", 100);
    const ed = vscode.window.activeTextEditor?.document.uri.fsPath;
    let recipe = files.find((f) => ed && ed.startsWith(path.dirname(path.dirname(f.fsPath)) + path.sep))?.fsPath;
    if (!recipe && files.length === 1) recipe = files[0].fsPath;
    if (!recipe && files.length) recipe = (await vscode.window.showQuickPick(files.map((f) => ({ label: vscode.workspace.asRelativePath(f), f })), { title: "Cook which recipe?" }))?.f.fsPath;
    if (!recipe) {
      if (!files.length) vscode.window.showInformationMessage("Kiln: no impact/cook.json in this workspace.");
      return;
    }
    const task = new vscode.Task(
      { type: "kiln", task: "cook", project: recipe },
      vscode.workspace.getWorkspaceFolder(vscode.Uri.file(recipe)) ?? vscode.TaskScope.Workspace,
      `cook ${vscode.workspace.asRelativePath(recipe)}`,
      "kiln",
      new vscode.ProcessExecution(this.ctx.python(), [path.join(root, "tools", "impact", "impact.py"), "cook", recipe, "--update-manifest"], { cwd: path.dirname(recipe) }),
      ["$kiln-impact"],
    );
    await vscode.tasks.executeTask(task);
  }

  private async disassemble(ref?: string) {
    const root = this.ctx.kilnRoot();
    if (!root) {
      vscode.window.showErrorMessage("Kiln: disassembling needs the Kiln checkout (kiln.root) for tools/impact.");
      return;
    }
    const r0 =
      ref ??
      (await vscode.window.showInputBox({
        title: "Disassemble: a vanilla program GUID (read from your installed game) or a .bin path",
        placeHolder: "80a2448f-945c-45f6-8fce-9b09ed61620c",
      }));
    if (!r0) return;
    const r = await this.ctx.exec(this.ctx.python(), [path.join(root, "tools", "impact", "impact.py"), "dis", r0.trim()], { timeoutMs: 120_000 });
    if (r.code !== 0) {
      vscode.window.showErrorMessage(`Kiln: impact dis: ${(r.stderr || r.stdout).trim().slice(-400)}`);
      return;
    }
    // an untitled document: vanilla listings are game data, never saved into a mod
    const doc = await vscode.workspace.openTextDocument({ language: "kimp", content: r.stdout });
    await vscode.window.showTextDocument(doc, { preview: false });
  }

  private async hash() {
    const t = await vscode.window.showInputBox({ title: "FNV-1a-32 of (a native name, config id string, type name, or a GUID for its program id)" });
    if (!t) return;
    const isGuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(t.trim());
    const h = hex32(isGuid ? fnv1a32(guidBytes(t.trim())) : fnv1a32(t));
    const act = await vscode.window.showInformationMessage(`${isGuid ? "program id of" : "FNV-1a"} ${t}: ${h}`, "Copy");
    if (act) vscode.env.clipboard.writeText(h);
  }
}
