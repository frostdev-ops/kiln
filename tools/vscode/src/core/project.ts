// SPDX-License-Identifier: MIT
// Mods in a workspace: a folder with kiln.mod.json and/or a Kiln.Sdk project (<Project Sdk="Kiln.Sdk/…"> or an
// Import of managed/Kiln.Sdk/Sdk/Sdk.props). The mod id is kiln.mod.json's "id", else <KilnModId>, else the
// lower-case AssemblyName with other characters as '-' (Kiln.Mod.targets).
import * as fs from "node:fs";
import * as path from "node:path";
import { parse as parseJsonc } from "jsonc-parser";

export interface ModInfo {
  id: string;
  name: string;
  version?: string;
  side?: string;
  /** the mod folder (kiln.mod.json's, else the project's) */
  dir: string;
  manifest?: string;
  project?: string;
  /** references Kiln.Testing (KilnServices contains Testing) */
  testing: boolean;
  /** has impact/cook.json */
  impactCook?: string;
  manifestData?: Record<string, unknown>;
}

export function readManifest(file: string): Record<string, unknown> | undefined {
  try {
    const v = parseJsonc(fs.readFileSync(file, "utf8"), [], { allowTrailingComma: true });
    return v && typeof v === "object" ? (v as Record<string, unknown>) : undefined;
  } catch {
    return undefined;
  }
}

export function isKilnProject(csprojText: string): boolean {
  return /Sdk\s*=\s*"Kiln\.Sdk(\/[^"]*)?"/.test(csprojText) || /Kiln\.Sdk[\\/]Sdk[\\/]Sdk\.props/.test(csprojText);
}

function prop(text: string, name: string): string | undefined {
  const m = new RegExp(`<${name}(?:\\s[^>]*)?>([^<]*)</${name}>`).exec(text);
  return m ? m[1].trim() : undefined;
}

export function defaultModId(assemblyName: string): string {
  return assemblyName.toLowerCase().replace(/[^a-z0-9._-]/g, "-");
}

/** One mod from a folder (holding kiln.mod.json and/or one .csproj). */
export function modFromDir(dir: string): ModInfo | undefined {
  let entries: string[];
  try {
    entries = fs.readdirSync(dir);
  } catch {
    return undefined;
  }
  const manifest = entries.includes("kiln.mod.json") ? path.join(dir, "kiln.mod.json") : undefined;
  const projName = entries.find((e) => e.endsWith(".csproj"));
  let project: string | undefined;
  let projText = "";
  if (projName) {
    projText = fs.readFileSync(path.join(dir, projName), "utf8");
    if (isKilnProject(projText)) project = path.join(dir, projName);
  }
  if (!manifest && !project) return undefined;
  const m = manifest ? readManifest(manifest) : undefined;
  const asm = (project && prop(projText, "AssemblyName")) || (projName ? projName.slice(0, -7) : path.basename(dir));
  const id = (typeof m?.id === "string" && m.id) || (project && prop(projText, "KilnModId")) || defaultModId(asm);
  const services = (project && prop(projText, "KilnServices")) || "";
  const cook = path.join(dir, "impact", "cook.json");
  return {
    id,
    name: (typeof m?.name === "string" && m.name) || (project && prop(projText, "KilnModName")) || asm,
    version: (typeof m?.version === "string" && m.version) || (project && prop(projText, "Version")) || undefined,
    side: (typeof m?.side === "string" && m.side) || (project && prop(projText, "KilnSide")) || undefined,
    dir,
    manifest,
    project,
    testing: services.split(/[;,\s]+/).some((s) => s.toLowerCase() === "testing") || /Kiln\.Testing/.test(projText),
    impactCook: fs.existsSync(cook) ? cook : undefined,
    manifestData: m,
  };
}

/** The mod a file belongs to: the nearest folder upwards (up to `stop`) that is a mod. */
export function modForFile(file: string, stop?: string): ModInfo | undefined {
  let d = path.dirname(file);
  const end = stop ? path.resolve(stop) : path.parse(d).root;
  for (;;) {
    const m = modFromDir(d);
    if (m) return m;
    if (d === end || path.dirname(d) === d) return undefined;
    d = path.dirname(d);
  }
}
