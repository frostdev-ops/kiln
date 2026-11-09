// SPDX-License-Identifier: MIT
// kiln.items: the hooks on the game's quick stack and move all, and what runs in them (transfer rules, handlers,
// reports). Ported in behaviour from shroudlink's inventory events (MIT, bjck/shroudlink v0.8.0,
// native/plugins/shroudlink/upstream/server/src/events.rs: keep, on_transfer, hiding kept stacks from the game's
// transfer). Nothing is hooked until the first registration; hooks are never removed, so another loader's hook on the
// same functions (kiln-shroudlink's) is never undone and ours never has to be taken apart under it.
#pragma once
#include <cstdarg>

#include "kiln_api.h"
#include "services/kiln_items.h"

namespace kitems {

// Logging and the host API, from items.cpp.
void items_log(int level, const char* fmt, ...);
const KilnApi* items_api();
KilnOwner items_owner();  // the owner kiln.items registered under (its hook links)

// A kiln.items move (KILN_MOVE_QUICK_STACK / MOVE_ALL) is about to call the game's function on this thread: the hook
// reports it as BY_MOD, and with `silent` neither rules nor handlers see it. A dry run still evaluates rules and
// handlers but emits no report for the transaction that will be rolled back. xfer_mod_end clears the mark and says
// whether the hook canceled the call.
void xfer_mod_begin(bool silent, bool dry_run = false);
bool xfer_mod_end();

void xfer_init();                       // after resolve(): E.transfer_ok says whether hooks are possible at all
void xfer_set_step(uint64_t step);      // tick thread, Pre
KilnResult xfer_add_rule(KilnOwner owner, const KilnItemsTransferRule* rule, uint64_t* handle);
KilnResult xfer_add_handler(KilnOwner owner, KilnItemsTransferFn fn, void* user, uint64_t* handle);
KilnResult xfer_add_reporter(KilnOwner owner, KilnItemsTransferReportFn fn, void* user, uint64_t* handle);
// Invalidates captured callbacks too; drains already-running callbacks unless called reentrantly from a transfer
// callback (then only invalidates, to avoid deadlock). See kiln_items.h transfer_end for the userdata lifetime contract.
KilnResult xfer_end(uint64_t handle);
void xfer_end_owner(KilnOwner owner);
void xfer_deliver();                    // tick thread, Pre: queued reports to their listeners
void xfer_status(KilnItemsStatus2* st); // fills the transfer fields

}  // namespace kitems
