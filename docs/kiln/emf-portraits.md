# Creature pictures for EMF mods

Kiln prepares EMF's `portraits` manifest entries before an enabled mod's DLL starts. Its separate GPL `kiln-eml`
component reads the player's game archives and draws PNG frames with a CPU renderer. It needs no game window or
GPU. The early startup asset writer has already finished, so installed creature packs are included. This step
reads game data; it writes only `emf/cache/portraits`.

```json
"portraits": [
  {"key": "my_mod.wolf", "template": "478381c5-585f-4264-b373-dbb1ea596eca", "frames": 24, "size": 192}
]
```

Use `emf.image("portrait:my_mod.wolf")` in Lua, or `image_load("portrait:my_mod.wolf")` in C. A window picture row
with `fps` turns through the frames. A `model` GUID can replace `template`. Frame counts are 1–64; sizes are 8–512.
Keys follow EMF's existing image key rules. The active profile and drop-in policy select the mods; switched-off or
rejected mods do not request pictures. A dedicated server prepares no pictures.
When ShroudForge starts through its own external bootstrap, automatic generation is skipped because that writer's
completion is outside Kiln's ready ordering. Existing cached pictures remain usable. Kiln-hosted ShroudForge and
kiln-eml finish their asset preparation before portrait generation.

Only missing or changed pictures are redrawn. An index records their model, texture and record inputs. A failed
picture is remembered until the manifest, game archive or renderer changes. Cache contention is retried at the
next start. Old cached pictures remain usable when the renderer is unavailable. There are at most 32 pictures and
32 Mi output pixels per start, 128 cached portrait folders and a 512 MiB generation budget. Symlinks and reparse points in cache paths are refused; whole-folder replacement
keeps the previous drawing when a write fails.

The renderer comes from [EMF 0.6.0](https://github.com/matenm/emf-enshrouded/tree/v0.6.0/src/content/src/portraits).
It stays in `gpl/kiln-eml`; its copyright, license and source pin are in that component's notice. Mod packages and
Kiln releases contain no pictures extracted from the game. The offline tool `kiln-portraits request.json` and its
tests are described in `gpl/kiln-eml/README.md`.

An offline archive check drew and inspected a textured wolf from the current client archive. Dedicated-server
archives omit render models, including creature-pack models. Drawing custom-pack client pictures and displaying
them in Beastmaster's window still need the corresponding client archive and an in-game check.
