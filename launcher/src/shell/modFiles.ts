// The files a player can add as mods: what kiln_manage::library imports (library::archive_kind: zip, 7z and rar,
// unpacked into a private folder; a rar with a tool the PC has) and DLLs. Folders are dropped on the window (the file
// dialog picks files). The "Add mods" dialog and the hints that name the formats read this.

/** The file dialog's extensions, without the dot. */
export const MOD_FILE_EXTENSIONS = ["zip", "7z", "rar", "dll"];

/** The "Add mods" dialog's filter. */
export const MOD_FILE_FILTERS = [{ name: "Mods", extensions: MOD_FILE_EXTENSIONS }];

/** "zip, 7z, rar, folder or DLL", for a sentence. */
export const MOD_FILE_WORDS = "zip, 7z, rar, folder or DLL";
