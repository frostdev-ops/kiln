# EMF v27 protocol implementation

These eight C++ files come from `src/core/net` in
[matenm/emf-enshrouded v0.6.0](https://github.com/matenm/emf-enshrouded/tree/v0.6.0/src/core/net), under the MIT license
in `licenses/EMF-LICENSE.txt`. The public C header is in `../../include/emf/emf_api.h`.

Kiln's changes are the attribution comments, an optional 30-second fragment expiry clock, rejection of fragments
whose type/version/encoding changes mid-message, bounded remembered rejection diagnostics, and a connection check
immediately before a queued remote message is delivered. Those changes do not alter the wire format. The game-facing dependencies are in `../api_net.cpp` and
`../api_net_client.cpp`.
