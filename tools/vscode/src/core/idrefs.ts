// SPDX-License-Identifier: MIT
// Which JSON values in mod files are game-data references: schema/idrefs.json (generated from the
// schemas' "x-kiln-ref" annotations): {"format":1,"refs":[{"files":[globs],"pointer":"/items/add/*/base",
// "section":"items","forms":["name","id","guid"],"schema":"<file>"}]}; `*` = any key or index, a trailing `~` = the
// key itself. Forms: name, id, guid, glob, item:, template:, like:.
import { matchFiles } from "./glob";

export interface IdRef {
  files: string[];
  pointer: string;
  section: string;
  forms?: string[];
  schema?: string;
}

export interface IdRefs {
  format: number;
  refs: IdRef[];
  source?: string;
}

interface Compiled extends IdRef {
  segs: string[];
  key: boolean;
}

/** Used until schema/idrefs.json exists: the data-pack fields of the samples (content/*.json). */
export const FALLBACK_IDREFS: IdRefs = {
  format: 1,
  source: "built-in fallback",
  refs: [
    { files: ["**/content/**/*.json"], pointer: "/items/add/*/base", section: "items", forms: ["name"] },
    { files: ["**/content/**/*.json"], pointer: "/items/*~", section: "items", forms: ["name", "glob"] },
    { files: ["**/content/**/*.json"], pointer: "/recipes/add/*/inputs/*/item", section: "items", forms: ["name"] },
    { files: ["**/content/**/*.json"], pointer: "/recipes/add/*/outputs/*/item", section: "items", forms: ["name"] },
    { files: ["**/content/**/*.json"], pointer: "/recipes/*~", section: "recipes", forms: ["name", "glob"] },
    { files: ["**/content/**/*.json"], pointer: "/templates/*~", section: "templates", forms: ["name", "guid", "id"] },
    { files: ["**/content/**/*.json"], pointer: "/sequences/*~", section: "sequences", forms: ["name", "guid"] },
    { files: ["**/content/**/*.json"], pointer: "/buffs/*~", section: "buffs", forms: ["name", "id"] },
    { files: ["**/content/**/*.json"], pointer: "/perks/*~", section: "perks", forms: ["name", "guid"] },
    { files: ["**/content/**/*.json"], pointer: "/skills/*~", section: "skills", forms: ["name", "id"] },
    { files: ["**/content/**/*.json"], pointer: "/arsenals/*~", section: "arsenals", forms: ["name", "template:"] },
  ],
};

const KEYWORDS = new Set(["add", "none", "hand", "*", "@where", "format", "comment"]);

export class IdRefIndex {
  private refs: Compiled[];

  constructor(readonly table: IdRefs) {
    this.refs = (table.refs ?? []).map((r) => {
      let p = r.pointer;
      const key = p.endsWith("~") && !p.endsWith("~0") && !p.endsWith("~1");
      if (key) p = p.slice(0, -1);
      const segs = p
        .split("/")
        .slice(1)
        .map((s) => s.replace(/~1/g, "/").replace(/~0/g, "~"));
      return { ...r, segs, key };
    });
  }

  get size(): number {
    return this.refs.length;
  }

  /** Does any rule apply to this file at all (cheap pre-check for providers)? */
  appliesTo(paths: string[]): boolean {
    return this.refs.some((r) => matchFiles(r.files, paths));
  }

  /**
   * The rule for a JSON location: `path` from jsonc-parser (keys and indices), `atKey` when the cursor is on a
   * property name (the path's last element is then that name).
   */
  match(paths: string[], jsonPath: (string | number)[], atKey: boolean): IdRef | undefined {
    for (const r of this.refs) {
      if (r.key !== atKey || r.segs.length !== jsonPath.length) continue;
      let ok = true;
      for (let i = 0; i < r.segs.length; i++) {
        const s = r.segs[i];
        if (s !== "*" && s !== String(jsonPath[i])) {
          ok = false;
          break;
        }
      }
      if (!ok) continue;
      if (atKey && KEYWORDS.has(String(jsonPath[jsonPath.length - 1]))) continue;
      if (matchFiles(r.files, paths)) return r;
    }
    return undefined;
  }
}

/** Catalog sections a ref looks in ("*" or "any": every section). */
export function refSections(r: IdRef): string[] | undefined {
  return r.section === "*" || r.section === "any" ? undefined : [r.section];
}
