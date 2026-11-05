// SPDX-License-Identifier: MIT
// Shared state of the extension: settings, the Kiln checkout, the output channel, the `kiln` CLI probe.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { run, RunOptions, RunResult, shellQuote } from "./core/proc";
import { expandHome } from "./core/servers";

export class Ctx implements vscode.Disposable {
  readonly out: vscode.LogOutputChannel;
  private cliProbe?: { path: string; ok: Promise<boolean> };
  private disposables: vscode.Disposable[] = [];

  constructor(readonly ext: vscode.ExtensionContext) {
    this.out = vscode.window.createOutputChannel("Kiln", { log: true });
    this.disposables.push(this.out);
    this.disposables.push(
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("kiln.cli.path")) this.cliProbe = undefined;
      }),
    );
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  get cfg(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration("kiln");
  }

  setting<T>(key: string, fallback: T): T {
    return this.cfg.get<T>(key, fallback);
  }

  asset(...p: string[]): string {
    return path.join(this.ext.extensionPath, ...p);
  }

  /** The Kiln checkout: kiln.root, else a workspace folder that is one (or contains one up its parents). */
  kilnRoot(): string | undefined {
    const is = (d: string) => fs.existsSync(path.join(d, "tools", "impact", "impact.py")) && fs.existsSync(path.join(d, "schema"));
    const set = this.setting("root", "");
    if (set) return is(expandHome(set)) ? expandHome(set) : undefined;
    for (const f of vscode.workspace.workspaceFolders ?? []) {
      let d = f.uri.fsPath;
      for (let i = 0; i < 6; i++) {
        if (is(d)) return d;
        const up = path.dirname(d);
        if (up === d) break;
        d = up;
      }
    }
    return undefined;
  }

  cliPath(): string {
    return expandHome(this.setting("cli.path", "kiln"));
  }

  /** Is the `kiln` CLI there (one `kiln --version` per setting value)? */
  hasCli(): Promise<boolean> {
    const p = this.cliPath();
    if (!this.cliProbe || this.cliProbe.path !== p) {
      this.cliProbe = {
        path: p,
        ok: run(p, ["--version"], { timeoutMs: 5000 }).then((r) => {
          const ok = !r.error && r.code === 0;
          this.out.info(ok ? `kiln CLI: ${r.stdout.trim() || p}` : `kiln CLI not available (${p}): using dotnet and the control folder`);
          return ok;
        }),
      };
    }
    return this.cliProbe.ok;
  }

  /** Runs a program, logging the command line and failures to the Kiln output. */
  async exec(cmd: string, args: string[], o: RunOptions = {}): Promise<RunResult> {
    this.out.debug(`$ ${[cmd, ...args].map(shellQuote).join(" ")}${o.cwd ? `   (in ${o.cwd})` : ""}`);
    const r = await run(cmd, args, o);
    if (r.error) this.out.warn(`${cmd}: ${r.error.message}`);
    else if (r.code !== 0) this.out.debug(`exit ${r.code}${r.stderr ? `: ${r.stderr.trim().slice(0, 2000)}` : ""}`);
    return r;
  }

  dotnet(): string {
    return expandHome(this.setting("dotnet.path", "dotnet"));
  }

  python(): string {
    return expandHome(this.setting("python.path", "python3"));
  }
}
