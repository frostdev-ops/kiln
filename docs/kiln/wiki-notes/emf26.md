# Wiki note: EMF mods on Kiln now speak EmfApi v26 (EMF 0.5.1)

> Archived release note: dev.20 supports EMF 0.6.0 / API v27. See the current [EMF guide](../emf.md) and
> [parity report](../emf-parity.md) for supported functions, evidence and remaining limits.

What changed for the public guide (EMF page):

- Kiln runs mods built for **EMF 0.3.3 up to 0.5.1** (EmfApi v26). Before, every mod built with EMF 0.5's SDK was
  refused, because the SDK checks the table size.
- New in v22-v26 and what Kiln does with it:
  - player level and XP (v22): works on servers and hosted worlds;
  - add / remove components (v23): ScaleComponent and InteractionPause, as in EMF;
  - mod windows and pictures (v24, v26): in the game client; a dedicated server refuses them (as EMF does);
  - "E" prompts on entities (v25): work for every player, the host included.
- Still missing: the radial wheel (`wheel_open`, the wheel mods' menus), program hooks, writes to other entities from
  a mod program, and stamina/mana attributes.
- EMF's content mods (`emf_content.dll`) are not part of kiln-emf. They belong to Kiln's EML plugin.

Suggested text:

> Kiln loads EMF mods without EMF. Put them in `emf/mods/` as usual. Mods for EMF 0.3.3 to 0.5.1 work, including
> the new level, component, window and E-prompt functions. The radial wheel is not there yet; mods that use it run,
> but their wheel menus don't open. See *EMF API parity* for every function.
