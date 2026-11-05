// SPDX-License-Identifier: MIT
// Debugging mods in a running server: the "kiln" attach configuration. The .NET runtime runs inside Wine, so the
// debugger (netcoredbg.exe, bundled in the .vsix) runs in the server's prefix and talks DAP. Adapter: `kiln debug --server <dir>
// --dap` when the CLI has it, else the same job done by dist/dapProxy.js (src/dapProxy.ts). Both keep the server alive
// across every way a session can end. Servers inside a Steam library are refused here, before anything starts.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { sendControl } from "../core/control";
import { staleContextsWarning } from "../core/dap";
import { DebugRefused, debugRefusal, findNetcoredbg, netcoredbgCandidates, proxySpec, resolveTarget } from "../core/debugTarget";
import { discoverLabs, expandHome, ServerDef, serverPids } from "../core/servers";
import { Ctx } from "../ctx";
import { Servers } from "./servers";

const DOC = ["docs", "kiln", "modding", "vscode.md"];

interface Prepared {
  def: ServerDef;
  descriptor: vscode.DebugAdapterDescriptor;
}

export class DebugFeature implements vscode.DebugConfigurationProvider, vscode.DebugAdapterDescriptorFactory, vscode.Disposable {
  private prepared = new Map<string, Prepared>();
  private active = new Map<string, vscode.DebugSession>();
  private disposables: vscode.Disposable[] = [];
  /** The Linux pids of a server (the integration tests answer this with a stand-in process). */
  hostPids: (def: ServerDef) => number[] = serverPids;

