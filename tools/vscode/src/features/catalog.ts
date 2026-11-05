// SPDX-License-Identifier: MIT
// The game-data catalog: loading/watching, the Game Data explorer, completion and hover for game ids in mod JSON
// files (schema/idrefs.json decides which values are references) and hover on string literals in C#.
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { getLocation, Location } from "jsonc-parser";
import { Catalog, CatalogEntry, describeEntry, locateCatalog, stringAt } from "../core/catalog";
import { FALLBACK_IDREFS, IdRef, IdRefIndex, IdRefs, refSections } from "../core/idrefs";
import { Ctx } from "../ctx";

export const SCHEME = "kiln-catalog";

class EntryItem extends vscode.CompletionItem {
  constructor(
    label: vscode.CompletionItemLabel,
    readonly section: string,
    readonly entry: CatalogEntry,
  ) {
    super(label, vscode.CompletionItemKind.Constant);
  }
}

type Node = { kind: "section"; section: string } | { kind: "group"; section: string; prefix: string } | { kind: "entry"; section: string; entry: CatalogEntry };

export class CatalogFeature implements vscode.TreeDataProvider<Node>, vscode.TextDocumentContentProvider, vscode.Disposable {
  catalog?: Catalog;
  idrefs: IdRefIndex;
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly docChanged = new vscode.EventEmitter<vscode.Uri>();
  readonly onDidChange = this.docChanged.event;
  private watcher?: fs.FSWatcher;
  private disposables: vscode.Disposable[] = [];
  private iconCache = new Map<string, string | null>();
  private tree: vscode.TreeView<Node>;

