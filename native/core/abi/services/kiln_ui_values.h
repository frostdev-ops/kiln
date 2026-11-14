/* SPDX-License-Identifier: MIT
 * kiln.ui.values v1 (in-core service, client only): values in the game's own UI. A client mod replaces what the game
 * draws for an item (the number and bar of the item detail view, the bar on an inventory slot, the held item) and the
 * local player's HUD pairs (health, stamina, mana); it adds text for an item to a Kiln-owned details window; it reads
 * the inventory slots the game drew. Picture only: the server keeps the real values, nothing here writes gameplay
 * state. A player without the mod, and every vanilla client, sees the game unchanged. Chapter 05a of the dev.28
 * dossier is the design; docs/kiln/ui.md the author's page.
 *
 * Capabilities (kiln.caps rows; sigdb features of sigdb/symbols/client.d/dev28-05a-value-providers.json, each on or
 * off by itself, each client-gated until the owner's client pass): ui.values.detail (KUI_DETAIL), ui.values.slot
 * (KUI_SLOT), ui.values.held (KUI_HELD), ui.values.hud (KUI_HEALTH, KUI_STAMINA, KUI_MANA), ui.values.tooltip_lines
 * (tooltip_lines, open/close_tooltip_details), ui.slots.inspect (copy_slots, subscribe_slots). A kind's game hooks are
 * installed when the first provider of that kind registers and its row is available; one kind failing leaves the
 * others working, and the game's own value is drawn whenever no provider changes it.
 *
 * Results. A call that cannot be processed returns a KilnResult: KILN_ERR_INVALID_ARGUMENT (bad argument or record
 * size), KILN_ERR_UNAVAILABLE (the capability is off, or not the client), KILN_ERR_REVOKED (the owner is retired),
 * KILN_ERR_WRONG_THREAD. A processed registration returns KILN_OK with its result record: ok = 0 and a
 * KilnUiValueReason when it was refused (KUI_REGISTRATION_BUDGET: 128 active registrations per stable mod id, value,
 * line, slot and change subscriptions together). Handles are owner-scoped; unsubscribe takes any of them.
 *
 * Providers (KilnUiValueFn). Called in accepted registration order with a read-only accumulated input (the game's
 * value after every earlier provider's accepted edit) and a zeroed, sized edit. KILN_OK with flags 0 passes through;
 * KILN_OK with a valid mask commits exactly those fields, the others keep the accumulated value. Any other result
 * refuses the whole edit: the earlier providers' output stays and edit.reason says why (a reason outside the enum
 * becomes KUI_REJECTED). A refusal is not a fault. A fault is a structured exception in the callback, an invalid edit,
 * or KILN_ERR_INTERNAL returned (what a managed bridge returns for an exception it caught); five consecutive faults
 * remove the registration. An accepted edit or a pass-through resets the count.
 *   Masks: KUI_DETAIL FRACTION|CURRENT|MAXIMUM|LABEL|GAME_LABEL; KUI_SLOT FRACTION|CURRENT|MAXIMUM; KUI_HELD and the
 *   HUD kinds CURRENT|MAXIMUM. LABEL and GAME_LABEL exclude each other. Invalid (KUI_INVALID_EDIT, nothing written):
 *   an unknown or not allowed flag, a fraction that is not finite or outside [0, 1], a text id this owner did not
 *   intern (other than the input's own label_id), a detail CURRENT or MAXIMUM above 2147483647, a held/HUD MAXIMUM of
 *   0. LABEL with id 0 clears the label, with an interned id replaces it; GAME_LABEL restores the game's own label.
 *   Input label_is_override = 0 means the game's own label.
 *   Units: detail water = charges; slot water and held water = raw fuel units; durability = raw durability; HUD = the
 *   vital's game units. KilnUiItemView.fuel / fuel_max are always raw fuel units (units_per_charge is the divisor the
 *   detail view uses, at least 1). The fraction is dimensionless and writable for detail and slot only; for held and
 *   HUD it is current / maximum, read-only. A slot whose fuel is 0/0 draws full and stays full on a pass-through.
 *   Item filter: 0 = any nonempty item (detail, slot, held); otherwise one item id. HUD kinds take 0 only.
 *   No callback runs for an absent local player, nor for a held or HUD value whose game maximum is 0.
 *
 * Threads and lifetimes. Detail, slot and line callbacks run on the client's main thread inside the game's UI frame
 * (context.frame is that frame); held and HUD callbacks right after the game's player-UI update on the same thread,
 * before the next UI frame (context.frame is the last completed frame, client_step the client world's step). No
 * context, view, writer or presentation pointer may be kept after the callback returns. Never call server-world APIs,
 * block or wait in a callback: read a value cached on the client tick.
 *
 * Text. context.text->intern copies UTF-8 (no NUL, at most 4096 bytes) into a process-lifetime store and returns an
 * opaque id (0 = the empty text; never a pointer). Bytes are never freed while the process runs: not at frame end,
 * provider removal, reload or owner revoke. Limits: 64 KiB interned per callback, 8 MiB per stable mod id, 64 MiB and
 * 65,536 strings in all; a string's charge is its bytes + 1 + 64, plus 16 per mod holding it (every mod holding a
 * string pays its full charge); 64 new strings a second per mod, burst 128. Over a limit intern returns
 * KILN_ERR_BUFFER_TOO_SMALL and the callback is refused with KUI_TEXT_BUDGET whatever it returns; invalid UTF-8 or a
 * NUL is KILN_ERR_INVALID_ARGUMENT. The store deduplicates exact bytes. Use the numeric fields for numbers that change,
 * not a new label every frame. resolve returns this owner's text or the input's label; both only inside the callback
 * the context was given to.
 *
 * Every reader checks header.size (table) and the size field of each record. The table is append-only. */
