// SPDX-License-Identifier: MIT
// VFX in blocks/*.json: completion and hover for what a `vfx` field names (the game's from the catalog, the mod's own from its files) and for a game VFX's parameters,
// and the VFX budget on a block's name. Fields and their documentation come from the schema, through VS Code's JSON support.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { getLocation } from "jsonc-parser";
import { budgetLines, describeGameVfx, findGameVfx, overBudget, parameterSnippet, readVfxNeed, vfxGameName, vfxNameAt, vfxParameters, vfxPlace, VFX_LIMITS } from "../core/vfx";
import { stringAt } from "../core/catalog";
import { Ctx } from "../ctx";
import { CatalogFeature } from "./catalog";

const BLOCKS_FILE = /[\\/]blocks[\\/][^\\/]+\.json$/i;
const CODE_NAME = /(?:VfxId|VfxRef)\.Game\(\s*$/;

export class VfxFeature implements vscode.Disposable {
  private readonly disposables: vscode.Disposable[] = [];

  constructor(
    private readonly ctx: Ctx,
    private readonly catalog: CatalogFeature,
  ) {
    const sel = [{ language: "json" }, { language: "jsonc" }];
    this.disposables.push(
      vscode.languages.registerCompletionItemProvider(sel, { provideCompletionItems: (d, p) => this.complete(d, p) }, '"', ":"),
      vscode.languages.registerHoverProvider(sel, { provideHover: (d, p) => this.hover(d, p) }),
      vscode.languages.registerHoverProvider({ language: "csharp" }, { provideHover: (d, p) => this.hoverCs(d, p) }),
    );
  }

  dispose(): void {
    for (const d of this.disposables) d.dispose();
  }

  private isBlocks(doc: vscode.TextDocument): boolean {
    return (doc.uri.scheme === "file" || doc.uri.scheme === "untitled") && BLOCKS_FILE.test(doc.uri.fsPath);
  }

  /** The open text of every blocks file of this document's mod. */
  private modTexts(doc: vscode.TextDocument): string[] {
    const dir = path.dirname(doc.uri.fsPath);
    const out = [doc.getText()];
    try {
      for (const f of fs.readdirSync(dir)) {
        const p = path.join(dir, f);
        if (!f.toLowerCase().endsWith(".json") || p === doc.uri.fsPath) continue;
        const open = vscode.workspace.textDocuments.find((d) => d.uri.fsPath === p);
        out.push(open ? open.getText() : fs.readFileSync(p, "utf8"));
      }
    } catch (e) {
      this.ctx.out.warn(`vfx: ${dir}: ${(e as Error).message}`);
    }
    return out;
  }

  private stringRange(doc: vscode.TextDocument, pos: vscode.Position): vscode.Range | undefined {
    const s = stringAt(doc.lineAt(pos.line).text, pos.character);
    return s ? new vscode.Range(pos.line, s.start, pos.line, s.end) : undefined;
  }

  complete(doc: vscode.TextDocument, pos: vscode.Position): vscode.CompletionItem[] | undefined {
    if (!this.isBlocks(doc)) return undefined;
    const text = doc.getText();
    const loc = getLocation(text, doc.offsetAt(pos));
    const place = vfxPlace(loc.path, loc.isAtPropertyKey);
    if (!place) return undefined;
    const range = this.stringRange(doc, pos);
    const c = this.catalog.catalog;
    if (place.kind === "parameter") {
      const name = vfxNameAt(text, place.holder);
      const game = name && vfxGameName(name);
      const entry = c && game ? findGameVfx(c, game) : undefined;
      if (!entry) return undefined;
      const items: vscode.CompletionItem[] = [];
      for (const p of vfxParameters(entry)) {
        const value = p.name ? parameterSnippet(p) : undefined;
        if (!p.name || !value) continue;
        const it = new vscode.CompletionItem({ label: p.name, description: p.type }, vscode.CompletionItemKind.Property);
        it.insertText = new vscode.SnippetString(`${range ? "" : '"'}${p.name}${range ? "" : '"'}`);
        if (range) it.range = range;
        it.detail = `${p.type}${p.default !== undefined ? `, ${JSON.stringify(p.default)} by default` : ""}`;
        items.push(it);
      }
      return items;
    }
    if (place.kind !== "reference") return undefined;
    const items: vscode.CompletionItem[] = [];
    const quote = (v: string) => (range ? v : `"${v}"`);
    for (const name of readVfxNeed(this.modTexts(doc)).blocks.keys()) {
      const it = new vscode.CompletionItem({ label: name, description: "this mod" }, vscode.CompletionItemKind.Reference);
      it.insertText = quote(name);
      if (range) it.range = range;
      it.sortText = `0${name}`;
      items.push(it);
    }
    for (const e of c?.entries("vfx") ?? []) {
      // a name nothing finds at run time goes in as its GUID, under that name
      const value = e.byName ? `game:${e.name}` : `game:${e.guid}`;
      const it = new vscode.CompletionItem({ label: `game:${e.name}`, description: e.byName ? "game" : "game, by GUID" }, vscode.CompletionItemKind.Constant);
      it.insertText = quote(value);
      if (range) it.range = range;
      it.filterText = `game:${e.name} ${(Array.isArray(e.names) ? (e.names as string[]) : []).join(" ")}`;
      it.sortText = `1${e.name}`;
      const md = new vscode.MarkdownString(describeGameVfx(e).join("\n\n"));
      it.documentation = md;
      items.push(it);
    }
    return items;
  }

  hover(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | undefined {
    if (!this.isBlocks(doc)) return undefined;
    const text = doc.getText();
    const loc = getLocation(text, doc.offsetAt(pos));
    const place = vfxPlace(loc.path, loc.isAtPropertyKey);
    const range = this.stringRange(doc, pos);
    if (!place || !range) return undefined;
    const value = doc.getText(range);
    const c = this.catalog.catalog;
    if (place.kind === "block") return new vscode.Hover(this.blockMarkdown(doc, place.name), range);
    if (place.kind === "parameter") {
      const name = vfxNameAt(text, place.holder);
      const game = name && vfxGameName(name);
      const entry = c && game ? findGameVfx(c, game) : undefined;
      const p = entry && vfxParameters(entry).find((x) => x.name === value);
      if (!entry || !p) return undefined;
      return new vscode.Hover(new vscode.MarkdownString(`\`${value}\` ${p.type}${p.default !== undefined ? `, ${JSON.stringify(p.default)} by default` : ""}, on \`${entry.name}\``), range);
    }
    const game = vfxGameName(value);
    if (game !== undefined) {
      if (!c) return undefined;
      const e = findGameVfx(c, game);
      return new vscode.Hover(new vscode.MarkdownString(e ? describeGameVfx(e).join("\n\n") : `\`${value}\`: not in the catalog (${c.label})`), range);
    }
    return readVfxNeed(this.modTexts(doc)).blocks.has(value) ? new vscode.Hover(this.blockMarkdown(doc, value), range) : undefined;
  }

  private blockMarkdown(doc: vscode.TextDocument, name: string): vscode.MarkdownString {
    const need = readVfxNeed(this.modTexts(doc));
    const b = need.blocks.get(name);
    const lines = [`\`${name}\`  _VFX block_`];
    if (b) lines.push(`${b.particles} particle blocks${b.light ? " and a light" : ""}: ${b.entries} of a VFX's ${VFX_LIMITS.entries} entries`);
    const game = this.catalog.catalog?.entries("vfx").length;
    lines.push(`**This mod's budget**\n\n${budgetLines(need, game).map((l) => `- ${l}`).join("\n")}`);
    if (overBudget(need, game)) lines.push("Over a limit: Kiln refuses the mod that reaches it, and none of its VFX load.");
    return new vscode.MarkdownString(lines.join("\n\n"));
  }

  hoverCs(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | undefined {
    const c = this.catalog.catalog;
    if (!c) return undefined;
    const line = doc.lineAt(pos.line).text;
    const s = stringAt(line, pos.character);
    if (!s || !CODE_NAME.test(line.slice(0, s.start - 1))) return undefined;
    const e = findGameVfx(c, line.slice(s.start, s.end));
    if (!e) return undefined;
    return new vscode.Hover(new vscode.MarkdownString(describeGameVfx(e).join("\n\n")), new vscode.Range(pos.line, s.start, pos.line, s.end));
  }
}
