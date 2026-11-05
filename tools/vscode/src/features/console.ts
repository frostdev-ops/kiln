// SPDX-License-Identifier: MIT
// Server console over KAP/1: a terminal that runs console commands (`command_run`), raw ops (`.op name {json}`),
// Tab completion (`command_complete`) and streams the `log` topic, colored like the kiln-log grammar.
import * as path from "node:path";
import * as vscode from "vscode";
import { connectServer, KapClient, Res } from "../core/kap";
import { C, colorLogLine, parseOpArgs } from "../core/consoleFormat";
import { ServerDef } from "../core/servers";
import { Ctx } from "../ctx";
import { Servers } from "./servers";

export class ConsoleTerminal implements vscode.Pseudoterminal {
  private readonly write = new vscode.EventEmitter<string>();
  readonly onDidWrite = this.write.event;
  private readonly closeEm = new vscode.EventEmitter<number | void>();
  readonly onDidClose = this.closeEm.event;
  private client?: KapClient;
  private line = "";
  private cursor = 0;
  private history: string[] = [];
  private hpos = -1;
  private busy = false;
  private logOn = false;
  private readonly prompt: string;

  constructor(
    private readonly ctx: Ctx,
    readonly server: ServerDef,
  ) {
    this.prompt = `${C.ember}${server.name}>${C.reset} `;
  }

  open(): void {
    void this.connect();
  }

  close(): void {
    this.client?.close();
  }

  private out(s: string) {
    this.write.fire(s.replace(/\r?\n/g, "\r\n"));
  }

  /** Prints above the input line and redraws it. */
  private print(s: string) {
    this.write.fire("\r\x1b[2K");
    this.out(s.endsWith("\n") ? s : s + "\n");
    if (!this.busy) this.redraw();
  }

  private redraw() {
    this.write.fire(`\r\x1b[2K${this.prompt}${this.line}`);
    const back = this.line.length - this.cursor;
    if (back > 0) this.write.fire(`\x1b[${back}D`);
  }

  private async connect() {
    this.out(`${C.dim}Kiln console: connecting to ${this.server.name} (${path.join(this.server.dir, "kiln", "data", "kiln-admin")})…${C.reset}\n`);
    try {
      const c = await connectServer(this.server.dir, { client: `kiln-vscode/${this.ctx.ext.extension.packageJSON.version}` });
      this.client = c;
      const h = c.hello;
      this.out(
        `${C.bold}${h.server?.name ?? this.server.name}${C.reset} ${C.dim}(server ${h.server?.id ?? "?"}, kiln ${h.kiln ?? "?"}, admin ${h.admin ?? "?"}, ${c.ready.ops.length} ops)${C.reset}\n` +
          `${C.dim}Type a console command (help), .op <name> [k=v | {json}], .ops, .log on|off, .clear, .quit. Tab completes.${C.reset}\n`,
      );
      c.on("push", (topic: string, data: { lines?: string[] }) => {
        if (topic === "log" && this.logOn && Array.isArray(data?.lines)) for (const l of data.lines) this.print(colorLogLine(l));
      });
      c.on("bye", (reason: string) => this.print(`${C.yellow}server said bye (${reason})${C.reset}`));
      c.on("close", (reason: string) => {
        this.print(`${C.dim}connection closed (${reason}). Press Enter to reconnect, Ctrl-D to close.${C.reset}`);
      });
      if (c.ready.topics.includes("log")) await this.setLog(true);
      this.redraw();
    } catch (e) {
      this.out(`${C.red}${(e as Error).message}${C.reset}\n${C.dim}Press Enter to retry, Ctrl-D to close.${C.reset}\n`);
    }
  }

  private async setLog(on: boolean) {
    const c = this.client;
    if (!c?.isOpen) return;
    const r = on ? await c.subscribe("log") : await c.unsubscribe("log");
    this.logOn = on && r.ok;
    if (!r.ok) this.print(`${C.red}log topic: ${r.error ?? r.code}${C.reset}`);
  }

