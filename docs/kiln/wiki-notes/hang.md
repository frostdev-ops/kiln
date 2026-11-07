# Wiki note: "the game hangs on a white window" / Kiln not loading (wv-hang, 2026-11-07)

What changed for players (launcher, desktop and Deck UI):
- When the game is running but Kiln didn't load into it, the launcher now says **No Kiln** (amber) instead of
  "Starting / Waiting for Kiln" forever. That happens when a non-vanilla one-off start (Without mods, Another profile)
  leaves its flag unread for 30 s after the game started, or when a non-vanilla launcher start has no Kiln boot a minute
  into the process's run. A missing, invalid or future process timestamp uses when the launcher first saw it running.
  A fresh boot record takes precedence over a leftover flag. Vanilla starts remembered by this launcher show **Vanilla**.
- The Play page then shows **Kiln is not loading** with the Steam launch option to copy when the override is absent.
  If the launch option is already set (or unnecessary), it asks for `kiln/logs/proxy.log` instead. On the Deck, Home shows a **Kiln is not loading** button that opens
  Help, which has a new **Kiln not loading?** block.

Suggested text for the guide's Troubleshooting page:

> **The launcher says "No Kiln", or a one-off start seems stuck.** The game started but Kiln didn't load into it.
> Close the game. In Steam, open Enshrouded's Properties and set the Launch Options to
> `WINEDLLOVERRIDES="dinput8=n,b" %command%`, then start again from the launcher.
> If it still says No Kiln, send us these files from the game folder: `kiln/logs/proxy.log` (if it's missing, that
> tells us Steam never loaded Kiln) and `kiln/logs/kiln.log`, plus `~/.local/share/kiln/logs/launcher.log`, your
> Proton version, and whether you were in Game Mode or Desktop Mode.
