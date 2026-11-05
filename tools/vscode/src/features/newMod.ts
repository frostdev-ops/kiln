// SPDX-License-Identifier: MIT
// New mod from the Kiln templates: `kiln new` (lists its templates, installs Kiln.Templates when missing), else
// `dotnet new kiln-mod` with the template's options (installing Kiln.Templates from a local MDK feed on request).
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import { Ctx } from "../ctx";
import { Servers } from "./servers";

interface TemplateOption {
  name: string;
  type: "string" | "bool" | "choice";
  default?: string | boolean;
  description?: string;
  choices?: string[];
}

interface Template {
  id: string;
  name: string;
  description?: string;
  kind?: string;
  options?: TemplateOption[];
}

const SERVICES = ["Abilities", "Assets", "Audio", "Build", "Chat", "Client", "Combat", "Content", "Creatures", "Gameplay", "Hooks", "Items", "Network", "Permissions", "Players", "Saves", "Settings", "Skills", "Spawn", "Time", "Ui", "World"];

export function kebab(name: string): string {
  return name
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1-$2")
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

export function registerNewMod(ctx: Ctx, servers: Servers): vscode.Disposable {
  return vscode.commands.registerCommand("kiln.newMod", async () => {
    const parent = await pickParent();
    if (!parent) return;
    if (await ctx.hasCli()) return newWithCli(ctx, servers, parent);
    return newWithDotnet(ctx, servers, parent);
  });
}

async function pickParent(): Promise<string | undefined> {
  const ws = vscode.workspace.workspaceFolders ?? [];
  const items: (vscode.QuickPickItem & { dir?: string })[] = ws.map((f) => ({ label: f.name, detail: f.uri.fsPath, dir: f.uri.fsPath }));
  items.push({ label: "$(folder) Choose a folder…" });
  const pick = items.length === 1 ? items[0] : await vscode.window.showQuickPick(items, { title: "Create the mod in" });
  if (!pick) return undefined;
  if (pick.dir) return pick.dir;
  const uri = await vscode.window.showOpenDialog({ canSelectFolders: true, canSelectFiles: false, openLabel: "Create the mod here" });
  return uri?.[0]?.fsPath;
}

async function askName(): Promise<string | undefined> {
  return vscode.window.showInputBox({
    title: "Mod name (the project and folder name, e.g. FrostLanterns)",
    validateInput: (v) => (/^[A-Za-z_][A-Za-z0-9_.]*$/.test(v) ? undefined : "a C# identifier: letters, digits, _ and ."),
  });
}

async function askServices(): Promise<string | undefined> {
  const picks = await vscode.window.showQuickPick(
    SERVICES.map((s) => ({ label: s })),
    { title: "Kiln services the mod uses (KilnServices)", canPickMany: true },
  );
  return picks?.map((p) => p.label).join(";");
}

async function finish(dir: string, project?: string) {
  const inWs = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(dir));
  const main = project
    ? fs.readdirSync(path.dirname(project)).find((f) => f.endsWith("Mod.cs")) ?? path.basename(project)
    : fs.existsSync(path.join(dir, "kiln.mod.json"))
      ? "kiln.mod.json"
      : undefined;
  if (inWs) {
    if (main) await vscode.window.showTextDocument(vscode.Uri.file(path.join(project ? path.dirname(project) : dir, main)));
    await vscode.commands.executeCommand("kiln.mods.refresh");
    return;
  }
  const act = await vscode.window.showInformationMessage(`Kiln: created ${dir}`, "Open Folder", "Add to Workspace");
  if (act === "Open Folder") await vscode.commands.executeCommand("vscode.openFolder", vscode.Uri.file(dir), { forceNewWindow: false });
  else if (act === "Add to Workspace") vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders?.length ?? 0, 0, { uri: vscode.Uri.file(dir) });
}

