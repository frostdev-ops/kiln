// SPDX-License-Identifier: MIT
// Workspace mods (kiln.mod.json folders and Kiln.Sdk projects), the Mods view, and build / deploy / reload / pack as
// tasks (`kiln` CLI when present, else dotnet + the control folder) with the KILN problem matchers.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { sendControl } from "../core/control";
import { ModInfo, modForFile, modFromDir } from "../core/project";
import { Ctx } from "../ctx";
import { Servers } from "./servers";

export const MATCHERS = ["$kiln-sdk", "$kiln-sdk-file", "$kiln-sdk-project", "$kiln-dotnet"];

export interface KilnTaskDef extends vscode.TaskDefinition {
  type: "kiln";
  task: "build" | "deploy" | "pack" | "lint" | "cook";
  project?: string;
  reload?: boolean;
  server?: string;
  configuration?: string;
}

export class Mods implements vscode.TreeDataProvider<ModInfo>, vscode.TaskProvider, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<ModInfo | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private mods: ModInfo[] = [];
  private scanning?: Promise<void>;
  private disposables: vscode.Disposable[] = [];

  constructor(
    private readonly ctx: Ctx,
    private readonly servers: Servers,
  ) {
    const watcher = vscode.workspace.createFileSystemWatcher("**/{kiln.mod.json,*.csproj}");
    this.disposables.push(
      this.changed,
      watcher,
      watcher.onDidCreate(() => this.rescan()),
      watcher.onDidDelete(() => this.rescan()),
      watcher.onDidChange(() => this.rescan()),
      vscode.window.registerTreeDataProvider("kiln.mods", this),
      vscode.tasks.registerTaskProvider("kiln", this),
      vscode.window.onDidChangeActiveTextEditor(() => this.updateContext()),
      vscode.commands.registerCommand("kiln.mods.refresh", () => this.rescan()),
      vscode.commands.registerCommand("kiln.mods.open", (m: ModInfo) => this.open(m)),
      vscode.commands.registerCommand("kiln.build", (m?: ModInfo) => this.runTask("build", m)),
      vscode.commands.registerCommand("kiln.deploy", (m?: ModInfo) => this.runTask("deploy", m)),
      vscode.commands.registerCommand("kiln.deployReload", (m?: ModInfo) => this.runTask("deploy", m, true)),
      vscode.commands.registerCommand("kiln.pack", (m?: ModInfo) => this.runTask("pack", m)),
      vscode.commands.registerCommand("kiln.reload", (m?: ModInfo) => this.reload(m)),
      vscode.commands.registerCommand("kiln.control", () => this.control()),
    );
    this.rescan();
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  all(): ModInfo[] {
    return this.mods;
  }

  async ready(): Promise<ModInfo[]> {
    await this.scanning;
    return this.mods;
  }

  rescan(): Promise<void> {
    this.scanning = (async () => {
      const files = await vscode.workspace.findFiles("**/{kiln.mod.json,*.csproj}", "**/{bin,obj,node_modules,.git,.out}/**", 2000);
      const dirs = new Set(files.map((f) => path.dirname(f.fsPath)));
      const mods: ModInfo[] = [];
      for (const d of dirs) {
        const m = modFromDir(d);
        if (m && (m.project || m.manifest)) mods.push(m);
      }
      mods.sort((a, b) => a.id.localeCompare(b.id));
      this.mods = mods;
      this.changed.fire(undefined);
      this.updateContext();
    })();
    return this.scanning;
  }

  private updateContext() {
    const f = vscode.window.activeTextEditor?.document.uri;
    vscode.commands.executeCommand("setContext", "kiln.inMod", !!(f?.scheme === "file" && this.modFor(f.fsPath)));
  }

  modFor(file: string): ModInfo | undefined {
    const known = this.mods
      .filter((m) => file === m.dir || file.startsWith(m.dir + path.sep))
      .sort((a, b) => b.dir.length - a.dir.length)[0];
    return known ?? modForFile(file, vscode.workspace.getWorkspaceFolder(vscode.Uri.file(file))?.uri.fsPath);
  }

  /** The mod of the active editor, the only mod, or ask. */
  async pickMod(purpose: string, filter?: (m: ModInfo) => boolean): Promise<ModInfo | undefined> {
    await this.scanning;
    const f = vscode.window.activeTextEditor?.document.uri;
    const active = f?.scheme === "file" ? this.modFor(f.fsPath) : undefined;
    if (active && (!filter || filter(active))) return active;
    const list = this.mods.filter((m) => !filter || filter(m));
    if (list.length === 1) return list[0];
    if (!list.length) {
      vscode.window.showWarningMessage(`Kiln: no mod to ${purpose} in this workspace.`);
      return undefined;
    }
    const pick = await vscode.window.showQuickPick(
      list.map((m) => ({ label: m.id, description: m.version, detail: vscode.workspace.asRelativePath(m.dir), m })),
      { title: `Mod to ${purpose}` },
    );
    return pick?.m;
  }

  getTreeItem(m: ModInfo): vscode.TreeItem {
    const it = new vscode.TreeItem(m.id, vscode.TreeItemCollapsibleState.None);
    it.description = [m.version, m.side, m.project ? "" : "manifest only", m.testing ? "tests" : ""].filter(Boolean).join(" · ");
    it.tooltip = new vscode.MarkdownString(`**${m.name}** \`${m.id}\`\n\n${vscode.workspace.asRelativePath(m.dir)}`);
    it.iconPath = new vscode.ThemeIcon(m.project ? "package" : "json");
    it.contextValue = m.project ? "kiln.mod.code" : "kiln.mod.data";
    it.command = { command: "kiln.mods.open", title: "Open", arguments: [m] };
    it.resourceUri = vscode.Uri.file(m.manifest ?? m.project ?? m.dir);
    return it;
  }

  getChildren(el?: ModInfo): ModInfo[] {
    return el ? [] : this.mods;
  }

  private async open(m: ModInfo) {
    const f = m.manifest ?? m.project;
    if (f) await vscode.window.showTextDocument(vscode.Uri.file(f), { preview: true });
  }

  // ---- tasks

  async provideTasks(): Promise<vscode.Task[]> {
    await this.scanning;
    const tasks: vscode.Task[] = [];
    for (const m of this.mods) {
      if (m.project) {
        for (const t of ["build", "deploy", "pack"] as const) {
          const task = await this.makeTask({ type: "kiln", task: t, project: m.project }, m);
          if (task) tasks.push(task);
        }
      }
      if (m.impactCook) {
        const task = await this.makeTask({ type: "kiln", task: "cook", project: m.impactCook }, m);
        if (task) tasks.push(task);
      }
    }
    return tasks;
  }

  async resolveTask(task: vscode.Task): Promise<vscode.Task | undefined> {
    const def = task.definition as KilnTaskDef;
    const m = def.project ? this.modFor(path.resolve(def.project)) ?? modFromDir(path.dirname(path.resolve(def.project))) : await this.pickMod(def.task);
    if (!m) return undefined;
    return this.makeTask(def, m, task.scope);
  }

  private async makeTask(def: KilnTaskDef, m: ModInfo, scope?: vscode.TaskScope | vscode.WorkspaceFolder): Promise<vscode.Task | undefined> {
    const cli = await this.ctx.hasCli();
    const cfgArgs = def.configuration ? ["-c", def.configuration] : [];
    const extra = this.ctx.setting<string[]>("dotnet.buildArgs", []);
    const proj = def.project && def.task !== "cook" ? path.resolve(def.project) : m.project;
    let cmd: string;
    let args: string[];
    let name = `${def.task} ${m.id}`;
    let matchers = MATCHERS;
    if (def.task === "cook") {
      const root = this.ctx.kilnRoot();
      if (!root) return undefined;
      cmd = this.ctx.python();
      args = [path.join(root, "tools", "impact", "impact.py"), "cook", def.project ?? m.impactCook!, "--update-manifest"];
      matchers = ["$kiln-impact"];
    } else if (def.task === "lint") {
      cmd = this.ctx.cliPath();
      args = ["lint", def.project ?? m.dir];
      matchers = ["$kiln-lint"];
    } else if (cli && !extra.length) {
      // kiln.dotnet.buildArgs set: dotnet directly (the CLI has no pass-through for them)
      cmd = this.ctx.cliPath();
      args = [def.task, ...(proj ? [proj] : [m.dir]), ...cfgArgs];
      if (def.task === "deploy") {
        const s = this.serverFor(def);
        if (!s) return undefined;
        args.push("--server", s.dir);
        if (def.reload) args.push("--reload");
        name = `deploy ${m.id} -> ${s.name}${def.reload ? " (reload)" : ""}`;
      }
    } else {
      if (!proj) return undefined;
      cmd = this.ctx.dotnet();
      args = ["build", proj, "-nologo", "-tl:off", "-p:GenerateFullPaths=true", ...cfgArgs, ...extra];
      if (def.task === "deploy") {
        const s = this.serverFor(def);
        if (!s) return undefined;
        args.push("-t:KilnDeploy", `-p:KilnServer=${s.dir}`);
        if (def.reload) args.push("-p:KilnReload=true");
        name = `deploy ${m.id} -> ${s.name}${def.reload ? " (reload)" : ""}`;
      } else if (def.task === "pack") args.push("-t:KilnPack");
    }
    const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(m.dir));
    const t = new vscode.Task(
      def,
      scope ?? folder ?? vscode.TaskScope.Workspace,
      name,
      "kiln",
      new vscode.ProcessExecution(cmd, args, { cwd: m.dir }),
      matchers,
    );
    if (def.task === "build") t.group = vscode.TaskGroup.Build;
    t.presentationOptions = { reveal: vscode.TaskRevealKind.Silent, clear: true, showReuseMessage: false };
    return t;
  }

  private serverFor(def: KilnTaskDef) {
    const all = this.servers.list();
    return def.server ? all.find((s) => s.name === def.server) : this.servers.selected();
  }

  /** Runs a task and resolves with its exit code. */
  async execute(task: vscode.Task): Promise<number | undefined> {
    const exec = await vscode.tasks.executeTask(task);
    return new Promise((resolve) => {
      const d = vscode.tasks.onDidEndTaskProcess((e) => {
        if (e.execution === exec) {
          d.dispose();
          resolve(e.exitCode);
        }
      });
    });
  }

  async runTask(kind: "build" | "deploy" | "pack", m?: ModInfo, reload = false): Promise<number | undefined> {
    const mod = m ?? (await this.pickMod(kind, (x) => !!x.project || kind === "deploy"));
    if (!mod) return undefined;
    let server;
    if (kind === "deploy") {
      server = await this.servers.require(reload ? "deploy and reload" : "deploy");
      if (!server) return undefined;
    }
    if (kind === "deploy" && !mod.project && !(await this.ctx.hasCli())) return this.copyDataMod(mod, server!.dir, reload);
    const task = await this.makeTask({ type: "kiln", task: kind, project: mod.project, reload, server: server?.name }, mod);
    if (!task) {
      vscode.window.showErrorMessage(`Kiln: cannot ${kind} ${mod.id} (no project).`);
      return undefined;
    }
    await vscode.workspace.saveAll(false);
    const code = await this.execute(task);
    if (code === 0) {
      vscode.window.setStatusBarMessage(`Kiln: ${task.name} ok`, 5000);
      if (kind === "deploy") this.servers.refresh();
    } else vscode.window.showErrorMessage(`Kiln: ${task.name} failed (exit ${code}). See the terminal and the Problems view.`);
    return code;
  }

  /** A mod without a project (and no CLI): copy its folder into <server>/kiln/mods/<id>. */
  private async copyDataMod(m: ModInfo, serverDir: string, reload: boolean): Promise<number> {
    const dest = path.join(serverDir, "kiln", "mods", m.id);
    const skip = new Set(["bin", "obj", "node_modules"]);
    const copy = (from: string, to: string) => {
      fs.mkdirSync(to, { recursive: true });
      for (const e of fs.readdirSync(from, { withFileTypes: true })) {
        if (e.name.startsWith(".") || skip.has(e.name)) continue;
        const a = path.join(from, e.name);
        const b = path.join(to, e.name);
        if (e.isDirectory()) copy(a, b);
        else if (!/\.(cs|csproj)$/.test(e.name)) fs.copyFileSync(a, b);
      }
    };
    copy(m.dir, dest);
    this.ctx.out.info(`deploy ${m.id}: copied ${m.dir} -> ${dest}`);
    if (reload) await this.reload(m);
    else vscode.window.setStatusBarMessage(`Kiln: ${m.id} copied to ${dest}`, 5000);
    return 0;
  }

  async reload(m?: ModInfo): Promise<boolean> {
    const mod = m ?? (await this.pickMod("reload"));
    if (!mod) return false;
    const s = await this.servers.require("reload");
    if (!s) return false;
    return vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: `Kiln: reload ${mod.id}` }, async () => {
      try {
        let ok: boolean;
        let msg: string;
        if (await this.ctx.hasCli()) {
          const r = await this.ctx.exec(this.ctx.cliPath(), ["reload", mod.id, "--server", s.dir, "--json"], { timeoutMs: 60_000 });
          const j = JSON.parse(r.stdout.slice(r.stdout.indexOf("{")) || "{}");
          ok = !!j.ok;
          msg = j.message ?? j.error?.message ?? r.stderr.trim();
        } else {
          const r = await sendControl(s.dir, `reload ${mod.id}`, { timeoutMs: 45_000 });
          ok = r.ok;
          msg = r.message;
        }
        this.ctx.out.info(`reload ${mod.id} on ${s.name}: ${ok ? "ok" : "FAILED"}: ${msg}`);
        if (ok) vscode.window.setStatusBarMessage(`Kiln: ${msg}`, 6000);
        else vscode.window.showErrorMessage(`Kiln: reload ${mod.id}: ${msg}`);
        return ok;
      } catch (e) {
        vscode.window.showErrorMessage(`Kiln: reload ${mod.id}: ${(e as Error).message}`);
        return false;
      }
    });
  }

  private async control() {
    const s = await this.servers.require("send a control command to");
    if (!s) return;
    const mods = this.mods.map((m) => m.id);
    const items = [
      { label: "mods", detail: "every mod: id, version, state, error, faults, ms/step" },
      { label: "gc", detail: "full collection; unloaded mod contexts still alive" },
      ...mods.flatMap((id) => [
        { label: `reload ${id}`, detail: "dependents unload, the mod reloads from disk" },
        { label: `unload ${id}`, detail: "unload it and its dependents" },
        { label: `reload-test ${id} 20`, detail: "20 reloads, one per step, then a leak check" },
      ]),
      { label: "Other…", detail: "type a command (load <folder>, test <id> [filter], …)" },
    ];
    const pick = await vscode.window.showQuickPick(items, { title: `Control command for ${s.name}`, matchOnDetail: true });
    if (!pick) return;
    const command = pick.label === "Other…" ? await vscode.window.showInputBox({ title: "Control command" }) : pick.label;
    if (!command) return;
    try {
      const r = await sendControl(s.dir, command, { timeoutMs: /^reload-test|^test/.test(command) ? 240_000 : 30_000 });
      const doc = await vscode.workspace.openTextDocument({ language: "json", content: JSON.stringify(r, null, 2) });
      await vscode.window.showTextDocument(doc, { preview: true });
    } catch (e) {
      vscode.window.showErrorMessage(`Kiln: ${command}: ${(e as Error).message}`);
    }
  }
}
