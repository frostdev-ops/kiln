Kiln @VERSION@
@RULE@

Kiln is a mod framework and launcher for Enshrouded: C# and native mods, mod profiles, local dedicated servers and
their administration. Built @DATE@ from @SOURCE@ (@CHANNEL@ channel). Kiln is MIT licensed (LICENSE);
third-party licences are in THIRD-PARTY-NOTICES.txt; CREDITS.md says whose work Kiln builds on.

Enshrouded is a trademark of Keen Games GmbH. Kiln is not affiliated with or endorsed by Keen Games.


Which file do I want?
---------------------
Most players want the Kiln Launcher. It installs Kiln into the game, manages mods and profiles, and updates itself.

  Linux    linux/@APPIMAGE@
           One file, no install: make it executable and run it. It updates itself in place.
             chmod +x @APPIMAGE@ && ./@APPIMAGE@
           Or the package: linux/@DEB@ (Debian, Ubuntu)@RPM_LINE@
           A package install is updated by downloading the next package, not by the launcher.
           Linux x86_64: glibc 2.35+ and desktop libraries (Ubuntu 22.04 baseline; SteamOS/Steam Deck).

  Windows  windows/@SETUP@
           Installs for your user (no administrator rights). It updates itself.
@SMARTSCREEN@

Then open the launcher, let it find Enshrouded in your Steam libraries, and press Install on Play. The launcher
carries the matching Kiln runtime (@RUNTIME_VERSION@).

Server owners and tinkerers:
  runtime/kiln-server-@VERSION@.zip    the server pack: unzip it into a dedicated server's folder (also over FTP on a
                                       rented server); KILN-SERVER-README.txt inside
  runtime/kiln-runtime-@VERSION@.zip   the Kiln runtime alone, for a dedicated server or a manual install
  runtime/kilnctl, runtime/kilnctl.exe  the command line that installs it (runtime/INSTALL-RUNTIME.txt)
  runtime/*.service, runtime/*.timer   systemd user units for a kilnctl server on Linux

Mod authors:
  sdk/*.nupkg                    the Kiln SDK (Kiln.Sdk, Kiln.Api, Kiln.Templates @SDK_VERSION@) for `dotnet new kiln-mod`
  sdk/kiln, sdk/kiln.exe         the `kiln` command line (new, build, run, deploy, lint, pack, ...)
  sdk/@VSIX@   the VS Code extension (Extensions: Install from VSIX...)
  samples/*.zip                  sample mods; import a zip in the launcher's Library
  source/kiln-@VERSION@-src.tar.gz   the source of this release

  Use the SDK from a folder:  dotnet new install Kiln.Templates --add-source <this folder>/sdk
                              dotnet new kiln-mod -n MyMod


Checking the downloads
----------------------
SHA256SUMS lists every file of this release:
  Linux:    sha256sum -c SHA256SUMS
  Windows:  Get-FileHash .\windows\@SETUP@ -Algorithm SHA256   (compare with its line in SHA256SUMS)

The launcher's own updates are signed (minisign, Ed25519); the launcher refuses any update whose signature does not
match its built-in key. The .sig files next to the AppImage and the setup program are those signatures.

Updates: the launcher checks frostdev.io (GitHub Releases if that fails) at start and every 6 hours. A check
downloads one small file; nothing about you or your PC is sent. Settings > Updates turns it off or picks the
channel (stable or dev).


Files
-----
@FILES@
