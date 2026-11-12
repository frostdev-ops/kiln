// SPDX-License-Identifier: MIT
// kiln.playeranim: mods' states in the player's animation graph, merged at boot (native/core/abi/services/kiln_playeranim.h).
// The portable part (typedb, blob, states, merge) is built and tested on the host by tests/run.sh. The service around it
// is not written yet: until it is, this DLL publishes no table and tells the core it is not used here, so a build that
// ships it changes nothing in the game.
#include "kiln_api.h"
#include "merge.h"
#include "services/kiln_playeranim.h"

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    (void)owner;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    info->id = KilnStr{KILN_PLAYERANIM_SERVICE, sizeof(KILN_PLAYERANIM_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    return KILN_ERR_UNAVAILABLE;
}
