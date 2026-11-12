// SPDX-License-Identifier: MIT
// Where the Kiln Launcher keeps its data, found the way the launcher finds it (crates/kiln-manage/src/paths.rs and
// data_folder.rs): $KILN_DATA_DIR wins; otherwise the default root, unless the pointer file `data-folder.json` in it
// names another folder (the player moved the data folder in Settings, Library, Data folder).
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

/** The default root: %LOCALAPPDATA%\Kiln on Windows, $XDG_DATA_HOME/kiln (or ~/.local/share/kiln) elsewhere. */
export function defaultDataRoot(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string {
  return platform === "win32"
    ? path.join(env.LOCALAPPDATA ?? os.homedir(), "Kiln")
    : path.join(env.XDG_DATA_HOME || path.join(os.homedir(), ".local", "share"), "kiln");
}

/**
 * The folder the pointer in `root` names, or undefined when there is no usable pointer: none, unreadable, not JSON,
 * a newer format, or a path that is not absolute. The launcher shows a dialog for a broken pointer; a reader here
 * falls back to the default root, where it finds nothing it would write.
 */
export function pointedDataDir(root: string): string | undefined {
  let text: string;
  try {
    text = fs.readFileSync(path.join(root, "data-folder.json"), "utf8");
  } catch {
    return undefined;
  }
  try {
    const v = JSON.parse(text.replace(/^﻿/, "")) as { format?: unknown; path?: unknown };
    if (typeof v.format === "number" && v.format > 1) return undefined;
    if (typeof v.path !== "string" || !path.isAbsolute(v.path) || v.path.startsWith("\\\\?\\")) return undefined;
    return v.path;
  } catch {
    return undefined;
  }
}

/** The data folder in use: $KILN_DATA_DIR, else the pointer's folder, else the default root. */
export function kilnDataDir(env: NodeJS.ProcessEnv = process.env, platform: NodeJS.Platform = process.platform): string {
  if (env.KILN_DATA_DIR) return env.KILN_DATA_DIR;
  const root = defaultDataRoot(env, platform);
  return pointedDataDir(root) ?? root;
}
