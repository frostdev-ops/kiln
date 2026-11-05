// SPDX-License-Identifier: MIT
// Kiln.Testing in the Testing view: [KilnTest] methods found in each mod's C# files; runs go to the selected test
// server (`kiln test <id> [filter] --json`, else the control folder `test <id> [filter]`), results come from
// kiln/logs/tests/<id>.json; the mod's kiln.log lines stream into the run's output.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { ControlError, sendControl, TestReport, testReportPath } from "../core/control";
import { findTests } from "../core/csharpTests";
import { parseLogLine, winToPosix } from "../core/logLine";
import { ModInfo } from "../core/project";
import { protectedReason } from "../core/servers";
import { Ctx } from "../ctx";
import { colorLogLine } from "../core/consoleFormat";
import { Mods } from "./mods";
import { Servers } from "./servers";

interface Meta {
  kind: "mod" | "class" | "test";
  mod: ModInfo;
  name?: string;
}

export class Testing implements vscode.Disposable {
  readonly controller: vscode.TestController;
  private meta = new WeakMap<vscode.TestItem, Meta>();
  private disposables: vscode.Disposable[] = [];
  private refreshTimer?: NodeJS.Timeout;
  /** outcome of each test item id in the last run (pass, fail, skip, error): the integration tests read it */
  readonly lastResults = new Map<string, string>();

  constructor(
    private readonly ctx: Ctx,
    private readonly mods: Mods,
    private readonly servers: Servers,
  ) {
    const c = vscode.tests.createTestController("kiln", "Kiln.Testing");
    this.controller = c;
    c.resolveHandler = async () => this.discover();
    c.refreshHandler = async () => this.discover();
    c.createRunProfile("Deploy, reload and run on the test server", vscode.TestRunProfileKind.Run, (r, t) => this.run(r, t, true), true);
    c.createRunProfile("Run on the test server (as deployed)", vscode.TestRunProfileKind.Run, (r, t) => this.run(r, t, false), false);
    const w = vscode.workspace.createFileSystemWatcher("**/*.cs");
    const later = () => {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = setTimeout(() => void this.discover(), 800);
    };
    this.disposables.push(c, w, w.onDidChange(later), w.onDidCreate(later), w.onDidDelete(later), mods.onDidChangeTreeData(later));
  }

  dispose(): void {
    clearTimeout(this.refreshTimer);
    for (const d of this.disposables) d.dispose();
  }

  async discover(): Promise<void> {
    const mods = await this.mods.ready();
    const seen = new Set<string>();
    for (const m of mods) {
      if (!m.project) continue;
      const files = await vscode.workspace.findFiles(new vscode.RelativePattern(m.dir, "**/*.cs"), "**/{bin,obj}/**", 500);
      const tests: { file: vscode.Uri; t: ReturnType<typeof findTests>[number] }[] = [];
      for (const f of files) {
        const open = vscode.workspace.textDocuments.find((d) => d.uri.toString() === f.toString());
        let text: string;
        try {
          text = open ? open.getText() : await fs.promises.readFile(f.fsPath, "utf8");
        } catch {
          continue;
        }
        if (!text.includes("KilnTest")) continue;
        for (const t of findTests(text)) tests.push({ file: f, t });
      }
      if (!tests.length && !m.testing) continue;
      const modItem = this.controller.items.get(m.id) ?? this.controller.createTestItem(m.id, m.id, vscode.Uri.file(m.manifest ?? m.project));
      modItem.description = m.version;
      this.meta.set(modItem, { kind: "mod", mod: m });
      this.controller.items.add(modItem);
      seen.add(m.id);
      const classes = new Map<string, vscode.TestItem>();
      for (const { file, t } of tests) {
        const cid = `${m.id}::${t.className || "(global)"}`;
        let ci = classes.get(cid);
        if (!ci) {
          ci = modItem.children.get(cid) ?? this.controller.createTestItem(cid, t.className || "(global)", file);
          this.meta.set(ci, { kind: "class", mod: m });
          ci.children.replace([]);
          classes.set(cid, ci);
        }
        const id = `${m.id}::${t.name}`;
        const ti = this.controller.createTestItem(id, t.name === `${t.className}.${t.method}` ? t.method : t.name, file);
        ti.range = new vscode.Range(t.methodLine, t.methodCol, t.methodLine, t.methodCol + t.method.length);
        ti.description = t.skip ? `skip: ${t.skip}` : undefined;
        ti.sortText = String(t.methodLine).padStart(6, "0");
        this.meta.set(ti, { kind: "test", mod: m, name: t.name });
        ci.children.add(ti);
      }
      modItem.children.replace([...classes.values()]);
    }
    this.controller.items.forEach((it) => {
      if (!seen.has(it.id)) this.controller.items.delete(it.id);
    });
  }

