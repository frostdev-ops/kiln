// SPDX-License-Identifier: MIT
// Following kiln.log: an output channel per server with the kiln-log grammar (PASS/FAIL/[E]/[W]), and document links
// for mod tags, stack-frame files, Wine paths and test names (in the output channels and in opened .log files).
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { findLinks } from "../core/logLine";
import { ServerDef } from "../core/servers";
import { Ctx } from "../ctx";
import { Mods } from "./mods";
import { Servers } from "./servers";

export type TestLocator = (mod: string | undefined, name: string) => vscode.Location | undefined;

class Follower implements vscode.Disposable {
  readonly channel: vscode.OutputChannel;
  private offset = 0;
  private timer: NodeJS.Timeout;
  private partial = Buffer.alloc(0);
  private ino?: number;

  constructor(
    readonly server: ServerDef,
    readonly file: string,
    initialLines: number,
  ) {
    this.channel = vscode.window.createOutputChannel(`Kiln log: ${server.name}`, "kiln-log");
    this.start(initialLines);
    this.timer = setInterval(() => this.poll(), 500);
  }

  private start(initialLines: number) {
    try {
      const st = fs.statSync(this.file);
      this.ino = st.ino;
      const from = Math.max(0, st.size - 256 * 1024);
      const buf = this.read(from, st.size);
      let lines = buf.split(/\r?\n/);
      if (from > 0) lines = lines.slice(1);
      if (lines.length && lines[lines.length - 1] === "") lines.pop();
      this.channel.append(lines.slice(-initialLines).join("\n") + (lines.length ? "\n" : ""));
      this.offset = st.size;
    } catch {
      this.channel.appendLine(`[kiln-vscode] ${this.file} does not exist yet; waiting for it`);
      this.offset = 0;
    }
  }

  private readBytes(a: number, b: number): Buffer {
    const fd = fs.openSync(this.file, "r");
    try {
      const buf = Buffer.alloc(b - a);
      const n = fs.readSync(fd, buf, 0, b - a, a);
      return buf.subarray(0, n);
    } finally {
      fs.closeSync(fd);
    }
  }

  private read(a: number, b: number): string {
    return this.readBytes(a, b).toString("utf8");
  }

  poll() {
    let st: fs.Stats;
    try {
      st = fs.statSync(this.file);
    } catch {
      return;
    }
    if (st.size < this.offset || (this.ino !== undefined && st.ino !== this.ino)) {
      this.channel.appendLine(`[kiln-vscode] ${path.basename(this.file)} started over (a server restart)`);
      this.offset = 0;
      this.partial = Buffer.alloc(0);
      this.ino = st.ino;
    }
    if (st.size === this.offset) return;
    const end = Math.min(st.size, this.offset + 4 * 1024 * 1024);
    const bytes = Buffer.concat([this.partial, this.readBytes(this.offset, end)]);
    this.offset = end;
    const nl = bytes.lastIndexOf(0x0a);
    if (nl < 0) {
      this.partial = bytes;
      return;
    }
    this.channel.append(bytes.subarray(0, nl + 1).toString("utf8").replace(/\r/g, ""));
    this.partial = Buffer.from(bytes.subarray(nl + 1));
  }

  dispose() {
    clearInterval(this.timer);
    this.channel.dispose();
  }
}

export class LogFeature implements vscode.DocumentLinkProvider, vscode.Disposable {
  private followers = new Map<string, Follower>();
  private disposables: vscode.Disposable[] = [];
  testLocator?: TestLocator;

  constructor(
    private readonly ctx: Ctx,
    private readonly servers: Servers,
    private readonly mods: Mods,
  ) {
    this.disposables.push(
      vscode.languages.registerDocumentLinkProvider({ language: "kiln-log" }, this),
      vscode.commands.registerCommand("kiln.followLog", (s?: ServerDef) => this.follow(s)),
    );
  }

  dispose(): void {
    for (const f of this.followers.values()) f.dispose();
    for (const d of this.disposables) d.dispose();
  }

  async follow(s?: ServerDef): Promise<vscode.OutputChannel | undefined> {
    const def = s ?? (await this.servers.require("follow kiln.log of", false));
    if (!def) return undefined;
    let f = this.followers.get(def.name);
    if (!f) {
      f = new Follower(def, path.join(def.dir, "kiln", "logs", "kiln.log"), this.ctx.setting("log.initialLines", 200));
      this.followers.set(def.name, f);
    }
    f.channel.show(true);
    return f.channel;
  }

  /** The server a log document belongs to (an output channel's name, or a file under <server>/kiln/logs). */
  private serverOf(doc: vscode.TextDocument): ServerDef | undefined {
    if (doc.uri.scheme === "file") {
      const m = /^(.*)[\\/]kiln[\\/]logs[\\/][^\\/]+$/.exec(doc.uri.fsPath);
      if (m) return this.servers.list().find((s) => path.resolve(s.dir) === path.resolve(m[1])) ?? { name: "", dir: m[1], kind: "folder", source: "settings" };
    }
    for (const f of this.followers.values()) if (doc.uri.path.includes(encodeURIComponent(f.channel.name)) || doc.uri.path.includes(f.channel.name)) return f.server;
    return this.servers.selected();
  }

  provideDocumentLinks(doc: vscode.TextDocument): vscode.DocumentLink[] {
    const out: vscode.DocumentLink[] = [];
    const server = this.serverOf(doc);
    const max = Math.min(doc.lineCount, 50_000);
    for (let i = Math.max(0, doc.lineCount - max); i < doc.lineCount; i++) {
      const line = doc.lineAt(i).text;
      for (const l of findLinks(line)) {
        const range = new vscode.Range(i, l.start, i, l.end);
        const t = l.target;
        let target: vscode.Uri | undefined;
        let tip = "";
        if (t.kind === "file") {
          if (!fs.existsSync(t.path)) continue;
          target = vscode.Uri.file(t.path);
          if (t.line) target = target.with({ fragment: `L${t.line}${t.col ? `,${t.col}` : ""}` });
          tip = "Open file";
        } else if (t.kind === "mod") {
          const ws = this.mods.all().find((m) => m.id === t.id);
          const file = ws?.manifest ?? ws?.project ?? (server ? path.join(server.dir, "kiln", "mods", t.id, "kiln.mod.json") : undefined);
          if (!file || !fs.existsSync(file)) continue;
          target = vscode.Uri.file(file);
          tip = ws ? `Open mod ${t.id} (workspace)` : `Open ${t.id}'s deployed manifest`;
        } else if (t.kind === "test") {
          const loc = this.testLocator?.(t.mod, t.name);
          if (!loc) continue;
          target = loc.uri.with({ fragment: `L${loc.range.start.line + 1},${loc.range.start.character + 1}` });
          tip = "Go to test";
        }
        if (!target) continue;
        const dl = new vscode.DocumentLink(range, target);
        dl.tooltip = tip;
        out.push(dl);
      }
    }
    return out;
  }
}
