Kiln server pack @VERSION@
@RULE@

Kiln for an Enshrouded dedicated server (runtime @RUNTIME_VERSION@). Unzip this pack into the server folder,
the folder that holds enshrouded_server.exe, and start the server. That's the whole install.

Full guide: https://frostdev.io/kiln/wiki (Dedicated servers), or docs/kiln/dedicated-servers.md in the source.
Kiln is MIT licensed: kiln/LICENSE.txt. Third-party licences: kiln/THIRD-PARTY-NOTICES.txt.
Enshrouded is a trademark of Keen Games GmbH. Kiln is not affiliated with or endorsed by Keen Games.

What's inside (@FILE_COUNT@ files):
  dinput8.dll    loads Kiln when the server starts
  kiln/          Kiln itself: its core, services, .NET runtime, symbol files, Kiln.Admin
Parts:
@COMPONENTS@


Before you start
----------------
- Stop the server first.
- Back up the savegame folder.
- Already a dinput8.dll in the server folder? That's another mod loader. Unzipping replaces it and turns that loader
  off. Keep a copy. kilnctl (below) can install Kiln next to it instead.


Rented server (GTXGaming, G-Portal, ...) with FTP or a file manager
--------------------------------------------------------------------
1. Stop the server in the host's panel.
2. Unzip this pack on your PC. You get dinput8.dll, the kiln folder and this file.
3. Connect with FTP (FileZilla, WinSCP) or open the host's file manager.
4. Find the folder with enshrouded_server.exe.
5. Upload dinput8.dll and the whole kiln folder into it. Keep the folder structure.
   Some file managers can unzip: upload the zip into that folder and unzip it there.
6. Start the server, wait a minute, then check kiln/logs/kiln.log (see "Does it work?").
Some hosts run the Windows server on Linux with Wine. Then Kiln needs the setting WINEDLLOVERRIDES="dinput8=n,b".
If kiln/logs/kiln.log never appears, ask the host's support whether they can set it. Some hosts don't allow custom
DLLs at all.


Your own Windows PC
-------------------
1. Stop the server.
2. Unzip this pack into the server folder (where enshrouded_server.exe is). With Steam's "Enshrouded Dedicated
   Server" tool that's usually ...\steamapps\common\EnshroudedServer.
3. Start the server as usual. Windows loads dinput8.dll from that folder by itself.
Note: Kiln's server parts were tested under Wine on Linux, not yet on a real Windows PC. Please report how it goes.


Your own Linux server
---------------------
Use kilnctl from the same release (runtime/kilnctl, with kiln-runtime-@VERSION@.zip): it installs the game with
SteamCMD, sets up Wine, installs Kiln and runs the server, with systemd units for start at boot and daily backups
(runtime/kiln@.service, kiln-backup@.service, kiln-backup@.timer). See docs/kiln/kilnctl.md.
Running the server under Wine by hand? Unzip this pack the same way and start Wine with WINEDLLOVERRIDES="dinput8=n,b".


Does it work?
-------------
Open kiln/logs/kiln.log after the server has started. Look for two lines:
  build <long number>: verified
  boot complete; handing control to the game
Both there: Kiln is running.
"safe idle: build ... is not verified" instead: the game was updated and this Kiln doesn't know the new build yet.
The server runs normally, without Kiln and its mods, until a Kiln update adds the build.
No kiln.log at all: the server didn't load dinput8.dll (wrong folder, or Wine without the setting above).

Server builds this pack knows:
@BUILDS@


Mods on the server
------------------
- Kiln mods: one folder per mod in kiln/mods/ (kiln/mods/<mod>/ with its kiln.mod.json inside).
- Native mods: kiln/native/<mod>/.
- Restart the server after adding or removing a mod.
What players need depends on the mod (the "clients" value in its kiln.mod.json):
  none   anyone can join, with or without Kiln
  kiln   players need Kiln; the server sends them what the mod adds
  mod    players need Kiln and the mod itself (the Kiln Launcher can fetch a server's modpack)
Mods made for other loaders (EML, ShroudForge, EMF, ...) need Kiln's part for that loader. This pack has the parts
listed under "Parts" at the top; for any other, use the full runtime with kilnctl.


Commands without the game: the console folder
---------------------------------------------
Put a text file with a .cmd ending into kiln/control/console/, one command per line, for example:
  help
  creative status
Within a few seconds Kiln runs it, deletes the .cmd file and writes <name>.result.json next to it with the output.
Commands run as the server console, with full rights. This works over FTP: upload the file as test.txt, then rename
it to test.cmd, so Kiln never reads a half-uploaded file.
Players with admin rights can also use chat commands: type /help in game.


Game updates
------------
After Enshrouded updates, Kiln checks the new server build. If it doesn't know it yet, the server runs vanilla
(mods off) until a Kiln update. Then get the new server pack and update Kiln (below).


Updating Kiln
-------------
1. Stop the server.
2. Delete kiln/dotnet, kiln/host, kiln/services and kiln/sigdb (Kiln's own files; your mods, settings and data are
   elsewhere).
3. Upload the new pack the same way, replacing dinput8.dll and the files in kiln/.
   Changed kiln/kiln.json? Don't replace it, or redo your change afterwards.
4. Start the server and check kiln/logs/kiln.log.


Removing Kiln
-------------
1. Stop the server.
2. Delete dinput8.dll and the kiln folder.
   Want to keep your mods' data? Keep kiln/data (and kiln/config for their settings) somewhere first.
3. Start the server. It runs vanilla again. Your savegame is untouched.
