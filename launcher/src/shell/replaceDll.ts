// "Replace DLL…" on a native mod's row (library_replace_dll, dev.29): a patched DLL becomes a new Library build of the
// same mod, and the build before it is kept for a roll back (shell/rollback.ts). Both builds carry the author's version;
// the row tells them apart by this label.
import type { LibraryVersion, ModKind } from "../api/commands";
import { t } from "../i18n";

/** Whether a row offers "Replace DLL…": a native DLL mod from the Library that the loaded profile has. */
export const canReplaceDll = (kind: ModKind | null, library: boolean, profileEntry: boolean) =>
  kind === "native" && library && profileEntry;

/** "replaced DLL, 15 Nov" for a build made by a replacement, "" for any other. */
export function replacementLabel(v: Pick<LibraryVersion, "source" | "imported"> | undefined): string {
  if (v?.source?.type !== "replaced-dll") return "";
  const date = new Date(v.imported);
  return t("shell.replaceDll.label", { date: date.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "UTC" }) });
}