  constructor(private readonly ctx: Ctx) {
    this.idrefs = new IdRefIndex(this.loadIdRefs());
    this.tree = vscode.window.createTreeView("kiln.catalog", { treeDataProvider: this, showCollapseAll: true });
    const sel = [{ language: "json" }, { language: "jsonc" }];
    this.disposables.push(
      this.changed,
      this.docChanged,
      this.tree,
      vscode.workspace.registerTextDocumentContentProvider(SCHEME, this),
      vscode.languages.registerCompletionItemProvider(
        sel,
        {
          provideCompletionItems: (d, p) => this.complete(d, p),
          // documentation (with the inlined icon) only for the item the user looks at
          resolveCompletionItem: (it) => {
            if (it instanceof EntryItem) {
              const md = new vscode.MarkdownString(this.markdown(it.section, it.entry));
              md.supportHtml = true;
              it.documentation = md;
            }
            return it;
          },
        },
        '"',
        ":",
      ),
      vscode.languages.registerHoverProvider(sel, { provideHover: (d, p) => this.hoverJson(d, p) }),
      vscode.languages.registerHoverProvider({ language: "csharp" }, { provideHover: (d, p) => this.hoverCs(d, p) }),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration("kiln.catalog")) this.reload();
      }),
      vscode.commands.registerCommand("kiln.catalog.refresh", () => this.reload(true)),
      vscode.commands.registerCommand("kiln.catalog.search", () => this.searchCommand()),
      vscode.commands.registerCommand("kiln.catalog.build", () => this.build()),
      vscode.commands.registerCommand("kiln.catalog.copyName", (n?: Node) => n?.kind === "entry" && vscode.env.clipboard.writeText(n.entry.name)),
      vscode.commands.registerCommand("kiln.catalog.copyGuid", (n?: Node) => {
        if (n?.kind !== "entry") return;
        if (n.entry.guid) vscode.env.clipboard.writeText(n.entry.guid);
        else vscode.window.showInformationMessage(`${n.entry.name} has no GUID (id ${n.entry.id ?? "?"})`);
      }),
      vscode.commands.registerCommand("kiln.catalog.insert", (n?: Node) => {
        const ed = vscode.window.activeTextEditor;
        if (n?.kind === "entry" && ed) ed.edit((b) => ed.selections.forEach((s) => b.replace(s, n.entry.name)));
      }),
      vscode.commands.registerCommand("kiln.catalog.show", (section: string, name: string) => this.show(section, name)),
    );
    this.reload();
  }

  dispose(): void {
    this.watcher?.close();
    for (const d of this.disposables) d.dispose();
  }

  /** schema/idrefs.json from the Kiln checkout (newest), else the bundled copy, else the built-in fallback. */
  private loadIdRefs(): IdRefs {
    const root = this.ctx.kilnRoot();
    for (const f of [root && path.join(root, "schema", "idrefs.json"), this.ctx.asset("data", "schemas", "idrefs.json")]) {
      if (!f || !fs.existsSync(f)) continue;
      try {
        const t = JSON.parse(fs.readFileSync(f, "utf8")) as IdRefs;
        if (t.format === 1 && Array.isArray(t.refs)) return { ...t, source: f };
      } catch (e) {
        this.ctx.out.warn(`${f}: ${(e as Error).message}`);
      }
    }
    return FALLBACK_IDREFS;
  }

  reload(interactive = false): void {
    this.idrefs = new IdRefIndex(this.loadIdRefs());
    const file = locateCatalog(this.ctx.setting("catalog.path", "") || undefined);
    this.watcher?.close();
    this.watcher = undefined;
    this.iconCache.clear();
    if (!file) {
      this.catalog = undefined;
      this.tree.message = undefined;
      if (interactive) vscode.window.showInformationMessage("Kiln: no game-data catalog found (kiln catalog build makes one).");
    } else {
      try {
        this.catalog = Catalog.load(file);
        const n = [...this.catalog.sections.values()].reduce((a, l) => a + l.length, 0);
        this.tree.message = `${this.catalog.label || path.basename(file)} · ${n} entries`;
        this.ctx.out.info(`catalog: ${file} (${n} entries; id fields from ${this.idrefs.table.source})`);
        try {
          let t: NodeJS.Timeout | undefined;
          this.watcher = fs.watch(path.dirname(file), (_e, name) => {
            if (name !== path.basename(file)) return;
            clearTimeout(t);
            t = setTimeout(() => this.reload(), 500);
          });
        } catch {
          /* no watch */
        }
      } catch (e) {
        this.catalog = undefined;
        this.tree.message = `catalog error: ${(e as Error).message}`;
        this.ctx.out.error(`catalog ${file}: ${(e as Error).message}`);
      }
    }
    vscode.commands.executeCommand("setContext", "kiln.hasCatalog", !!this.catalog);
    this.changed.fire(undefined);
  }

  private async build() {
    if (!(await this.ctx.hasCli())) {
      vscode.window.showErrorMessage("Kiln: building the catalog needs the kiln CLI (crates/kiln-cli; kiln.cli.path).");
      return;
    }
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: "Kiln: kiln catalog build" }, async () => {
      const r = await this.ctx.exec(this.ctx.cliPath(), ["catalog", "build"], { timeoutMs: 900_000, onStdout: (c) => this.ctx.out.info(c.trimEnd()) });
      if (r.code !== 0) vscode.window.showErrorMessage(`Kiln: kiln catalog build failed: ${(r.stderr || r.stdout).trim().slice(-400)}`);
    });
    this.reload(true);
  }

  // ---- tree

  getTreeItem(n: Node): vscode.TreeItem {
    if (n.kind === "section") {
      const it = new vscode.TreeItem(n.section, vscode.TreeItemCollapsibleState.Collapsed);
      it.description = String(this.catalog?.entries(n.section).length ?? 0);
      it.iconPath = new vscode.ThemeIcon("symbol-namespace");
      return it;
    }
    if (n.kind === "group") {
      const it = new vscode.TreeItem(n.prefix, vscode.TreeItemCollapsibleState.Collapsed);
      it.description = String(this.groupEntries(n.section, n.prefix).length);
      it.iconPath = new vscode.ThemeIcon("folder");
      return it;
    }
    const e = n.entry;
    const it = new vscode.TreeItem(e.displayName || e.name, vscode.TreeItemCollapsibleState.None);
    it.description = e.displayName ? e.name : (e.guid ?? e.id ?? "");
    const icon = this.catalog?.iconPath(e);
    it.iconPath = icon ? vscode.Uri.file(icon) : new vscode.ThemeIcon("symbol-constant");
    it.contextValue = "kiln.catalog.entry";
    it.command = { command: "kiln.catalog.show", title: "Show", arguments: [n.section, e.name] };
    return it;
  }

  resolveTreeItem(item: vscode.TreeItem, n: Node): vscode.TreeItem {
    if (n.kind === "entry") {
      const md = new vscode.MarkdownString(this.markdown(n.section, n.entry));
      md.supportHtml = true;
      item.tooltip = md;
    }
    return item;
  }

  private static prefixOf(name: string): string {
    const i = name.indexOf("_");
    return i > 0 ? name.slice(0, i) : name.slice(0, 1).toUpperCase();
  }

  private groupEntries(section: string, prefix: string): CatalogEntry[] {
    return (this.catalog?.entries(section) ?? []).filter((e) => CatalogFeature.prefixOf(e.name) === prefix);
  }

  getChildren(n?: Node): Node[] {
    const c = this.catalog;
    if (!c) return [];
    if (!n) return c.sectionNames().filter((s) => c.entries(s).length).map((section) => ({ kind: "section", section }));
    if (n.kind === "entry") return [];
    const sort = (l: CatalogEntry[]) => [...l].sort((a, b) => a.name.localeCompare(b.name));
    if (n.kind === "group") return sort(this.groupEntries(n.section, n.prefix)).map((entry) => ({ kind: "entry", section: n.section, entry }));
    const list = c.entries(n.section);
    if (list.length <= 300) return sort(list).map((entry) => ({ kind: "entry", section: n.section, entry }));
    const prefixes = [...new Set(list.map((e) => CatalogFeature.prefixOf(e.name)))].sort();
    return prefixes.map((prefix) => ({ kind: "group", section: n.section, prefix }));
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    const [section, ...rest] = uri.path.replace(/^\//, "").replace(/\.json$/, "").split("/");
    const name = decodeURIComponent(rest.join("/"));
    const hit = this.catalog?.resolve(name, [section]);
    return hit ? JSON.stringify(hit.entry, null, 2) + "\n" : `// ${name}: not in the catalog\n`;
  }

  private async show(section: string, name: string) {
    const uri = vscode.Uri.from({ scheme: SCHEME, path: `/${section}/${encodeURIComponent(name)}.json` });
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.languages.setTextDocumentLanguage(doc, "json");
    await vscode.window.showTextDocument(doc, { preview: true });
  }

  private async searchCommand() {
    const c = this.catalog;
    if (!c) {
      vscode.window.showInformationMessage("Kiln: no game-data catalog loaded.");
      return;
    }
    const qp = vscode.window.createQuickPick<vscode.QuickPickItem & { section: string; entry: CatalogEntry }>();
    qp.title = "Search game data (name, display name, GUID, id)";
    qp.matchOnDescription = true;
    qp.matchOnDetail = true;
    const update = () => {
      qp.items = c.search(qp.value || "", undefined, 300).map(({ section, entry }) => ({
        label: entry.displayName || entry.name,
        description: entry.displayName ? entry.name : "",
        detail: `${section}${entry.guid ? ` · ${entry.guid}` : ""}${entry.id ? ` · ${entry.id}` : ""}`,
        section,
        entry,
      }));
    };
    qp.onDidChangeValue(update);
    qp.onDidAccept(async () => {
      const it = qp.selectedItems[0];
      qp.hide();
      if (!it) return;
      const act = await vscode.window.showQuickPick(["Insert name at cursor", "Copy name", "Copy GUID", "Show entry"], { title: it.entry.name });
      if (act === "Insert name at cursor") vscode.commands.executeCommand("kiln.catalog.insert", { kind: "entry", section: it.section, entry: it.entry });
      else if (act === "Copy name") vscode.env.clipboard.writeText(it.entry.name);
      else if (act === "Copy GUID" && it.entry.guid) vscode.env.clipboard.writeText(it.entry.guid);
      else if (act === "Show entry") this.show(it.section, it.entry.name);
    });
    update();
    qp.show();
  }

  // ---- hover / completion

  private iconData(e: CatalogEntry): string | undefined {
    const p = this.catalog?.iconPath(e);
    if (!p) return undefined;
    if (!this.iconCache.has(p)) {
      try {
        const buf = fs.readFileSync(p);
        const ext = path.extname(p).slice(1).toLowerCase();
        const mime = ext === "png" ? "image/png" : ext === "jpg" || ext === "jpeg" ? "image/jpeg" : "image/webp";
        this.iconCache.set(p, buf.length < 512 * 1024 ? `data:${mime};base64,${buf.toString("base64")}` : null);
      } catch {
        this.iconCache.set(p, null);
      }
    }
    return this.iconCache.get(p) ?? undefined;
  }

  markdown(section: string, e: CatalogEntry): string {
    const icon = this.iconData(e);
    const lines = describeEntry(section, e);
    return (icon ? `<img src="${icon}" width="48" height="48" align="left">\n\n` : "") + lines.join("\n\n");
  }

  private filePaths(doc: vscode.TextDocument): string[] {
    const f = doc.uri.fsPath;
    const ws = vscode.workspace.getWorkspaceFolder(doc.uri);
    const out = [f];
    if (ws) out.push(path.relative(ws.uri.fsPath, f));
    return out;
  }

  /** The ref rule and the string token at a position, if the position is a game-id field. */
  refAt(doc: vscode.TextDocument, pos: vscode.Position): { ref?: IdRef; loc: Location; range?: vscode.Range; value?: string } | undefined {
    if (doc.uri.scheme !== "file" && doc.uri.scheme !== "untitled") return undefined;
    const paths = this.filePaths(doc);
    const text = doc.getText();
    const loc = getLocation(text, doc.offsetAt(pos));
    const ref = this.idrefs.appliesTo(paths) ? this.idrefs.match(paths, loc.path, loc.isAtPropertyKey) : undefined;
    const str = stringAt(doc.lineAt(pos.line).text, pos.character);
    let range: vscode.Range | undefined;
    let value: string | undefined;
    if (str) {
      range = new vscode.Range(pos.line, str.start, pos.line, str.end);
      value = doc.lineAt(pos.line).text.slice(str.start, str.end);
    }
    return { ref, loc, range, value };
  }

  complete(doc: vscode.TextDocument, pos: vscode.Position): vscode.CompletionItem[] | undefined {
    const c = this.catalog;
    if (!c) return undefined;
    const at = this.refAt(doc, pos);
    if (!at?.ref) return undefined;
    const forms = at.ref.forms ?? ["name"];
    const secs = refSections(at.ref) ?? c.sectionNames();
    const items: vscode.CompletionItem[] = [];
    const quoted = !!at.range;
    const prefix = forms.includes("item:") ? "item:" : forms.includes("template:") ? "template:" : forms.includes("like:") ? "like:" : "";
    const useGuid = !forms.includes("name") && forms.includes("guid");
    for (const s of secs) {
      for (const e of c.entries(s)) {
        const val = useGuid && e.guid ? e.guid : e.name;
        const label: vscode.CompletionItemLabel = { label: val, description: e.displayName };
        const it = new EntryItem(label, s, e);
        it.insertText = quoted ? `${prefix}${val}` : `"${prefix}${val}"`;
        it.filterText = `${prefix}${val} ${e.displayName ?? ""}`;
        if (at.range) it.range = at.range;
        it.detail = `${s}${e.guid && !useGuid ? ` · ${e.guid}` : ""}`;
        items.push(it);
      }
    }
    return items;
  }

  hoverJson(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | undefined {
    const c = this.catalog;
    if (!c) return undefined;
    const at = this.refAt(doc, pos);
    if (!at?.value || !at.range) return undefined;
    const secs = at.ref ? refSections(at.ref) : undefined;
    if (!at.ref && !this.idrefs.appliesTo(this.filePaths(doc)) && !/kiln\.mod\.json$|[\\/](content|assets|impact)[\\/]/.test(doc.uri.fsPath)) {
      return undefined;
    }
    const hit = c.resolve(at.value, secs);
    if (!hit) {
      if (at.ref && !at.ref.forms?.includes("glob") && !/[*?]/.test(at.value)) {
        return new vscode.Hover(new vscode.MarkdownString(`\`${at.value}\`: not in the catalog (${at.ref.section}; ${c.label})`), at.range);
      }
      return undefined;
    }
    const md = new vscode.MarkdownString(this.markdown(hit.section, hit.entry));
    md.supportHtml = true;
    return new vscode.Hover(md, at.range);
  }

  hoverCs(doc: vscode.TextDocument, pos: vscode.Position): vscode.Hover | undefined {
    const c = this.catalog;
    if (!c) return undefined;
    const str = stringAt(doc.lineAt(pos.line).text, pos.character);
    if (!str || str.end === str.start) return undefined;
    const inner = new vscode.Range(pos.line, str.start, pos.line, str.end);
    const hit = c.resolve(doc.getText(inner));
    if (!hit) return undefined;
    const md = new vscode.MarkdownString(this.markdown(hit.section, hit.entry));
    md.supportHtml = true;
    return new vscode.Hover(md, inner);
  }
}

