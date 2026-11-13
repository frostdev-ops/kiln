// The files a player can add as mods: what kiln_manage::library imports (library::archive_kind: zip, 7z and rar,
// unpacked into a private folder; a rar with a tool the PC has) and DLLs. Folders are dropped on the window (the file
// dialog picks files). The "Add mods" dialog and the hints that name the formats read this.

/** The file dialog's extensions, without the dot. */
export const MOD_FILE_EXTENSIONS = ["zip", "7z", "rar", "dll"];

/** The "Add mods" dialog's filter for a platform. The Library takes an extension in any case (archive_kind lowers
 *  it), but Linux's dialogs (rfd: GTK's gtk_file_filter_add_pattern, the desktop portal's globs) match patterns
 *  case-sensitively, so a `mod.7Z` or `MOD.RAR` would stay hidden: there the upper-case patterns are listed too. Windows
 *  matches `*.zip` without case and macOS by type, so they keep one pattern each. */
export function modFileFilters(windowsOrMac: boolean): { name: string; extensions: string[] }[] {
  const exts = windowsOrMac ? MOD_FILE_EXTENSIONS : [...MOD_FILE_EXTENSIONS, ...MOD_FILE_EXTENSIONS.map((e) => e.toUpperCase())];
  return [{ name: "Mods", extensions: exts }];
}

/** The filter for this window's platform. */
export const MOD_FILE_FILTERS = modFileFilters(typeof navigator !== "undefined" && /Windows|Macintosh|Mac OS X/i.test(navigator.userAgent));

/** "zip, 7z, rar, folder or DLL", for a sentence. */
export const MOD_FILE_WORDS = "zip, 7z, rar, folder or DLL";
