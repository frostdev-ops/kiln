# Wiki note: ShroudForge mods (agent sf, 2026-11-07)

What changed: Kiln runs ShroudForge (SF) Lua mods unchanged. Kiln ships SF's runtime, built from its official fork
frostdev-ops/shroudforge (a separate GPL-3 component, `kiln/plugins/shroudforge/`). The MIT plugin `kiln-shroudforge`
drives it, or the player's installed SF when "Use my installed ShroudForge" is on. Installing Kiln must no longer
displace ShroudForge (the installer change is in progress with agent detect).

Suggested guide text (section "Other mod loaders → ShroudForge"):

> **ShroudForge mods.** Kiln runs ShroudForge mods as they are and ships ShroudForge's runtime, so you need nothing
> else. Already have ShroudForge? Keep it: launcher Settings, "Use my installed ShroudForge", runs your copy. Kiln replaces only its
> `dinput8.dll` and leaves `dbghelp.dll`, `winmm.dll`, the `shroudforge` folder and your `mods` folder alone. Switch
> mods on and change their settings in ShroudForge's Modloader window or in the Kiln Launcher. On the Steam Deck
> you need no extra launch options. Kiln's log shows ShroudForge's lines as `[sf]`, and `kiln/state/shroudforge.json`
> says whether ShroudForge ran (`hosted`, `self-hosted`, `off`, `not-installed`, `failed` with a reason).
> ShroudForge mods also run on dedicated servers with Kiln.

Launcher texts to replace (once the installer keeps ShroudForge):
- RuntimePage: "ShroudForge is in this game. Kiln replaces it." → "ShroudForge is in this game. Kiln installs next to
  it, and your ShroudForge mods keep working."
- PlayPage: "ShroudForge is in this game. Kiln replaces it on Runtime." → "ShroudForge is in this game. Install Kiln
  next to it on Runtime."
- Deck Home/Kiln: "Switch in the desktop UI" → "Install Kiln next to ShroudForge".
- The "Switch from ShroudForge?" confirm ("ShroudForge mods stop loading") becomes the non-default advanced option:
  "Remove ShroudForge's loader files (your ShroudForge mods stop loading)".

Library skip messages (from `kiln_manage::shroudforge::skip_reason`):
- "<name> <version> is a ShroudForge Lua mod: it runs through ShroudForge, which Kiln hosts when ShroudForge is installed
  in the game folder"
- "has a mod.json that is neither a ShroudForge (Lua) nor an EMF mod manifest"
- "has Lua files but no mod.json: not a mod package (ShroudForge mods need mod.json with id, name and version)"
