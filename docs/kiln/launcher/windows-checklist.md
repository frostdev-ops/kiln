# Windows: what still needs a real PC

Verified under Wine: NSIS install/uninstall, `--selftest` 11/11, WebView2 UI (runtime 156, `--no-sandbox`),
Rust suites for x86_64-pc-windows-msvc, proxyhost with `C:\` paths (107/107), a dedicated server installed, started,
administered over KAP and stopped with Ctrl-C by the Windows runner. Test user "Jörg Müller" (non-ASCII paths).

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

Notes: never put `mscoree=` in WINEDLLOVERRIDES when testing servers (the .NET host fails with 0x8007007E); in a
WebView2 test prefix, kill `MicrosoftEdgeUpdate.exe`.
