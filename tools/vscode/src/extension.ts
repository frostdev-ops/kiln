// SPDX-License-Identifier: MIT
// Kiln for VS Code: entry point. Features live in src/features, pure logic (unit-tested headless) in src/core.
import * as fs from "node:fs";
import * as vscode from "vscode";
import { Ctx } from "./ctx";
import { CatalogFeature } from "./features/catalog";
import { VfxFeature } from "./features/vfx";
import { registerConsole } from "./features/console";
import { DebugFeature } from "./features/debug";
import { ImpactFeature } from "./features/impact";
import { Lint } from "./features/lint";
import { LogFeature } from "./features/log";
import { Mods } from "./features/mods";
import { registerNewMod } from "./features/newMod";
import { Servers } from "./features/servers";
import { Testing } from "./features/testing";

export interface KilnApi {
  ctx: Ctx;
  servers: Servers;
  mods: Mods;
  lint: Lint;
  catalog: CatalogFeature;
  log: LogFeature;
  testing: Testing;
  impact: ImpactFeature;
  debug: DebugFeature;
  vfx: VfxFeature;
}

export function activate(context: vscode.ExtensionContext): KilnApi {
  const ctx = new Ctx(context);
  const schemaMap = (() => {
    try {
      return JSON.parse(fs.readFileSync(ctx.asset("data", "schemas", "schemas.json"), "utf8")).schemas as { fileMatch: string[] }[];
    } catch {
      return [];
    }
  })();
  const servers = new Servers(ctx);
  const mods = new Mods(ctx, servers);
  const lint = new Lint(ctx, mods, schemaMap);
  const catalog = new CatalogFeature(ctx);
  const vfx = new VfxFeature(ctx, catalog);
  const log = new LogFeature(ctx, servers, mods);
  const testing = new Testing(ctx, mods, servers);
  log.testLocator = (mod, name) => testing.locate(mod, name);
  const impact = new ImpactFeature(ctx);
  const debug = new DebugFeature(ctx, servers);
  context.subscriptions.push(
    ctx,
    servers,
    mods,
    lint,
    catalog,
    vfx,
    log,
    testing,
    impact,
    debug,
    registerConsole(ctx, servers),
    registerNewMod(ctx, servers),
    vscode.commands.registerCommand("kiln.showOutput", () => ctx.out.show()),
  );
  void testing.discover();
  ctx.out.info(`Kiln ${context.extension.packageJSON.version} active; Kiln checkout: ${ctx.kilnRoot() ?? "none"}`);
  return { ctx, servers, mods, lint, catalog, vfx, log, testing, impact, debug };
}

export function deactivate(): void {
  /* disposables do the work */
}