  /** For kiln.log links: the test with this reported name. */
  locate(mod: string | undefined, name: string): vscode.Location | undefined {
    let found: vscode.Location | undefined;
    this.controller.items.forEach((mi) => {
      if (found || (mod && mi.id !== mod)) return;
      mi.children.forEach((ci) =>
        ci.children.forEach((ti) => {
          if (!found && this.meta.get(ti)?.name === name && ti.uri && ti.range) found = new vscode.Location(ti.uri, ti.range);
        }),
      );
    });
    return found;
  }

  private leaves(item: vscode.TestItem, out: vscode.TestItem[] = []): vscode.TestItem[] {
    if (this.meta.get(item)?.kind === "test") out.push(item);
    item.children.forEach((c) => this.leaves(c, out));
    return out;
  }

  async run(req: vscode.TestRunRequest, token: vscode.CancellationToken, deploy: boolean): Promise<void> {
    await this.discover();
    const roots: vscode.TestItem[] = [];
    if (req.include) roots.push(...req.include);
    else this.controller.items.forEach((i) => roots.push(i));
    const excluded = new Set((req.exclude ?? []).flatMap((e) => this.leaves(e)).map((t) => t.id));
    // group requested tests by mod
    const byMod = new Map<string, { mod: ModInfo; tests: vscode.TestItem[]; whole: boolean }>();
    for (const r of roots) {
      const meta = this.meta.get(r);
      if (!meta) continue;
      const g = byMod.get(meta.mod.id) ?? { mod: meta.mod, tests: [], whole: false };
      if (meta.kind === "mod") g.whole = true;
      g.tests.push(...this.leaves(r).filter((t) => !excluded.has(t.id)));
      byMod.set(meta.mod.id, g);
    }
    const run = this.controller.createTestRun(req, undefined, true);
    this.lastResults.clear();
    const server = await this.servers.require("run tests on");
    if (!server || protectedReason(server.dir)) {
      for (const g of byMod.values()) for (const t of g.tests) run.errored(t, new vscode.TestMessage("no writable test server selected"));
      run.end();
      return;
    }
    try {
      for (const g of byMod.values()) {
        if (token.isCancellationRequested) break;
        for (const t of g.tests) run.enqueued(t);
        if (deploy && g.mod.project) {
          run.appendOutput(`\x1b[2m> deploy and reload ${g.mod.id} -> ${server.name}\x1b[0m\r\n`);
          const code = await this.mods.runTask("deploy", g.mod, true);
          if (code !== 0) {
            for (const t of g.tests) run.errored(t, new vscode.TestMessage(`deploy of ${g.mod.id} failed (exit ${code})`));
            continue;
          }
          await this.waitForReload(server.dir, g.mod.id, token);
        }
        await this.runMod(run, g, server.dir, token);
      }
    } finally {
      run.end();
    }
  }

  /** After a deploy with reload, give the server a moment to load the new build (kiln.log `mod <id> … loaded`). */
  private async waitForReload(serverDir: string, id: string, token: vscode.CancellationToken) {
    const log = path.join(serverDir, "kiln", "logs", "kiln.log");
    const start = Date.now();
    let size = 0;
    try {
      size = fs.statSync(log).size;
    } catch {
      return;
    }
    const rx = new RegExp(`(mod ${id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")} \\S+ loaded|reloaded ${id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`);
    while (Date.now() - start < 8000 && !token.isCancellationRequested) {
      await new Promise((r) => setTimeout(r, 250));
      try {
        const st = fs.statSync(log);
        if (st.size > size) {
          const fd = fs.openSync(log, "r");
          const buf = Buffer.alloc(st.size - size);
          fs.readSync(fd, buf, 0, buf.length, size);
          fs.closeSync(fd);
          if (rx.test(buf.toString("utf8"))) return;
        }
      } catch {
        return;
      }
    }
  }

