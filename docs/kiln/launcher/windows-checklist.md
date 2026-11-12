# Windows: what still needs a real PC

Verified under Wine: NSIS install/uninstall, `--selftest` 11/11, WebView2 UI (runtime 156, `--no-sandbox`),
Rust suites for x86_64-pc-windows-msvc, proxyhost with `C:\` paths (107/107), a dedicated server installed, started,
administered over KAP and stopped with Ctrl-C by the Windows runner. Test user "Jörg Müller" (non-ASCII paths).
The data folder's move (dev.25, `tests/data_folder*.rs` for x86_64-pc-windows-msvc, 93/93): to `D:\` (made `D:\Kiln`),
to another letter on the same disk, to another volume (exFAT `E:`) and within it (with a crashed move's recovery), to
`D:\Spiele Ordner\Jörg Müller\データ`, with a 259-character library path (a 260 one refused) and a data folder past 260
characters, each and back; `\\?\` and another case; junctions refused, and no old copy removed through one;
`%LOCALAPPDATA%\Kiln\state` untouched; a file held without read sharing (fails the move cleanly, nothing left in the
target, on the same letter and another); a file open for writing kept and named during a removal, a writer refused
while a file is proved and removed; a file the move made held without delete sharing while the move fails (named, the
target stays marked until the next start removes it); a re-activation that fails (the old copy stays until the profile
is activated again); the journal's write-through rename; the lock across two processes; read-only files removed
through a handle with read and `DELETE` only (Wine refuses the right to write attributes on a read-only file; Windows
grants it, see item 10); the attribute fallback's delete, and its read-only put back when refused; set-aside leftovers
and 250-byte names. A file another program holds open with delete sharing, on `C:` and exFAT `E:` in both removals:
through the POSIX delete its name goes at once and the removal is done; through the classic delete (as on FAT, exFAT
and older Windows) its name stays, it is counted removed and named as on its way out, and once the program lets go it
is gone and the recovery finishes (a real exFAT stick: item 10).

On a real Windows 10/11 PC:
1. NSIS install with the WebView2 bootstrapper on a clean PC; `kiln-launcher.exe --selftest --out r.json` from cmd and
   PowerShell.
2. UI: native dropdowns (blank under Wine), folder/file dialogs, drag and drop, high DPI, the save editor's art over
   `http://kiln-asset.localhost`.
3. Steam discovery from the real registry plus a second library on another drive; `game_running` while Steam runs the
   game; launching through `steam://` (ShellExecuteW).
4. The client: proxy load from `C:\Program Files (x86)\…`, a profile with a native dllmain mod under `%LOCALAPPDATA%`,
   a user name with an umlaut, Steam's Play button.
5. Servers: start/stop from the launcher, Ctrl-C to its console (also a Steam-started server with its own console
   window), admin pages, the firewall prompt for UDP and the TCP admin port, `steamcmd.exe` on PATH.
6. Defender or antivirus against the dinput8 proxy and the installer's file renames.
7. Saved Games moved to another drive; a OneDrive-redirected Documents folder (save editor backups).
8. A PC where 0.2.0-dev.19's "Mod Manager Download links" was on over Vortex: start a build with Nexus off, then
   `reg query HKCU\Software\Classes\nxm\shell\open\command` shows Vortex again (the log says "gave the nxm links back
   to Vortex") and a Nexus link opens Vortex. A stored personal API key in Credential Manager
   (`Kiln Launcher/nexusmods-api-key`) is gone after that start. Not tested under Wine: the registry restore.
9. A folder that is a junction inside the game folder, or in a profile's mod folder, is still refused; a junction
   ABOVE the game folder (a Steam library reached through one) works.
10. The data folder (Settings, "Data folder", Change), with a game folder and a profile active:
   - to another drive and back, playing in between: the dialog shows the size and the free space; after the restart
     Settings shows `D:\…\Kiln`, `%LOCALAPPDATA%\Kiln` holds only `data-folder.json`, `state\` and folders the launcher
     does not know; the game loads the profile's mods (`kiln\profile.json` `configDir` names the new place); the move
     back leaves nothing of Kiln on `D:`.
   - a drive root: picking `D:\` makes `D:\Kiln`; nothing else on `D:\` changes.
   - `D:\Spiele Ordner\Jörg Müller`: the move works and the game loads its mods from there.
   - a path that is too long: a folder deep enough that a library file would pass 259 characters is refused with
     "a path the game reads would be N characters long, more than Windows allows (259)"; nothing is copied. A folder
     deeper than 259 characters with a library small enough: the dialog shows its free space (not "unknown").
   - a second launcher open during a move: Change is refused ("Another Kiln Launcher is using the data folder"); a
     launcher started while a move runs does not open the data folder until it ends.
   - antivirus (Defender real-time protection on; a file held open by another program): a large library moves; a file
     that cannot be read fails the move naming it, the old place stays in use and complete. A file of the old copy
     open in another program while the old copy is removed stays, named, and goes at the next start.
   - read-only files in the library (an archive unpacked with the attribute), on NTFS and on an exFAT stick: they move
     and their old copy goes.
   - a file of the old copy that another program holds open with delete sharing while the old copy is removed, on an
     exFAT stick (no POSIX deletes there): the result names it as on its way out; it is gone once the program lets go,
     and the next start finishes.
   - a launcher of dev.24 or older started after a move: it starts as on a first run (empty library) at
     `%LOCALAPPDATA%\Kiln` and does not touch the moved folder; dev.25 started again uses the moved folder, and if the
     old launcher activated a profile, says the game runs another data folder's profile.

Notes: never put `mscoree=` in WINEDLLOVERRIDES when testing servers (the .NET host fails with 0x8007007E); in a
WebView2 test prefix, kill `MicrosoftEdgeUpdate.exe`.
