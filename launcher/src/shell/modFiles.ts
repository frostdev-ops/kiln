// The files a player can add as mods: what kiln_manage::library imports (library::archive_kind: zip, 7z and rar,
// unpacked into a private folder; a rar with a tool the PC has) and DLLs. Folders are dropped on the window (the file
// dialog picks files). The "Add mods" dialog and the hints that name the formats read this.

/** The file dialog's extensions, without the dot. */
export const MOD_FILE_EXTENSIONS = ["zip", "7z", "rar", "dll"];

/** "zip" → "[zZ][iI][pP]": the extension in any letter case, as a glob. */
export const anyCase = (ext: string) => [...ext].map((c) => (c.toLowerCase() === c.toUpperCase() ? c : `[${c.toLowerCase()}${c.toUpperCase()}]`)).join("");

/** The "Add mods" dialog's filter for a platform. The Library takes an extension in any case (archive_kind lowers
 *  it), but Linux's dialogs match patterns case-sensitively. The dialog library (rfd 0.17, under tauri-plugin-dialog
 *  2.8) puts the extension unchanged after "*." on both of its Linux paths: GTK's gtk_file_filter_add_pattern
 *  (gtk3/file_dialog/dialog_ffi.rs) and the desktop portal's glob filters (xdg_desktop_portal.rs), whose GTK and GNOME
 *  backends hand the glob to the same GtkFileFilter. Its globs take character classes (checked with GTK 3 and 4:
 *  `*.[zZ][iI][pP]` takes NightOwl.Zip, a.zip and b.ZIP), so Linux gets one pattern per extension that matches every
 *  letter case. Windows matches `*.zip` without case and macOS by type: one plain pattern each. */
export function modFileFilters(windowsOrMac: boolean): { name: string; extensions: string[] }[] {
  return [{ name: "Mods", extensions: windowsOrMac ? MOD_FILE_EXTENSIONS : MOD_FILE_EXTENSIONS.map(anyCase) }];
}

/** The filter for this window's platform. */
export const MOD_FILE_FILTERS = modFileFilters(typeof navigator !== "undefined" && /Windows|Macintosh|Mac OS X/i.test(navigator.userAgent));

/** "zip, 7z, rar, folder or DLL", for a sentence. */
export const MOD_FILE_WORDS = "zip, 7z, rar, folder or DLL";