#ifndef KILN_UI_VALUES_H
#define KILN_UI_VALUES_H
#include "../kiln_api.h"
#include "kiln_ui.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_UI_VALUES_SERVICE "kiln.ui.values"
#define KILN_UI_VALUES_VERSION 1u

enum KilnUiValueKind { KUI_DETAIL = 0, KUI_SLOT = 1, KUI_HELD = 2, KUI_HEALTH = 3, KUI_STAMINA = 4, KUI_MANA = 5 };
/* KilnUiItemView.flags: which identity fields are valid. Missing identity fields are 0 with their flag clear. */
enum KilnUiViewFlags {
    KUI_CLIENT_ENTITY = 1,  /* client_entity is the stack's client-world entity (its pide) */
    KUI_SERVER_ENTITY = 2,  /* server_entity is proven (never a numeric guess) */
    KUI_INVENTORY_SLOT = 4, /* inventory_page / slot_index name a real slot (a stack without a pide can have it) */
    KUI_DETAIL_COPY = 8,    /* the item detail view's own copied record, read-only, identity optional */
    KUI_SYNTHETIC = 16,     /* a detail record the game built for an item id alone: no slot, count or entity */
};
enum KilnUiEditFlags { KUI_FRACTION = 1, KUI_CURRENT = 2, KUI_MAXIMUM = 4, KUI_LABEL = 8, KUI_GAME_LABEL = 16 };
enum KilnUiValueReason {
    KUI_REASON_NONE = 0,
    KUI_REJECTED = 1,
    KUI_TEXT_BUDGET = 2,
    KUI_INVALID_EDIT = 3,
    KUI_CALLBACK_FAULT = 4,
    KUI_NO_FRAME = 5,
    KUI_STALE_FRAME = 6,
    KUI_BUFFER_SMALL = 7,
    KUI_REGISTRATION_BUDGET = 8,
    KUI_FRAME_BUDGET = 9,
    KUI_NO_TOOLTIP = 10,
};
typedef uint64_t KilnUiTextId; /* 0 = canonical empty text; never a pointer */

/* What the game drew the value for; a copy, valid for the callback. */
typedef struct KilnUiItemView {
    uint32_t size, flags, local_player, client_entity, server_entity, item;
    uint32_t inventory_page, slot_index, count, durability, durability_max;
    uint32_t fuel, fuel_max, units_per_charge, level, rarity;
    KilnUiScreen screen; /* kiln.ui's last completed screen (size 0 when kiln.ui is not there) */
    uint64_t session, frame;
} KilnUiItemView;

/* The accumulated input of a provider. */
typedef struct KilnUiBarValue {
    uint32_t size, kind;
    float fraction;
    uint32_t current, maximum;
    uint32_t label_is_override; /* 0 = the game's own label; 1 = label_id (0 = cleared) */
    KilnUiTextId label_id;
} KilnUiBarValue;

/* A provider's edit: flags (KilnUiEditFlags) say which fields to commit; reason (KilnUiValueReason) with a refusal. */
typedef struct KilnUiBarEdit {
    uint32_t size, flags;
    float fraction;
    uint32_t current, maximum;
    uint32_t reason;
    KilnUiTextId label_id;
} KilnUiBarEdit;

struct KilnUiProviderContext;
typedef struct KilnUiTextOps {
    uint32_t size, version;
    KilnResult(KILN_CALL* intern)(const struct KilnUiProviderContext*, KilnStr, KilnUiTextId*);
    KilnResult(KILN_CALL* resolve)(const struct KilnUiProviderContext*, KilnUiTextId, KilnStr*);
} KilnUiTextOps;
typedef struct KilnUiProviderContext {
    uint32_t size, kind;
    uint64_t session, frame, client_step;
    KilnOwner owner;
    const KilnUiTextOps* text;
    void* reserved; /* the service's; do not touch */
} KilnUiProviderContext;

