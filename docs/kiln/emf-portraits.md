# Creature pictures for EMF mods

Kiln prepares EMF's `portraits` manifest entries when the enabled mods load. Its separate GPL `kiln-eml`
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

Only missing or changed pictures are redrawn. An index records their model, texture and record inputs. When the
manifests, game files and renderer are unchanged and every picture is still in the cache, Kiln does not call the
renderer at all. Otherwise drawing runs on its own thread, so the game's start does not wait for it. A mod that calls
`image_load("portrait:<key>")` while its DLL loads waits until the pictures are drawn (at most 120 s for the whole
start). Any other thread (a tick, a window callback) gets 0 at once while that picture is being drawn: ask again a
moment later. A failed picture is remembered until the manifest, game archive or renderer changes. A full cache (over
128 folders or 512 MiB) or an entry the renderer refuses is remembered while the folders in `emf/cache/portraits`
stay as they are: the log says so once; delete unused folders there and the next start draws again. Cache
contention (another generator holds the cache) is retried at the next start. Old cached pictures remain usable when the renderer is unavailable. There are at most 32 pictures and
32 Mi output pixels per start, 128 cached portrait folders and a 512 MiB generation budget. Symlinks and reparse points in cache paths are refused; whole-folder replacement
keeps the previous drawing when a write fails.

The renderer comes from [EMF 0.6.0](https://github.com/matenm/emf-enshrouded/tree/v0.6.0/src/content/src/portraits).
It stays in `gpl/kiln-eml`; its copyright, license and source pin are in that component's notice. Mod packages and
Kiln releases contain no pictures extracted from the game. The offline tool `kiln-portraits request.json` and its
tests are described in `gpl/kiln-eml/README.md`.

Read-only archive checks through the actual Windows DLL drew textured wolf frames and reused the cache on Wine C:
and mounted Z: paths. Only synthetic nonredirecting Wine volume ancestors are accepted; symbolic links, junctions and
redirecting parents are refused, and cache paths must remain regular. These checks observed no external mutation.
Dedicated-server archives omit render models, including creature-pack models. Automatic startup and drawing custom-pack
client pictures in Beastmaster's window still need the corresponding client archive and an in-game check.