  private async runMod(run: vscode.TestRun, g: { mod: ModInfo; tests: vscode.TestItem[]; whole: boolean }, serverDir: string, token: vscode.CancellationToken) {
    const id = g.mod.id;
    const names = g.tests.map((t) => this.meta.get(t)!.name!);
    const filter = !g.whole && names.length === 1 ? names[0] : undefined;
    for (const t of g.tests) run.started(t);
    const tail = this.tailLog(serverDir, id, run);
    const ac = new AbortController();
    const cancel = token.onCancellationRequested(() => ac.abort());
    let report: TestReport | undefined;
    let error: string | undefined;
    const timeoutS = this.ctx.setting("tests.timeoutSeconds", 240);
    const reportFile = testReportPath(serverDir, id);
    let before = 0;
    try {
      before = fs.statSync(reportFile).mtimeMs;
    } catch {
      /* no earlier report */
    }
    try {
      if (await this.ctx.hasCli()) {
        const args = ["test", id, ...(filter ? ["--exact", filter] : []), "--server", serverDir, "--timeout", String(timeoutS), "--json"];
        const r = await this.ctx.exec(this.ctx.cliPath(), args, { timeoutMs: (timeoutS + 30) * 1000, signal: ac.signal });
        const j = JSON.parse(r.stdout.slice(r.stdout.indexOf("{")) || "{}");
        if (Array.isArray(j.results)) report = j as TestReport;
        else error = j.error?.message ?? (r.stderr.trim() || `kiln test exited ${r.code}`);
      } else {
        // an auto-run (tests.autoRun after a hot reload) may still be going: wait for it, then start ours
        const deadline = Date.now() + timeoutS * 1000;
        let res = await sendControl(serverDir, `test ${id}${filter ? ` ${filter}` : ""}`, { timeoutMs: timeoutS * 1000, signal: ac.signal });
        while (!res.ok && /already running/.test(res.message) && Date.now() < deadline && !ac.signal.aborted) {
          run.appendOutput(`\x1b[2m(${res.message}; waiting)\x1b[0m\r\n`);
          await new Promise((r) => setTimeout(r, 1000));
          before = Date.now() - 1;
          res = await sendControl(serverDir, `test ${id}${filter ? ` ${filter}` : ""}`, { timeoutMs: Math.max(5000, deadline - Date.now()), signal: ac.signal });
        }
        try {
          if (fs.statSync(reportFile).mtimeMs >= before) report = JSON.parse(fs.readFileSync(reportFile, "utf8"));
        } catch {
          /* fall back to the control result */
        }
        if (!report && res.data && Array.isArray((res.data as TestReport).results)) {
          const d = res.data as TestReport;
          report = { mod: id, ok: res.ok, passed: d.passed, failed: d.failed, skipped: d.skipped, results: d.results };
        }
        if (!report) error = res.message;
      }
    } catch (e) {
      error = e instanceof ControlError || e instanceof Error ? e.message : String(e);
    } finally {
      cancel.dispose();
      await new Promise((r) => setTimeout(r, 600)); // the last log lines
      tail.dispose();
    }
    const byName = new Map((report?.results ?? []).map((r) => [r.name, r]));
    for (const t of g.tests) {
      const name = this.meta.get(t)!.name!;
      const r = byName.get(name);
      if (!r) {
        run.errored(t, new vscode.TestMessage(error ?? `${name}: not in the report (is the deployed build current? did the mod load?)`));
        this.lastResults.set(t.id, "error");
        continue;
      }
      const ms = r.ms ?? undefined;
      this.lastResults.set(t.id, r.outcome === "timeout" ? "fail" : r.outcome);
      if (r.outcome === "pass") run.passed(t, ms);
      else if (r.outcome === "skip") run.skipped(t);
      else {
        const msg = new vscode.TestMessage(`${r.outcome === "timeout" ? "(timeout) " : ""}${r.message ?? "failed"}${r.stack ? `\n${r.stack}` : ""}`);
        msg.location = this.stackLocation(r.stack, g.mod.dir) ?? (t.uri && t.range ? new vscode.Location(t.uri, t.range) : undefined);
        run.failed(t, msg, ms);
      }
    }
    if (report) run.appendOutput(`\r\n${report.passed} passed, ${report.failed} failed, ${report.skipped} skipped${report.ms ? ` in ${(report.ms / 1000).toFixed(2)} s` : ""}\r\n`);
    else if (error) run.appendOutput(`\r\n\x1b[31m${error}\x1b[0m\r\n`);
  }

  private stackLocation(stack: string | null | undefined, modDir: string): vscode.Location | undefined {
    if (!stack) return undefined;
    for (const m of stack.matchAll(/ in ((?:[A-Za-z]:\\|\/)[^\n]*?):line (\d+)/g)) {
      const p = winToPosix(m[1]);
      if (p.startsWith(modDir) && fs.existsSync(p)) return new vscode.Location(vscode.Uri.file(p), new vscode.Position(Number(m[2]) - 1, 0));
    }
    return undefined;
  }

  /** Streams new kiln.log lines of this mod (and the host's lines about it) into the run output. */
  private tailLog(serverDir: string, id: string, run: vscode.TestRun): vscode.Disposable {
    const file = path.join(serverDir, "kiln", "logs", "kiln.log");
    let off = 0;
    try {
      off = fs.statSync(file).size;
    } catch {
      /* none yet */
    }
    let rest = "";
    const t = setInterval(() => {
      try {
        const st = fs.statSync(file);
        if (st.size < off) off = 0;
        if (st.size === off) return;
        const fd = fs.openSync(file, "r");
        const buf = Buffer.alloc(st.size - off);
        fs.readSync(fd, buf, 0, buf.length, off);
        fs.closeSync(fd);
        off = st.size;
        const lines = (rest + buf.toString("utf8")).split(/\r?\n/);
        rest = lines.pop() ?? "";
        for (const l of lines) {
          const p = parseLogLine(l);
          if (p && (p.tag === `mod:${id}` || (p.tag === "host" && p.text.includes(id)))) run.appendOutput(colorLogLine(l) + "\r\n");
        }
      } catch {
        /* keep trying */
      }
    }, 250);
    return { dispose: () => clearInterval(t) };
  }
}
