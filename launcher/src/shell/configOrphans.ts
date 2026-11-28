// The Configs window's files of mods that are not installed (0.2.0-dev.31.31; HTR: Mods, More, Configs listed the configs
// of mods he had removed, and their switches looked live). The backend says which (`ConfigFileInfo.orphaned`, kiln_manage::
// config_orphans: the file's mod is in no profile entry, not in the Library, not in a game folder; never a guess); these are
// the pure parts the window uses: the two groups, which file is open, and the words after a Clear.
import type { ConfigFileInfo, SettingsRemoval } from "../api/commands";

/** The files of installed mods (the list as it was, in its order) and the files of mods that are gone. */
export function splitConfigs(list: ConfigFileInfo[]): { installed: ConfigFileInfo[]; orphaned: ConfigFileInfo[] } {
  return { installed: list.filter((f) => !f.orphaned), orphaned: list.filter((f) => f.orphaned) };
}

/** The file the window shows: the one picked (while it is still listed), else `initial` (a file, or the mod whose file), else the first
 *  with a schema, else the first: the files of installed mods before the others, so an orphan is never what opens first. */
export function pickConfig(list: ConfigFileInfo[], picked: string | null, initial?: string): string | null {
  if (picked && list.some((f) => f.file === picked)) return picked;
  const { installed, orphaned } = splitConfigs(list);
  const order = [...installed, ...orphaned];
  const found = order.find((f) => initial && (f.file === initial || f.mod === initial)) ?? order.find((f) => f.hasSchema) ?? order[0];
  return found?.file ?? null;
}

/** What a file is called in the list and the dialogs: its mod, else its file name. */
export const configName = (f: Pick<ConfigFileInfo, "mod" | "file">): string => f.mod ?? f.file;

/** The toast after a Clear: where the copy went (the title and the detail), or that nothing was there. */
export function clearedWords(r: SettingsRemoval, t: (key: string, vars?: Record<string, string | number>) => string): { title: string; detail: string } {
  return r.backup
    ? { title: t("configEditor.orphans.cleared"), detail: t("configEditor.orphans.clearedDetail", { path: r.backup }) }
    : { title: t("configEditor.orphans.nothing"), detail: t("configEditor.orphans.nothingDetail") };
}
