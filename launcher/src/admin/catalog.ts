// Local game catalogs for the admin pages. Game-derived data is never in the repo or a build:
// it stays in launcher/assets-local/ (gitignored, dev) or <data>/admin-assets/ (release builds), and the pages read it
// through assetsRoot(). Without it the pages fall
// back to what the server reports (the `enemies` catalog, item_search).
import { useQuery } from "@tanstack/react-query";
import { convertFileSrc } from "@tauri-apps/api/core";
import { isTauri } from "../api/commands";

/** The one base URL of the admin pages' local assets (layout: map/..., data/...):
 *  - VITE_KILN_ASSETS when set;
 *  - a release build of the launcher: the `kiln-asset` protocol, prefix admin/ (src-tauri/src/assets.rs:
 *    $KILN_ADMIN_ASSETS, then <data>/admin-assets): kiln-asset://localhost/admin/, on Windows
 *    http://kiln-asset.localhost/admin/;
 *  - dev (vite, `tauri dev`, a plain browser): /assets-local/ (vite serves launcher/assets-local; under tauri dev
 *    WebKitGTK refuses custom-scheme images and CSS from the http://localhost:1420 page). */
export function assetsRoot(): string {
  const env = import.meta.env.VITE_KILN_ASSETS as string | undefined;
  if (env) return env.endsWith("/") ? env : `${env}/`;
  if (isTauri() && !import.meta.env.DEV) {
    const base = convertFileSrc("", "kiln-asset");
    return `${base.endsWith("/") ? base : `${base}/`}admin/`;
  }
  return "/assets-local/";
}
export const ASSETS_ROOT: string = assetsRoot();

export interface CreatureTemplate {
  guid: string;
  template: string;
  name: string;
}

/** Creature template GUID -> name (the Enshrouded Interactive Map's data, MIT, Nicolas Ferreira), if fetched. */
export function useCreatureCatalog() {
  return useQuery({
    queryKey: ["assets-local", "creature-templates"],
    staleTime: Infinity,
    retry: false,
    queryFn: async (): Promise<CreatureTemplate[]> => {
      const r = await fetch(`${ASSETS_ROOT}data/creature-templates.json`);
      if (!r.ok || !(r.headers.get("content-type") ?? "").includes("json")) return [];
      const d = (await r.json()) as Record<string, { template: string; name?: string }>;
      return Object.entries(d)
        .filter(([, c]) => c.name)
        .map(([guid, c]) => ({ guid, template: c.template, name: c.name! }))
        .sort((a, b) => a.name.localeCompare(b.name));
    },
  });
}

/** Turns Enemy_Wildlife_Wolf_T2 into "Wolf" */
export const prettyTemplate = (t?: string | null) =>
  t ? t.replace(/^(Base_)?(Enemy|Animal)_(Wildlife_|Wildbeast_)?/, "").replace(/_(T\d|AG2|Dungeon|Patrol)$/g, "").replace(/_/g, " ") : "Creature";