async function newWithCli(ctx: Ctx, servers: Servers, parent: string) {
  const list = await ctx.exec(ctx.cliPath(), ["new", "--list", "--json"], { timeoutMs: 60_000 });
  let templates: Template[] = [];
  try {
    templates = JSON.parse(list.stdout.slice(list.stdout.indexOf("[")));
  } catch {
    vscode.window.showErrorMessage(`Kiln: kiln new --list failed: ${(list.stderr || list.stdout).trim().slice(-300)}`);
    return;
  }
  const t = (
    await vscode.window.showQuickPick(
      templates.map((x) => ({ label: x.name, description: x.id, detail: x.description, t: x })),
      { title: "New Kiln mod: template", matchOnDetail: true },
    )
  )?.t;
  if (!t) return;
  const name = await askName();
  if (!name) return;
  const sets: string[] = [];
  for (const o of t.options ?? []) {
    if (["name", "dir", "server", "feed"].includes(o.name)) continue;
    let v: string | undefined;
    if (o.name === "services") v = await askServices();
    else if (o.type === "bool") {
      const p = await vscode.window.showQuickPick(["no", "yes"], { title: `${o.name}: ${o.description ?? ""}`, placeHolder: String(o.default ?? false) });
      if (p === undefined) return;
      v = p === "yes" ? "true" : "false";
    } else if (o.type === "choice" && o.choices?.length) {
      v = await vscode.window.showQuickPick(o.choices, { title: `${o.name}: ${o.description ?? ""}`, placeHolder: String(o.default ?? "") });
    } else {
      v = await vscode.window.showInputBox({
        title: `${o.name}: ${o.description ?? ""}`,
        value: o.name === "modId" ? kebab(name) : String(o.default ?? ""),
      });
    }
    if (v === undefined) return;
    if (v !== "" && v !== String(o.default ?? "")) sets.push("--set", `${o.name}=${v}`);
  }
  const s = servers.selected();
  const args = ["new", t.id, name, "--dir", parent, ...(s ? ["--server", s.dir] : []), ...sets, "--json"];
  const r = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Kiln: kiln new ${t.id} ${name}` }, () =>
    ctx.exec(ctx.cliPath(), args, { timeoutMs: 180_000, cwd: parent }),
  );
  let res: { ok?: boolean; dir?: string; project?: string | null; error?: { message?: string } } = {};
  try {
    res = JSON.parse(r.stdout.slice(r.stdout.indexOf("{")));
  } catch {
    /* below */
  }
  if (!res.ok || !res.dir) {
    vscode.window.showErrorMessage(`Kiln: kiln new failed: ${res.error?.message ?? (r.stderr || r.stdout).trim().slice(-400)}`);
    return;
  }
  await finish(res.dir, res.project ?? undefined);
}

/** A local MDK feed with Kiln.Templates (managed/Kiln.Sdk/pack.sh writes $KILN_OUT/nuget). */
function findFeeds(ctx: Ctx): string[] {
  const root = ctx.kilnRoot();
  const out: string[] = [];
  if (root && fs.existsSync(path.join(root, ".out"))) {
    for (const d of fs.readdirSync(path.join(root, ".out"))) {
      const feed = path.join(root, ".out", d, "nuget");
      try {
        if (fs.readdirSync(feed).some((f) => /^kiln\.templates\..*\.nupkg$/i.test(f))) out.push(feed);
      } catch {
        /* not a feed */
      }
    }
  }
  return out;
}

async function newWithDotnet(ctx: Ctx, servers: Servers, parent: string) {
  const dotnet = ctx.dotnet();
  const listed = await ctx.exec(dotnet, ["new", "list", "kiln-mod"], { timeoutMs: 60_000 });
  if (listed.error) {
    vscode.window.showErrorMessage(`Kiln: ${dotnet} not found (kiln.dotnet.path), and the kiln CLI is not installed.`);
    return;
  }
  let feed: string | undefined;
  if (listed.code !== 0 || !/kiln-mod/.test(listed.stdout)) {
    const feeds = findFeeds(ctx);
    const items: (vscode.QuickPickItem & { feed?: string })[] = feeds.map((f) => ({ label: f, feed: f }));
    items.push({ label: "$(folder) Choose a feed folder…" });
    const pick = await vscode.window.showQuickPick(items, {
      title: "Kiln.Templates is not installed: install it from a local MDK feed (managed/Kiln.Sdk/pack.sh)",
    });
    if (!pick) return;
    feed = pick.feed ?? (await vscode.window.showOpenDialog({ canSelectFolders: true, openLabel: "Use this feed" }))?.[0]?.fsPath;
    if (!feed) return;
    const inst = await ctx.exec(dotnet, ["new", "install", "Kiln.Templates", "--add-source", feed], { timeoutMs: 180_000 });
    if (inst.code !== 0) {
      vscode.window.showErrorMessage(`Kiln: dotnet new install Kiln.Templates failed: ${(inst.stderr || inst.stdout).trim().slice(-400)}`);
      return;
    }
  }
  const name = await askName();
  if (!name) return;
  const modId = await vscode.window.showInputBox({ title: "Mod id", value: kebab(name), validateInput: (v) => (/^[a-z0-9][a-z0-9._-]{0,63}$/.test(v) ? undefined : "[a-z0-9][a-z0-9._-]{0,63}") });
  if (!modId) return;
  const services = await askServices();
  if (services === undefined) return;
  const tests = await vscode.window.showQuickPick(["yes", "no"], { title: "Add a Kiln.Testing test class ([KilnTest], auto-run)?" });
  if (!tests) return;
  const s = servers.selected();
  const dir = path.join(parent, name);
  const args = ["new", "kiln-mod", "-n", name, "-o", dir, "--modId", modId];
  if (services) args.push("--services", services);
  if (tests === "yes") args.push("--tests");
  const feedForProject = feed ?? findFeeds(ctx)[0];
  if (feedForProject) args.push("--feed", feedForProject);
  if (s) args.push("--server", s.dir);
  const r = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Kiln: dotnet new kiln-mod -n ${name}` }, () =>
    ctx.exec(dotnet, args, { timeoutMs: 180_000, cwd: parent }),
  );
  if (r.code !== 0) {
    vscode.window.showErrorMessage(`Kiln: dotnet new kiln-mod failed: ${(r.stderr || r.stdout).trim().slice(-400)}`);
    return;
  }
  await finish(dir, path.join(dir, `${name}.csproj`));
}