  handleInput(data: string): void {
    if (this.busy) {
      if (data === "\x03") this.print(`${C.dim}(still waiting for the answer)${C.reset}`);
      return;
    }
    for (let i = 0; i < data.length; i++) {
      const ch = data[i];
      if (ch === "\x1b") {
        const seq = data.slice(i, i + 3);
        const seq4 = data.slice(i, i + 4);
        if (seq === "\x1b[A") this.historyMove(1);
        else if (seq === "\x1b[B") this.historyMove(-1);
        else if (seq === "\x1b[C") this.cursor = Math.min(this.line.length, this.cursor + 1);
        else if (seq === "\x1b[D") this.cursor = Math.max(0, this.cursor - 1);
        else if (seq === "\x1b[H" || seq4 === "\x1b[1~") this.cursor = 0;
        else if (seq === "\x1b[F" || seq4 === "\x1b[4~") this.cursor = this.line.length;
        else if (seq4 === "\x1b[3~") this.line = this.line.slice(0, this.cursor) + this.line.slice(this.cursor + 1);
        i += seq4.endsWith("~") ? 3 : 2;
        this.redraw();
        continue;
      }
      if (ch === "\r" || ch === "\n") {
        const l = this.line;
        this.line = "";
        this.cursor = 0;
        this.write.fire("\r\n");
        void this.submit(l);
        return;
      }
      if (ch === "\x7f" || ch === "\b") {
        if (this.cursor > 0) {
          this.line = this.line.slice(0, this.cursor - 1) + this.line.slice(this.cursor);
          this.cursor--;
        }
      } else if (ch === "\x03") {
        this.line = "";
        this.cursor = 0;
        this.write.fire("^C\r\n");
      } else if (ch === "\x04") {
        if (!this.line) {
          this.client?.close();
          this.closeEm.fire();
          return;
        }
      } else if (ch === "\x0c") {
        this.write.fire("\x1b[2J\x1b[H");
      } else if (ch === "\x01") this.cursor = 0;
      else if (ch === "\x05") this.cursor = this.line.length;
      else if (ch === "\x15") {
        this.line = this.line.slice(this.cursor);
        this.cursor = 0;
      } else if (ch === "\t") {
        void this.complete();
        return;
      } else if (ch >= " ") {
        this.line = this.line.slice(0, this.cursor) + ch + this.line.slice(this.cursor);
        this.cursor++;
      }
    }
    this.redraw();
  }

  private historyMove(d: number) {
    if (!this.history.length) return;
    this.hpos = Math.max(-1, Math.min(this.history.length - 1, this.hpos + d));
    this.line = this.hpos < 0 ? "" : this.history[this.history.length - 1 - this.hpos];
    this.cursor = this.line.length;
  }

  private async complete() {
    const c = this.client;
    const before = this.line.slice(0, this.cursor);
    let start = 0;
    let suggestions: string[] = [];
    if (before.startsWith(".op ") && !before.slice(4).includes(" ")) {
      start = 4;
      suggestions = (c?.ready.ops ?? []).filter((o) => o.startsWith(before.slice(4))).sort();
    } else if (before.startsWith(".")) {
      suggestions = [".op ", ".ops", ".log on", ".log off", ".clear", ".quit", ".help"].filter((x) => x.startsWith(before));
    } else if (c?.isOpen && c.hasOp("command_complete")) {
      try {
        const r = await c.request("command_complete", { text: before }, 5000);
        const d = r.data as { start?: number; suggestions?: (string | { text?: string; name?: string })[] } | undefined;
        if (r.ok && d) {
          start = d.start ?? 0;
          suggestions = (d.suggestions ?? []).map((s) => (typeof s === "string" ? s : s.text ?? s.name ?? "")).filter(Boolean);
        }
      } catch {
        /* no completion */
      }
    }
    if (suggestions.length === 1 && !suggestions[0].startsWith("<")) {
      const s = suggestions[0];
      this.line = this.line.slice(0, start) + s + (s.endsWith(" ") ? "" : " ") + this.line.slice(this.cursor);
      this.cursor = start + s.length + (s.endsWith(" ") ? 0 : 1);
      this.redraw();
    } else if (suggestions.length > 1) {
      // common prefix, then the list
      let pre = suggestions[0];
      for (const s of suggestions) while (!s.startsWith(pre)) pre = pre.slice(0, -1);
      const typed = before.slice(start);
      if (pre.length > typed.length) {
        this.line = this.line.slice(0, start) + pre + this.line.slice(this.cursor);
        this.cursor = start + pre.length;
      }
      this.print(`${C.dim}${suggestions.join("  ")}${C.reset}`);
    } else this.write.fire("\x07");
  }