/* Tooltip lines: only writer.append may add one (it checks the token and the room left and fills a sized line with
 * flags 0); status is a KilnUiValueReason that a failed append latches. At most 16 entries per detail occurrence, all
 * providers together. They feed Kiln's details window, never the game's tooltip. */
typedef struct KilnUiLine {
    uint32_t size, flags;
    KilnUiTextId text;
} KilnUiLine;
typedef struct KilnUiLineWriter {
    uint32_t size, capacity, count, status;
    KilnUiLine lines[16];
    void* reserved;
    KilnResult(KILN_CALL* append)(struct KilnUiLineWriter*, KilnUiTextId);
} KilnUiLineWriter;

typedef KilnResult(KILN_CALL* KilnUiValueFn)(const KilnUiProviderContext*, const KilnUiItemView*,
                                             const KilnUiBarValue*, KilnUiBarEdit*, void* user);
typedef KilnResult(KILN_CALL* KilnUiLinesFn)(const KilnUiProviderContext*, const KilnUiItemView*, KilnUiLineWriter*,
                                             void* user);
typedef struct KilnUiRegistration {
    uint32_t size, ok, reason, reserved;
    KilnHandle handle;
} KilnUiRegistration;

typedef struct KilnUiTooltipOpen {
    uint32_t size, ok, reason, reserved;
    uint64_t session, source_frame;
} KilnUiTooltipOpen;
/* A detail snapshot of the tooltip lines (tooltip_details): the item, where it was collected, and up to 16 lines. The
 * line views point at the text store's bytes, valid for the process (never freed). */
typedef struct KilnUiTooltipDetails {
    uint32_t size, ok, reason, count;
    uint64_t session, source_frame, capture_ms;
    uint32_t item, open; /* open: the details window is open (the snapshot of `which` 0 is its frozen one) */
    uint32_t inventory_page, slot_index, client_entity, view_flags;
    KilnStr lines[16];
} KilnUiTooltipDetails;
typedef struct KilnUiRect {
    float x, y, width, height;
} KilnUiRect;
enum KilnUiSlotAvailability {
    KUI_OUTER_RECT = 1,
    KUI_INNER_RECT = 2,
    KUI_NATIVE_STATE = 4,
    KUI_VIEWPORT = 8,
    KUI_PIXEL_MAPPING_VERIFIED = 16,
};
/* KilnUiVisibleSlot.holder_flags */
enum KilnUiHolderFlags {
    KUI_HOLDER_KNOWN = 1,          /* holder / holder_serial name the inventory's holder (from the game's UI models) */
    KUI_HOLDER_LOCAL_PLAYER = 2,   /* the holder is kiln.client's local player */
    KUI_HOLDER_ORDINAL = 4,        /* page_ordinal is set */
    KUI_HOLDER_SERVER_PROVEN = 8,  /* the holder is proven to be the server's entity of that id: never set in v1 */
};
/* One slot the game drew in a completed UI frame (a copy). occurrence is the draw order, not an identity; a page/index
 * can appear twice in one frame (two screen regions). outer_ui / inner_ui are UI units, valid only with their
 * KUI_OUTER_RECT / KUI_INNER_RECT bit (zero, negative or non-finite geometry clears it). native_state is the slot
 * record's 16 state bytes; state[0] is only the candidate hover byte until check 05a-c5 passed. A missing identity
 * leaves usable geometry valid. */
typedef struct KilnUiVisibleSlot {
    uint32_t size, available, occurrence, inventory_page, slot_index;
    uint32_t item, count, client_entity, server_entity, view_flags;
    KilnUiRect outer_ui, inner_ui;
    uint8_t native_state[16];
    /* the holder (since the first release; for slot marks, 13d-shroudlink): the client-world entity whose inventory
     * model holds the page (the model's owner), Kiln's lifetime serial of that holder in this process (a new one when
     * the holder first shows in a client session or shows again after a frame without it: a destroyed and recreated
     * holder never keeps its serial; never reused), and the page's position among the holder's pages in the game's
     * model order (not proven to be the saved inventory's page ordinal). Server identity is not proven in v1. */
    uint32_t holder, holder_flags;
    uint64_t holder_serial;
    uint32_t page_ordinal, reserved2;
} KilnUiVisibleSlot;
typedef struct KilnUiSlotsFrame {
    uint32_t size, ok, reason, count, local_player, reserved;
    KilnUiScreen screen;
    uint64_t session, frame, capture_ms;
    float canvas_width, canvas_height;
    KilnUiRect viewport_px;
    float pixels_per_unit;
    uint32_t transform_flags;
} KilnUiSlotsFrame;
typedef void(KILN_CALL* KilnUiSlotsFn)(const KilnUiSlotsFrame*, const KilnUiVisibleSlot*, uint32_t count, void* user);

