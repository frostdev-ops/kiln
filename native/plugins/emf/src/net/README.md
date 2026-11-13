# EMF v27 protocol implementation and the mod check on join

These C++ files come from `src/core/net` and `src/core` in
[matenm/emf-enshrouded v0.6.8](https://github.com/matenm/emf-enshrouded/tree/v0.6.8/src/core), under the MIT license
in `licenses/EMF-LICENSE.txt`. The public C header is in `../../include/emf/emf_api.h`.

- `net_line`, `net_link`, `net_peers`, `net_service` (`src/core/net`): mod messages over the game's chat. Kiln's changes
  are the attribution comments, an optional 30-second fragment expiry clock, rejection of fragments whose
  type/version/encoding changes mid-message, bounded remembered rejection diagnostics, and a connection check
  immediately before a queued remote message is delivered. Those changes do not alter the wire format. They were
  carried from the v0.6.0 copy by a three-way merge (`git merge-file` with upstream v0.6.0 as the base); do the same
  for the next upstream version.
- `mods_check.h`, `mods_check_service.h`, `mods_policy.h`, `mod_registry.h` (`src/core`): the mod check on join (the
  core channel `emf/mods`). Unchanged apart from the attribution comments and the include paths.

The game-facing dependencies are in `../api_net.cpp` and `../api_net_client.cpp`.