  constructor(
    private readonly ctx: Ctx,
    private readonly servers: Servers,
  ) {
    this.disposables.push(
      vscode.debug.registerDebugConfigurationProvider("kiln", this),
      vscode.debug.registerDebugAdapterDescriptorFactory("kiln", this),
      vscode.debug.onDidStartDebugSession((s) => {
        if (s.type === "kiln" && s.configuration.server) this.active.set(s.configuration.server, s);
      }),
      vscode.debug.onDidTerminateDebugSession((s) => {
        if (s.type === "kiln" && this.active.get(s.configuration.server) === s) this.active.delete(s.configuration.server);
      }),
      vscode.commands.registerCommand("kiln.server.debug", (s?: ServerDef) => this.attach(s)),
    );
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  provideDebugConfigurations(): vscode.DebugConfiguration[] {
    return [{ type: "kiln", request: "attach", name: "Kiln: attach to server" }];
  }

  /** Fills the defaults (F5 without a launch.json comes here with an empty configuration). */
  resolveDebugConfiguration(_folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration): vscode.DebugConfiguration {
    return { ...config, type: config.type || "kiln", request: config.request || "attach", name: config.name || "Kiln: attach to server" };
  }

  /** The checks, on the final value of `server`. Returning undefined aborts the session: nothing has started. */
  async resolveDebugConfigurationWithSubstitutedVariables(_folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration): Promise<vscode.DebugConfiguration | undefined> {
    try {
      const def = await this.server(config.server);
      if (!def) return undefined;
      if (this.active.has(def.dir)) throw new DebugRefused("refused", `a debugger is already attached to ${def.name}.`);
      this.prepared.set(def.dir, { def, descriptor: await this.descriptor(def) });
      return { ...config, server: def.dir };
    } catch (e) {
      if (!(e instanceof DebugRefused)) throw e;
      this.ctx.out.warn(`debug: ${e.message}`);
      void this.explain(e);
      return undefined;
    }
  }

  async createDebugAdapterDescriptor(session: vscode.DebugSession): Promise<vscode.DebugAdapterDescriptor | undefined> {
    const dir: string = session.configuration.server;
    const p = this.prepared.get(dir);
    this.prepared.delete(dir);
    if (!p) throw new Error(`Kiln: session for an unprepared server (${dir})`);
    return p.descriptor;
  }

  private bundledNetcoredbg(): string {
    return this.ctx.asset("dist", "netcoredbg", "netcoredbg.exe");
  }

  /** Test Servers view: attach to this server (or the selected one). */
  private attach(s?: ServerDef): Thenable<boolean> {
    const d = s ?? this.servers.selected();
    return vscode.debug.startDebugging(vscode.workspace.workspaceFolders?.[0], {
      type: "kiln",
      request: "attach",
      name: d ? `Kiln: attach to ${d.name}` : "Kiln: attach to server",
      ...(d ? { server: d.dir } : {}),
    });
  }

  /** `server` from launch.json: a name, a folder or a lab name; empty: the selected server. */
  private async server(v: unknown): Promise<ServerDef | undefined> {
    if (typeof v !== "string" || !v) return this.servers.require("attach the debugger to");
    const all = this.servers.list();
    const dir = path.resolve(expandHome(v));
    const def =
      all.find((s) => s.name === v) ??
      all.find((s) => path.resolve(s.dir) === dir) ??
      discoverLabs().find((s) => s.name === v || path.resolve(s.dir) === dir);
    if (def) return def;
    if (fs.existsSync(path.join(dir, "enshrouded_server.exe"))) return { name: path.basename(dir), dir, kind: "folder", source: "settings" };
    throw new DebugRefused("not_running", `no test server "${v}" (a name from the Test Servers view, a lab name, or a server folder).`);
  }

  private async descriptor(def: ServerDef): Promise<vscode.DebugAdapterDescriptor> {
    const why = debugRefusal(def);
    if (why) throw new DebugRefused("refused", `will not attach a debugger to ${def.name}: ${why}`);
    const dbg = findNetcoredbg(this.ctx.setting("debug.netcoredbg", ""), process.env, this.bundledNetcoredbg());
    const wine = this.ctx.setting("debug.wine", "");
    // `kiln debug` knows labs and server instances, and always uses the wine of the running server; a bare folder or an
    // explicit kiln.debug.wine is the built-in proxy's job
    if (def.kind !== "folder" && !wine && (await this.ctx.hasCli())) {
      const help = await this.ctx.exec(this.ctx.cliPath(), ["debug", "--help"], { timeoutMs: 5000 });
      if (!help.error && help.code === 0) {
        const env = { KILN_NETCOREDBG: dbg }; // the CLI alone has no bundled copy to fall back on
        await this.preflight(def, env);
        this.ctx.out.info(`debug: kiln debug --server ${def.dir} --dap`);
        return new vscode.DebugAdapterExecutable(this.ctx.cliPath(), ["debug", "--server", def.dir, "--dap"], { env });
      }
    }
    const target = await resolveTarget(def, { wine, hostPids: this.hostPids(def), log: (s) => this.ctx.out.warn(`debug: ${s}`) });
    await this.collect(def);
    this.ctx.out.info(`debug: ${dbg} in ${target.prefix ?? "(host)"} under ${target.wine ?? "(no wine)"}, server pid ${target.pid}`);
    // "node": VS Code forks its own Node for this, no node on PATH needed
    return new vscode.DebugAdapterExecutable("node", [this.ctx.asset("dist", "dapProxy.js")], {
      env: { KILN_DAP_SPEC: JSON.stringify(proxySpec(target, dbg)) },
    });
  }

  /**
   * A reload leaves the old mod's module loaded until the next collection, and netcoredbg can bind a breakpoint to that
   * dead copy (1 of 6 attaches stopped without this, 6 of 6 with it). `kiln debug --dap` does the same.
   */
  private async collect(def: ServerDef): Promise<void> {
    try {
      const r = await sendControl(def.dir, "gc", { timeoutMs: 30_000 });
      const warning = staleContextsWarning(r.message);
      if (warning) void vscode.window.showWarningMessage(`Kiln: ${warning}`);
    } catch (e) {
      this.ctx.out.warn(`debug: no collection before attaching (${(e as Error).message}); after a reload, breakpoints may not stop`);
    }
  }

  /**
   * `kiln debug --json` says whether the adapter could attach, and nothing has run in a prefix if it could not. Asking
   * first gives the same notification as the built-in path. Without this, `--dap` still answers every request with the
   * reason, but VS Code shows a failed initialize as a modal dialog.
   */
  private async preflight(def: ServerDef, env: Record<string, string>): Promise<void> {
    const r = await this.ctx.exec(this.ctx.cliPath(), ["debug", "--server", def.dir, "--json"], { timeoutMs: 40_000, env: { ...process.env, ...env } });
    let report: { ok?: boolean; error?: { code?: string; message?: string } } | undefined;
    try {
      report = JSON.parse(r.stdout);
    } catch {
      return; // not the expected answer: the adapter reports for itself
    }
    if (report?.ok === false) {
      const code = report.error?.code;
      const known = code === "refused" || code === "not_running" || code === "tool_missing" || code === "unsupported";
      throw new DebugRefused(known ? code : "refused", report.error?.message ?? `kiln debug refused ${def.name}.`);
    }
  }

  private async explain(e: DebugRefused): Promise<void> {
    const act = await vscode.window.showErrorMessage(`Kiln: ${e.message}`, ...(e.code === "tool_missing" ? ["Open docs"] : []));
    const root = this.ctx.kilnRoot();
    if (act && root) void vscode.window.showTextDocument(vscode.Uri.file(path.join(root, ...DOC)));
    else if (act) this.ctx.out.info(`netcoredbg goes to: ${netcoredbgCandidates(this.ctx.setting("debug.netcoredbg", ""), process.env, this.bundledNetcoredbg()).join(", ")}`);
  }
}