enum KilnUiChangeKind { KUI_ADDED = 0, KUI_UPDATED = 1, KUI_REMOVED = 2, KUI_EVICTED = 3, KUI_RESET = 4 };
enum KilnUiResetReason { KUI_RESET_WORLD = 0, KUI_RESET_CAPABILITY = 1, KUI_RESET_REGISTRY = 2 };
typedef struct KilnUiValueIdentity {
    uint32_t size, kind, local_player, ui_state, hud_screen, crafting, station;
    uint32_t inventory_page, slot_index, client_entity, item, synthetic_group;
    uint64_t session;
} KilnUiValueIdentity;
typedef struct KilnUiValueChange {
    uint32_t size, change_kind, has_before, has_after, reason, reserved;
    uint64_t frame, old_session, new_session;
    KilnUiValueIdentity identity;
    KilnUiBarValue before, after;
    KilnStr before_label, after_label;
} KilnUiValueChange;
typedef void(KILN_CALL* KilnUiValueChangedFn)(const KilnUiValueChange*, void* user);

typedef struct KilnUiValuesApi {
    KilnServiceHeader header; /* { sizeof(KilnUiValuesApi), KILN_UI_VALUES_VERSION }; append-only */
    /* A value provider of `kind` (KilnUiValueKind) for one item id or 0. Any thread. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner, uint32_t kind, uint32_t item_filter, KilnUiValueFn, void* user,
                                     KilnUiRegistration*);
    /* A tooltip-lines provider (Kiln's details window). Any thread. */
    KilnResult(KILN_CALL* tooltip_lines)(KilnOwner, uint32_t item_filter, KilnUiLinesFn, void* user,
                                         KilnUiRegistration*);
    /* Removes any registration of this service (KILN_ERR_NOT_FOUND when unknown or already gone). Any thread; a
     * provider removed from inside a callback is not called again. */
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle);
    /* One completed frame of drawn slots (ui.slots.inspect): (0, 0) = the latest, else exactly one of the last two
     * retained frames. Any thread. slots[0].size (when capacity > 0) is the caller's record size (at least the 88
     * bytes up to native_state). KILN_OK with frame->ok = 0: KUI_NO_FRAME (none yet), KUI_STALE_FRAME (the ids are not
     * retained: start again from the latest), KUI_FRAME_BUDGET (more than 4096 slot draws in that frame: no partial
     * list). capacity below the count: KILN_ERR_BUFFER_TOO_SMALL with the whole frame metadata and *required, no
     * record written; retry with the same ids. A copy freezes one retained frame: metadata and records belong together.
     * capture_ms is GetTickCount64 at publication. Pixel rectangles need KUI_VIEWPORT and KUI_PIXEL_MAPPING_VERIFIED
     * in transform_flags; the latter waits for check 05a-c5 (never set in v1). */
    KilnResult(KILN_CALL* copy_slots)(KilnOwner, uint64_t expected_session, uint64_t expected_frame,
                                      KilnUiSlotsFrame*, KilnUiVisibleSlot*, uint32_t capacity, uint32_t* required);
    /* fn(frame, slots, count) once per completed UI frame, on the UI thread after the frame; borrowed for the call. */
    KilnResult(KILN_CALL* subscribe_slots)(KilnOwner, KilnUiSlotsFn, void* user, KilnUiRegistration*);
    KilnResult(KILN_CALL* subscribe_changes)(KilnOwner, KilnUiValueChangedFn, void* user, KilnUiRegistration*);
    /* Opens the shared details window on the last completed detail snapshot (frozen until refresh or close): KILN_OK
     * with ok = 0 and KUI_NO_TOOLTIP when there is none in this client session. Idempotent. close: idempotent too.
     * A world/session change, a provider registry change or an unload closes it. Any thread. */
    KilnResult(KILN_CALL* open_tooltip_details)(KilnOwner, KilnUiTooltipOpen*);
    KilnResult(KILN_CALL* close_tooltip_details)(KilnOwner);
    /* ---- appended in the first release, read header.size: what Kiln's details window shows ----
     * tooltip_details: which 0 = the window's frozen snapshot (ok 0, KUI_NO_TOOLTIP while it is closed), 1 = the last
     * completed one. refresh_tooltip_details: the window's snapshot becomes the last completed one when that is of the
     * same presentation identity (else ok 0, KUI_STALE_FRAME). Any thread. */
    KilnResult(KILN_CALL* tooltip_details)(KilnOwner, uint32_t which, KilnUiTooltipDetails*);
    KilnResult(KILN_CALL* refresh_tooltip_details)(KilnOwner, KilnUiTooltipOpen*);
} KilnUiValuesApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_UI_VALUES_H */