  private async submit(raw: string) {
    const line = raw.trim();
    if (!this.client?.isOpen) {
      if (line === ".quit") {
        this.closeEm.fire();
        return;
      }
      await this.connect();
      return;
    }
    if (!line) {
      this.redraw();
      return;
    }
    if (this.history[this.history.length - 1] !== line) this.history.push(line);
    this.hpos = -1;
    const c = this.client;
    this.busy = true;
    try {
      if (line === ".quit" || line === ".exit") {
        c.close();
        this.closeEm.fire();
        return;
      } else if (line === ".help") {
        this.out(
          `${C.bold}console commands${C.reset}: anything else is sent as a console line (op command_run)\n` +
            `.op <name> [k=v ...|{json}]   any Kiln.Admin op (ops: .ops [filter])\n.log on|off   stream kiln.log\n.clear   clear the screen\n.quit   close\n`,
        );
      } else if (line.startsWith(".ops")) {
        const f = line.slice(4).trim();
        this.out(c.ready.ops.filter((o) => o.includes(f)).sort().join("  ") + "\n");
      } else if (line === ".clear") {
        this.write.fire("\x1b[2J\x1b[H");
      } else if (line.startsWith(".log")) {
        await this.setLog(!/off/.test(line));
        this.out(`${C.dim}log ${this.logOn ? "on" : "off"}${C.reset}\n`);
      } else if (line.startsWith(".op ")) {
        const m = /^\.op\s+(\S+)\s*(.*)$/.exec(line)!;
        let args: Record<string, unknown>;
        try {
          args = parseOpArgs(m[2]);
        } catch (e) {
          this.out(`${C.red}arguments: ${(e as Error).message}${C.reset}\n`);
          return;
        }
        this.showRes(await c.request(m[1], args, 200_000));
      } else {
        const op = c.hasOp("command_run") ? "command_run" : "command";
        const r = await c.request(op, { line }, 60_000);
        this.showCommand(r);
      }
    } catch (e) {
      this.out(`${C.red}${(e as Error).message}${C.reset}\n`);
    } finally {
      this.busy = false;
      this.redraw();
    }
  }

  private showCommand(r: Res) {
    if (!r.ok) {
      this.out(`${C.red}${r.error ?? r.code}${C.reset}\n`);
      return;
    }
    const d = r.data as { ok?: boolean; found?: boolean; error?: string | null; parseError?: string | null; denied?: boolean; output?: string[] } | undefined;
    for (const l of d?.output ?? []) this.out(l + "\n");
    if (d && (d.ok === false || d.found === false)) {
      this.out(`${C.red}${d.error ?? d.parseError ?? (d.denied ? "denied" : "failed")}${C.reset}\n`);
    }
  }

  private showRes(r: Res) {
    const head = `${r.ok ? C.green + "ok" : C.red + (r.code ?? "failed")}${C.reset} ${C.dim}${r.op} ${r.ms ?? "?"} ms${C.reset}`;
    this.out(head + "\n");
    if (!r.ok) this.out(`${C.red}${r.error ?? ""}${C.reset}\n`);
    else if (r.data !== undefined) this.out(JSON.stringify(r.data, null, 2) + "\n");
  }
}

export function registerConsole(ctx: Ctx, servers: Servers): vscode.Disposable {
  return vscode.commands.registerCommand("kiln.openConsole", async (s?: ServerDef) => {
    const def = s ?? (await servers.require("open the console of", false));
    if (!def) return undefined;
    const pty = new ConsoleTerminal(ctx, def);
    const term = vscode.window.createTerminal({ name: `Kiln console: ${def.name}`, pty, iconPath: new vscode.ThemeIcon("flame") });
    term.show();
    return term;
  });
}
