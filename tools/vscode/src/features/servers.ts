// SPDX-License-Identifier: MIT
// Test servers: list (settings + kilnctl instances + labs), selection, start/stop, state, status bar.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import {
  discoverKilnctl,
  discoverLabs,
  expandHome,
  protectedReason,
  ServerDef,
  ServerState,
  serverState,
} from "../core/servers";
import { Ctx } from "../ctx";

const SELECTED_KEY = "kiln.selectedServer";

export class Servers implements vscode.TreeDataProvider<ServerDef>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<ServerDef | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly selectionChanged = new vscode.EventEmitter<ServerDef | undefined>();
  readonly onDidChangeSelection = this.selectionChanged.event;
  private states = new Map<string, ServerState>();
  private status: vscode.StatusBarItem;
  private timer?: NodeJS.Timeout;
  private disposables: vscode.Disposable[] = [];

  constructor(private readonly ctx: Ctx) {
    this.status = vscode.window.createStatusBarItem("kiln.server", vscode.StatusBarAlignment.Left, 40);
    this.status.name = "Kiln test server";
    this.status.command = "kiln.selectServer";
    this.disposables.push(
      this.status,
      this.changed,
      this.selectionChanged,
      vscode.window.registerTreeDataProvider("kiln.servers", this),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("kiln.servers") || e.affectsConfiguration("kiln.server")) this.refresh();
      }),
      vscode.commands.registerCommand("kiln.selectServer", (s?: ServerDef) => this.pickAndSelect(s)),
      vscode.commands.registerCommand("kiln.addServer", () => this.add()),
      vscode.commands.registerCommand("kiln.server.refresh", () => this.refresh()),
      vscode.commands.registerCommand("kiln.server.start", (s?: ServerDef) => this.startStop("start", s)),
      vscode.commands.registerCommand("kiln.server.stop", (s?: ServerDef) => this.startStop("stop", s)),
      vscode.commands.registerCommand("kiln.server.openFolder", (s?: ServerDef) => {
        const d = s ?? this.selected();
        if (d) vscode.commands.executeCommand("revealFileInOS", vscode.Uri.file(path.join(d.dir, "kiln")));
      }),
    );
    this.refresh();
    this.timer = setInterval(() => this.refreshStates(), 10_000);
  }

  dispose(): void {
    if (this.timer) clearInterval(this.timer);
    for (const d of this.disposables) d.dispose();
  }

  list(): ServerDef[] {
    const out: ServerDef[] = [];
    const seen = new Set<string>();
    const push = (s: ServerDef) => {
      const key = path.resolve(s.dir);
      if (seen.has(key) || out.some((o) => o.name === s.name)) return;
      seen.add(key);
      out.push(s);
    };
    for (const raw of this.ctx.setting<{ name: string; dir: string; kind?: string; instance?: string }[]>("servers", [])) {
      if (!raw?.name || !raw?.dir) continue;
      const kind = raw.kind === "kilnctl" || raw.kind === "lab" ? raw.kind : "folder";
      const dir = expandHome(raw.dir);
      const inst = raw.instance ?? raw.name;
      push({
        name: raw.name,
        dir,
        kind,
        instance: inst,
        prefix: kind === "lab" ? path.join(path.dirname(dir), `${inst}.pfx`) : kind === "kilnctl" ? `${dir}.pfx` : undefined,
        source: "settings",
      });
    }
    if (this.ctx.setting("server.discover", true)) discoverKilnctl().forEach(push);
    if (this.ctx.setting("server.discoverLabs", false) && this.ctx.kilnRoot()) discoverLabs().forEach(push);
    return out;
  }

  selected(): ServerDef | undefined {
    const all = this.list();
    const name = this.ctx.ext.workspaceState.get<string>(SELECTED_KEY) || this.ctx.setting("server.default", "");
    return all.find((s) => s.name === name) ?? (all.length === 1 ? all[0] : undefined);
  }

  state(s: ServerDef): ServerState | undefined {
    return this.states.get(s.name);
  }

  async select(s: ServerDef | undefined): Promise<void> {
    await this.ctx.ext.workspaceState.update(SELECTED_KEY, s?.name);
    this.selectionChanged.fire(s);
    this.changed.fire(undefined);
    this.updateStatus();
  }

  /** The selected server, or ask; `writable` refuses servers inside a Steam library. */
  async require(purpose: string, writable = true): Promise<ServerDef | undefined> {
    let s = this.selected();
    if (!s) {
      s = await this.pick(`Test server to ${purpose}`);
      if (!s) return undefined;
      await this.select(s);
    }
    if (writable) {
      const why = protectedReason(s.dir);
      if (why) {
        vscode.window.showErrorMessage(`Kiln: will not ${purpose} on ${s.name}: ${why}.`);
        return undefined;
      }
    }
    if (!fs.existsSync(path.join(s.dir, "kiln"))) {
      vscode.window.showWarningMessage(`Kiln: ${s.dir} has no kiln/ folder (deploy Kiln there first: tools/deploy-server.sh or kilnctl deploy).`);
    }
    return s;
  }

  refresh(): void {
    this.changed.fire(undefined);
    void this.refreshStates();
  }

  private async refreshStates() {
    const all = this.list();
    const next = new Map<string, ServerState>();
    await Promise.all(all.map(async (s) => next.set(s.name, await serverState(s))));
    const before = JSON.stringify([...this.states]);
    this.states = next;
    if (JSON.stringify([...next]) !== before) this.changed.fire(undefined);
    this.updateStatus();
  }

  private updateStatus() {
    const s = this.selected();
    if (!s) {
      this.status.text = "$(flame) Kiln: no server";
      this.status.tooltip = "Select a Kiln test server";
    } else {
      const st = this.states.get(s.name);
      this.status.text = `$(flame) ${s.name}${st ? (st.running ? " $(circle-filled)" : " $(circle-outline)") : ""}`;
      this.status.tooltip = `${s.dir}\n${st?.running ? "running" : "stopped"}${st?.kap?.live ? ` · KAP ${st.kap.host}:${st.kap.port}` : ""}`;
    }
    this.status.show();
  }

  getTreeItem(s: ServerDef): vscode.TreeItem {
    const st = this.states.get(s.name);
    const sel = this.selected()?.name === s.name;
    const item = new vscode.TreeItem(s.name, vscode.TreeItemCollapsibleState.None);
    const ro = protectedReason(s.dir);
    const bits = [ro ? "read-only" : st?.running ? "running" : "stopped", s.kind !== "folder" ? s.kind : "", st && !st.kiln ? "no kiln/" : ""];
    if (st?.kap?.live) bits.push(`KAP :${st.kap.port}`);
    item.description = bits.filter(Boolean).join(" · ") + (sel ? "  (selected)" : "");
    item.tooltip = new vscode.MarkdownString(
      `**${s.name}** (${s.kind}, from ${s.source})\n\n\`${s.dir}\`${st?.pids.length ? `\n\npid ${st.pids.join(", ")}` : ""}${ro ? `\n\nRead-only: ${ro}.` : ""}`,
    );
    item.iconPath = new vscode.ThemeIcon(
      st?.running ? "vm-running" : "vm-outline",
      st?.running ? new vscode.ThemeColor("testing.iconPassed") : undefined,
    );
    item.contextValue = ro ? "kiln.server.readonly" : st?.running ? "kiln.server.running" : "kiln.server.stopped";
    item.command = { command: "kiln.selectServer", title: "Select", arguments: [s] };
    return item;
  }

  getChildren(el?: ServerDef): ServerDef[] {
    return el ? [] : this.list();
  }

  private async pick(title: string): Promise<ServerDef | undefined> {
    const all = this.list();
    if (!all.length) {
      const a = await vscode.window.showInformationMessage("Kiln: no test server configured.", "Add Test Server…");
      if (a) await this.add();
      return this.list().length ? this.pick(title) : undefined;
    }
    const items = all.map((s) => ({
      label: s.name,
      description: `${s.kind}${this.states.get(s.name)?.running ? " · running" : ""}`,
      detail: s.dir,
      s,
    }));
    return (await vscode.window.showQuickPick(items, { title, matchOnDetail: true }))?.s;
  }

  private async pickAndSelect(s?: ServerDef) {
    const chosen = s ?? (await this.pick("Select the Kiln test server"));
    if (chosen) await this.select(chosen);
  }

  private async add() {
    const items: (vscode.QuickPickItem & { def?: ServerDef; folder?: boolean })[] = [
      { label: "$(folder) Server folder…", detail: "A folder with enshrouded_server.exe and Kiln deployed (no start/stop)", folder: true },
    ];
    const have = new Set(this.list().map((s) => path.resolve(s.dir)));
    for (const d of discoverKilnctl()) if (!have.has(path.resolve(d.dir))) items.push({ label: `$(server) ${d.name}`, description: "kilnctl", detail: d.dir, def: d });
    if (this.ctx.kilnRoot()) for (const d of discoverLabs()) if (!have.has(path.resolve(d.dir))) items.push({ label: `$(beaker) ${d.name}`, description: "lab", detail: d.dir, def: d });
    const pick = await vscode.window.showQuickPick(items, { title: "Add a Kiln test server", matchOnDetail: true });
    if (!pick) return;
    let def = pick.def;
    if (pick.folder) {
      const uri = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: "Use as test server" });
      if (!uri?.[0]) return;
      const dir = uri[0].fsPath;
      if (!fs.existsSync(path.join(dir, "enshrouded_server.exe")) && !fs.existsSync(path.join(dir, "kiln"))) {
        const go = await vscode.window.showWarningMessage(`${dir} has neither enshrouded_server.exe nor kiln/. Add it anyway?`, "Add");
        if (!go) return;
      }
      const name = await vscode.window.showInputBox({ title: "Name of the test server", value: path.basename(dir) });
      if (!name) return;
      def = { name, dir, kind: "folder", source: "settings" };
    }
    if (!def) return;
    const why = protectedReason(def.dir);
    if (why) vscode.window.showWarningMessage(`Kiln: ${def.name} is read-only for the extension: ${why}.`);
    const target = vscode.workspace.workspaceFolders?.length ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global;
    const cur = this.ctx.setting<unknown[]>("servers", []);
    await this.ctx.cfg.update(
      "servers",
      [...cur, { name: def.name, dir: def.dir, kind: def.kind, ...(def.instance && def.instance !== def.name ? { instance: def.instance } : {}) }],
      target,
    );
    await this.select(def);
  }

  private async startStop(what: "start" | "stop", s?: ServerDef) {
    const def = s ?? (await this.require(what));
    if (!def) return;
    const why = protectedReason(def.dir);
    if (why) {
      vscode.window.showErrorMessage(`Kiln: will not ${what} ${def.name}: ${why}.`);
      return;
    }
    let cmd: string;
    let args: string[];
    if (def.kind === "kilnctl") {
      cmd = expandHome(this.ctx.setting("kilnctl.path", "kilnctl"));
      args = [what, def.instance ?? def.name];
    } else if (def.kind === "lab") {
      const root = this.ctx.kilnRoot();
      if (!root) {
        vscode.window.showErrorMessage("Kiln: lab servers need the Kiln checkout (kiln.root) for tools/lab/lab.sh.");
        return;
      }
      cmd = path.join(root, "tools", "lab", "lab.sh");
      args = [what, def.instance ?? def.name];
    } else {
      vscode.window.showInformationMessage(
        `Kiln: ${def.name} is a plain folder; start and stop it yourself (or add it as a kilnctl instance / lab).`,
      );
      return;
    }
    await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: `Kiln: ${what} ${def.name}`, cancellable: false },
      async () => {
        const r = await this.ctx.exec(cmd, args, { timeoutMs: what === "start" ? 360_000 : 180_000, onStdout: (c) => this.ctx.out.info(c.trimEnd()) });
        if (r.error || r.code !== 0) {
          vscode.window.showErrorMessage(`Kiln: ${what} ${def.name} failed: ${(r.error?.message ?? r.stderr ?? r.stdout).trim().slice(-400)}`);
          this.ctx.out.show(true);
        } else vscode.window.setStatusBarMessage(`Kiln: ${def.name} ${what === "start" ? "started" : "stopped"}`, 5000);
      },
    );
    await this.refreshStates();
  }
}
