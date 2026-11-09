Kiln @VERSION@ (Nexus Mods)
@RULE@=============

Kiln is a mod framework and launcher for Enshrouded: C# and native mods, mod profiles, local dedicated servers and
their administration. Built @DATE@ from @SOURCE@. Kiln is MIT licensed; the launcher carries its third-party
licences (THIRD-PARTY-NOTICES.txt) and CREDITS.md says whose work Kiln builds on.

Enshrouded is a trademark of Keen Games GmbH. Kiln is not affiliated with or endorsed by Keen Games.


Which file do I want?
---------------------
Most players want the Kiln Launcher. It installs Kiln into the game and manages mods and profiles.

  Linux    Kiln Launcher (Linux): @APPIMAGE@
           One file, no install: make it executable and run it.
             chmod +x @APPIMAGE@ && ./@APPIMAGE@
           Or Kiln Launcher (Linux .deb): @DEB@ (Debian, Ubuntu)
           Runs on SteamOS (Steam Deck) and distros with glibc 2.35 or newer: Ubuntu 22.04+, Debian 12+, Fedora 36+.

  Windows  Kiln Launcher (Windows): @SETUP@
           Installs for your user (no administrator rights).
@SMARTSCREEN@

Then open the launcher, let it find Enshrouded in your Steam libraries, and press Install on Play. The launcher
carries the matching Kiln runtime (@RUNTIME_VERSION@).

Add mods: use Manual Download in your browser, then drop the archive on the launcher window or import it through
Library. No launcher account sign-in or Mod Manager Download handler is required for this route.

Also on the Kiln page:
  Kiln runtime (manual install)   the Kiln runtime alone with kilnctl, for a dedicated server or a manual install
                                  (INSTALL-RUNTIME.txt in it)
  Kiln SDK (mod authors)          the Kiln SDK (Kiln.Sdk, Kiln.Api, Kiln.Templates @SDK_VERSION@), the `kiln` command
                                  line and the VS Code extension (@VSIX@)
                                    dotnet new install "<the unzipped folder>/Kiln.Templates.@SDK_VERSION@.nupkg"
                                    dotnet new kiln-mod -n MyMod


New versions
------------
This build of the launcher does not update itself and never checks for updates. New versions are on the Kiln page
on Nexus Mods, Files tab: https://www.nexusmods.com/enshrouded/mods/301 (Settings and About in the launcher link
there). To update, download the new version and replace the AppImage, install the new .deb, or run the new setup
program over the old install. Your mods, profiles and settings stay.


Checking the downloads
----------------------
SHA256 of the launcher files:
@NEXUS_SUMS@

  Linux:    sha256sum @APPIMAGE@
  Windows:  Get-FileHash .\@SETUP@ -Algorithm SHA256
