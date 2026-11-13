/* This file is EMF's public C API header, copied unchanged (below the line) from EMF (Enshrouded Modding Framework)
 * 0.6.8, sdk/include/emf/emf_api.h, for kiln-emf.dll (native/plugins/emf), which builds an EmfApi v40 table on Kiln's
 * services so unmodified EMF mods load on Kiln. Also in licenses/EMF-LICENSE.txt.
 *
 * MIT License
 *
 * Copyright (c) 2026 EMF contributors
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 * ------------------------------------------------------------------------------------------------------------------ */
/* EMF (Enshrouded Modding Framework) C API, version 40 (see EMF_API_VERSION).
 *
 * A mod is a DLL in <game>/emf/mods/ that exports:
 *     extern "C" __declspec(dllexport) bool emf_mod_load(const EmfApi* api);
 * Return false to report that the mod could not start. The DLL stays loaded either way and anything it
 * registered before returning stays active, so do your checks first. Keep the api pointer; it stays
 * valid for the whole process.
 *
 * Threading rule: ECS data (worlds, components, singletons) may only be touched inside the callbacks
 * registered with on_tick / run_on_world, on the world passed to the callback. They run on the thread
 * that owns that world, right after its simulation step, when no game jobs are running. spawn and
 * destroy enforce this and refuse (with a log line) anywhere else. Component pointers are valid only
 * until the callback returns.
 *
 * Compatibility: fields are only ever appended. Check api->size before using fields added later. Structs with a
 * size field: set it to sizeof(the struct); a newer core accepts the size from any older header (fields added after
 * it keep their defaults).
 */
#ifndef EMF_API_H
#define EMF_API_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define EMF_API_VERSION 40

/* World kinds (the byte at the entity storage + 8: world+0xE15DC8 on build 1273043, world+0xE179C8 on 1283531). */
#define EMF_WORLD_SERVER 0
#define EMF_WORLD_PREDICTION 1
#define EMF_WORLD_CLIENT 2

typedef struct EmfWorld EmfWorld; /* opaque: the game's ECS world object */
typedef void (*EmfWorldCallback)(EmfWorld* world, void* user);
typedef void (*EmfEntityCallback)(uint32_t entity, void* user);
/* world_slot_id: low byte = slot index + 1, high byte = storage (0 Local, 1 Steam) */
typedef void (*EmfSaveCallback)(uint16_t world_slot_id, void* user);
/* program_context = the ProgramContext* the game passes to a program update function */
typedef void (*EmfProgramCallback)(void* program_context, void* user);
/* event = one engine event struct (see the generated headers: timeStamp first, then its fields) */
typedef void (*EmfEventCallback)(const void* event, void* user);
/* v4 on_event: a copy of one engine event, delivered after the world's simulation step on its thread */
typedef void (*EmfWorldEventCallback)(EmfWorld* world, const void* event, uint32_t size, void* user);

/* v5 items */
typedef struct EmfItemInfo {
    uint32_t size;      /* set to sizeof(EmfItemInfo) before calling item_info */
    uint32_t item_id;   /* the game's ItemId = FNV-1a-32 over the 16 GUID bytes */
    uint8_t guid[16];   /* as stored in the game (Windows layout) */
    uint16_t max_stack;
    char name[96];      /* the game's debug name (e.g. "Torch"), or "" */
} EmfItemInfo;
typedef void (*EmfItemCallback)(const EmfItemInfo* item, void* user);
/* v6 commands: argv[0] is the command name; strings are valid during the call */
typedef void (*EmfCommandCallback)(EmfWorld* world, int argc, const char* const* argv, void* user);
/* v5 spawn_ex: one extra spawn component (types.json qualifiedHash, exact size, data copied by the game) */
typedef struct EmfComponentInit {
    uint32_t hash;
    uint32_t size;
    const void* data;
} EmfComponentInit;

/* v8 put_items: how a request ended (added = 0 when nothing fit or it was refused at run time) */
typedef void (*EmfPutItemsCallback)(EmfWorld* world, uint32_t entity, uint32_t item_id, uint32_t requested,
                                    uint32_t added, void* user);

/* v10 radial wheel ("emote wheel"): choice = index into the entries, -1 = cancelled */
typedef void (*EmfWheelCallback)(EmfWorld* world, int choice, void* user);
typedef struct EmfWheel {
    uint32_t size;            /* sizeof(EmfWheel) */
    const char* title;        /* shown in the middle, or NULL */
    const char* const* items; /* count labels (copied); "Name (details)" shows the details on a second line */
    uint32_t count;           /* 1..64; 8 per page, the mouse wheel turns the page */
    uint32_t hold_key;        /* a keyboard key's generic virtual-key code (VK_SHIFT, not VK_LSHIFT; no mouse
                                 buttons: refused) held while it is open: releasing it picks (in the centre: cancels);
                                 a quick tap (released within 300 ms, nothing highlighted) leaves it open for a click.
                                 0 (or not held when the wheel appears) = no key: a left click picks. A left click on
                                 a highlighted entry picks in either mode; Esc or a right click cancel. A hold key
                                 still down when the wheel closes reads as up to mods until it is released. */
    uint8_t world_kind;       /* cb runs after the next simulation step of a world of this kind (EMF_WORLD_SERVER or
                                 EMF_WORLD_CLIENT), on its thread */
    EmfWheelCallback cb;
    void* user;
} EmfWheel;

/* v11: one request for put_items_ex and take_items. Results come back like put_items': cb(world, entity, item,
 * requested, done, user) after the server world's next step, on its thread (done = items put / taken). */
#define EMF_ITEMS_EXACT_SLOT 0x01u /* take_items: take only from `slot`; done = 0 when it no longer holds the item */
#define EMF_ITEMS_RARITY 0x02u     /* put_items_ex: create the item with `rarity` (without it: the game's default) */
typedef struct EmfItemsRequest {
    uint32_t size;    /* sizeof(EmfItemsRequest) */
    uint32_t entity;  /* whose inventory (a player, villager, chest: an entity with an initialized InventorySetup) */
    uint32_t item;    /* item id (find_item) */
    uint32_t count;   /* 1..65535 */
    uint32_t level;   /* put_items_ex: the item level a gear item is created with; 0 = the game's default */
    uint8_t rarity;   /* put_items_ex with EMF_ITEMS_RARITY: 0 Common .. 4 Legendary */
    uint8_t flags;    /* EMF_ITEMS_*; other bits are refused */
    uint8_t pad[2];
    uint64_t slot;    /* take_items: the slot to take from first, an InventorySlotId (one of the entity's own 8-slot
                         inventory entities in the low 32 bits, the slot index 0..7 in bits 32..39; anything else is
                         refused); 0 = any (the held action-bar slot first). Without EMF_ITEMS_EXACT_SLOT the game
                         takes from its other slots (backpack, action bars, equipment) when this one has too few */
    EmfPutItemsCallback cb;  /* may be NULL */
    void* user;
} EmfItemsRequest;

/* v12 hud_set: where a line sits (the game's own HUD uses the top left, the top centre and the bottom centre; its quest
 * tracker sits in the top right, so EMF_HUD_TOP_RIGHT lines start a quarter down the screen, below it) */
#define EMF_HUD_TOP_RIGHT 0u
#define EMF_HUD_BOTTOM_RIGHT 1u
#define EMF_HUD_BOTTOM_LEFT 2u

/* v13 settings: a mod's settings live in <game>/emf/config/<mod>.ini, section [<mod>], and show in the Mods tab of the
 * in-game console. Declare them in the mod's mod.json ("settings") or with setting_declare. */
#define EMF_SETTING_BOOL 0u
#define EMF_SETTING_INT 1u
#define EMF_SETTING_FLOAT 2u
#define EMF_SETTING_CHOICE 3u /* an index into `choices` */
#define EMF_SETTING_KEY 4u    /* a key name as emf/keys.hpp reads it ("G", "F2", "Space", "0xba") */
#define EMF_SETTING_TEXT 5u   /* up to 256 bytes, no line breaks */
#define EMF_SETTING_HAS_MIN 0x01u
#define EMF_SETTING_HAS_MAX 0x02u
#define EMF_SETTING_LIVE 0x04u /* the mod applies a change at once (on_setting_changed); else "applies at next start" */
typedef struct EmfSettingDef {
    uint32_t size;             /* sizeof(EmfSettingDef) */
    const char* key;           /* 1-48 of A-Z a-z 0-9 _ . -; "enabled" is reserved (the mod's on/off switch) */
    uint32_t type;             /* EMF_SETTING_* */
    const char* label;         /* shown in the Mods tab (NULL: the key) */
    const char* help;          /* a sentence under it, or NULL */
    const char* default_value; /* as text ("1", "2.5", "bottom right" or "1" for a choice, "G"), or NULL */
    double min, max;           /* with EMF_SETTING_HAS_MIN / _HAS_MAX (int and float) */
    uint32_t flags;            /* EMF_SETTING_HAS_MIN | _HAS_MAX | _LIVE */
    const char* choices;       /* EMF_SETTING_CHOICE: "top right|bottom right|bottom left" */
} EmfSettingDef;
/* world = the world after whose simulation step it runs (any kind), mod/key = what changed (key "enabled" = the switch) */
typedef void (*EmfSettingCallback)(EmfWorld* world, const char* mod, const char* key, void* user);

/* v14 patterns: engine code EMF has not named, found by bytes (the way EMF finds its own functions, so a game update
 * that keeps the bytes keeps your mod working) */
#define EMF_FOLLOW_NONE 0u
#define EMF_FOLLOW_REL32 1u /* the int32 at match+offset is relative to the end of its instruction: the result is
                               match + offset + 4 + rel32_tail + that int32 (a call/jmp target, RIP-relative data) */
typedef struct EmfPattern {
    uint32_t size;       /* sizeof(EmfPattern) */
    const char* bytes;   /* IDA style: "48 8B 05 ?? ?? ?? ?? E8"; it must match exactly once */
    const char* section; /* a section of the game exe: ".text" (NULL), ".rdata", ".data" */
    int32_t offset;      /* added to the match */
    uint32_t follow;     /* EMF_FOLLOW_* */
    int32_t rel32_tail;  /* EMF_FOLLOW_REL32: bytes after the int32 up to the end of the instruction (an immediate) */
} EmfPattern;
typedef struct EmfGameBuild {
    uint32_t size;        /* sizeof(EmfGameBuild) */
    uint32_t number;      /* the build number in the game's menu when EMF knows this exe, else 0 */
    char sha256[65];      /* the exe's sha256, lower-case hex */
    uint8_t known;        /* 1: EMF has this build's addresses (else it runs on signatures only) */
    uint8_t server;       /* 1: inside the dedicated server (enshrouded_server.exe) */
    uint8_t pad;
    uint64_t module_base; /* where the exe is mapped */
    uint32_t text_rva;    /* its .text section */
    uint32_t text_size;
} EmfGameBuild;

/* v15 command_sender: who typed the command that is running */
typedef struct EmfCommandSender {
    uint32_t size;      /* sizeof(EmfCommandSender) */
    uint32_t entity;    /* the player's entity in the server world (chat), 0 if unknown or not from chat */
    uint64_t account;   /* the player's account hash as the game knows it (chat), else 0 */
    uint8_t from_chat;  /* 1: a player typed it in the game's chat; 0: the console, emf/commands.txt, the overlay */
    uint8_t admin;      /* v17: 1 = the sender may run every command (the console, commands.txt, the host's own chat, an
                           account in [chat] admins or everyone=1); 0 = a player who may run only commands for players.
                           A core older than v17 leaves it 0: read it only when emf::has_v17() */
    uint8_t pad[6];
} EmfCommandSender;

/* v17 register_command_ex: a command with options */
#define EMF_COMMAND_PLAYERS 0x01u /* any player may run it from the game's chat, not only the [chat] admins (where players'
                                     chat commands are on: a dedicated server, or [chat] players=1 in a game you host).
                                     Use it for commands that only read, or act on the sender (command_sender) */
typedef struct EmfCommandDef {
    uint32_t size;            /* sizeof(EmfCommandDef) */
    uint32_t flags;           /* EMF_COMMAND_*; other bits are refused */
    const char* name;         /* as register_command */
    const char* help;
    EmfCommandCallback cb;
    void* user;
} EmfCommandDef;

/* v18 attribute_get / attribute_add: attribute ids as the game uses them (an attribute lives in the Health, Stamina or
 * Mana component of a creature or player in a world this game hosts) */
#define EMF_ATTR_HEALTH 0x8EB84995u     /* current health (Health) */
#define EMF_ATTR_MAX_HEALTH 0xDC8358BEu /* maximum health (Health), computed by the game: a change to it does not last */
#define EMF_ATTR_STAMINA 0x04B6AA8Bu    /* current stamina (Stamina) */
#define EMF_ATTR_MANA 0x6AA2574Eu       /* current mana (Mana) */
#define EMF_ATTR_MAX_STAMINA 0xF443C410u /* maximum stamina (Stamina), computed by the game */
#define EMF_ATTR_MAX_MANA 0x17536777u    /* maximum mana (Mana), computed by the game */
/* v22: a player's XP and level (the Experience and Level components). Give XP with attribute_add of a positive amount
 * of EMF_ATTR_XP_PENDING, as every XP source in the game does: on its next steps the game moves it into XP and levels
 * the player up (once per step while XP reaches the requirement, with the rest carried over, a LevelUpEvent and the
 * skill points that come with the level). Pending XP is thrown away at the level cap. The other three are read-only
 * (attribute_add refuses them): the game keeps them consistent. XP and levels are saved with the character. */
#define EMF_ATTR_XP_PENDING 0x1D7DD102u  /* XP given and not yet counted (Experience): the one XP attribute to add to */
#define EMF_ATTR_XP 0x8DCDDAF4u          /* XP towards the next level (Experience) */
#define EMF_ATTR_XP_REQUIRED 0x04723F62u /* XP the next level needs (Experience) */
#define EMF_ATTR_LEVEL 0x724BF568u       /* the level (Level) */

/* v19 on_chat: one chat line a player sent (not an EMF command). account = their account hash (as command_sender
 * reports it), entity = their player entity in the server world (0 if it could not be told), text = the line (UTF-8,
 * NUL-terminated, control characters already '?'; valid during the call). */
typedef void (*EmfChatCallback)(EmfWorld* world, uint64_t account, uint32_t entity, const char* text, void* user);
#define EMF_CHAT_MAX_LINES 10u    /* chat_tell / chat_broadcast: lines per call (more: one "..." line, the rest dropped) */
#define EMF_CHAT_LINE_BYTES 1024u /* the game's longest chat line; longer lines are cut */

/* v20 teleport (docs/engine_parts/re_teleport.md): moves an entity the way the game's own fast travel does */
#define EMF_TELEPORT_FIND_GROUND 0x01u  /* the game's ground search: the nearest walkable spot within about 10 m of the target
                                           (up or down); none found = the target as it is, without telling (see
                                           EMF_TELEPORT_RAW_TARGET). It waits for the destination to load (up to ~0.5 s) */
#define EMF_TELEPORT_FACE_YAW 0x02u     /* turn to yaw_deg (about the up axis); otherwise keep the current orientation */
#define EMF_TELEPORT_CHECKPOINT 0x04u   /* players: also a replication checkpoint, as fast travel sets one (live: no visible
                                           difference; meant for long moves) */
#define EMF_TELEPORT_NOTIFY_READY 0x08u /* a second callback (EMF_TELEPORT_READY) after a MOVED, once a player can play
                                           again (the game keeps a player "not ready" for 0.3-3 s after a teleport while it
                                           loads the destination; at most 10 s); for other entities right after the MOVED */
/* EmfTeleportResult.status */
#define EMF_TELEPORT_FAILED 0u /* not moved: refused when it was checked, or given up (reason says why) */
#define EMF_TELEPORT_MOVED 1u  /* moved: x/y/z is where the entity is now */
#define EMF_TELEPORT_READY 2u  /* with EMF_TELEPORT_NOTIFY_READY, after MOVED: the player is ready for gameplay again */
/* EmfTeleportResult.flags */
#define EMF_TELEPORT_OFF_TARGET 0x01u /* it ended more than 1 m from the target (with spawn_point: 25 m from the spawn point):
                                         the ground search moved it, or the game sent it elsewhere */
#define EMF_TELEPORT_RAW_TARGET 0x02u /* FIND_GROUND found no walkable spot near the target: the entity stands exactly at the
                                         target (in the air, or inside the terrain: the game frees a player stuck inside the
                                         terrain by sending it to its spawn point about 1.5 s later) */
#define EMF_TELEPORT_LAST 0x04u       /* the last callback of this request: free what `user` points to now */
#define EMF_TELEPORT_SESSION_ENDED 0x08u /* the session ended before the answer (a new world was set up, or the server world
                                            went away: back to the menu): FAILED (READY after a move) and LAST. It may come
                                            outside a world callback, on another thread: do not touch `world` or entities
                                            (their ids mean nothing now) and do not give anything back for it; only free
                                            your data */
typedef struct EmfTeleportResult {
    uint32_t size;      /* sizeof(EmfTeleportResult) of the core */
    uint32_t entity;
    uint32_t status;    /* EMF_TELEPORT_FAILED / _MOVED / _READY */
    uint32_t flags;     /* EMF_TELEPORT_OFF_TARGET | _RAW_TARGET | _LAST | _SESSION_ENDED */
    double x, y, z;     /* where the entity is now, in metres (FAILED: where it still is when known, else 0) */
    double distance;    /* metres between x/y/z and the target (MOVED, READY) */
    const char* reason; /* FAILED (and a READY that timed out): why, in words; else ""; valid during the call */
} EmfTeleportResult;
typedef void (*EmfTeleportCallback)(EmfWorld* world, const EmfTeleportResult* result, void* user);
typedef struct EmfTeleport {
    uint32_t size;        /* sizeof(EmfTeleport) */
    uint32_t entity;      /* a player, a villager, an animal or a creature: an entity with CurrentTransform, LastTransform
                             and TeleportCount (props, chests and altars have none: refused) */
    double x, y, z;       /* the target in metres, world space (y up); x and z 0..10240, y -1000..5000. Ignored with
                             spawn_point */
    float yaw_deg;        /* with EMF_TELEPORT_FACE_YAW */
    uint32_t spawn_point; /* optional: an entity with PlayerSpawnPoint (a flame altar, a spire, a start location): the game
                             lands the entity on one of its landing pads (pad (entity - 1) % 5), as when it respawns there */
    uint32_t flags;       /* EMF_TELEPORT_*; other bits are refused */
    uint32_t pad;
    EmfTeleportCallback cb; /* may be NULL */
    void* user;
} EmfTeleport;

/* v21 world settings (docs/modding/world_settings.md): the running world's ecs::GameSettings */
#define EMF_WS_PERSIST 0x1u /* world_setting_set / _reset: also keep it for this world (re-applied after each load;
                               a reset with it forgets the kept value) */
/* key = the setting's name as enshrouded_server.json has it ("experienceCombatFactor"), value as world_setting_get
 * gives it. Valid during the call. */
typedef void (*EmfWorldSettingCallback)(const char* key, double value, void* user);
/* EmfWorldSettingInfo.type */
#define EMF_WS_FLOAT 0u   /* a factor or amount */
#define EMF_WS_BOOL 1u    /* 0 or 1 */
#define EMF_WS_ENUM 2u    /* a choice by its number, 0 .. max */
#define EMF_WS_MINUTES 3u /* a duration, in minutes */
#define EMF_WS_WHOLE 4u   /* a whole number */
/* EmfWorldSettingInfo.flags */
#define EMF_WS_CHANGED 0x1u     /* value differs from the world's own (changed in this session or kept) */
#define EMF_WS_NEEDS_CLIENT 0x2u /* players' games read it too: they follow after the game's resync */
typedef struct EmfWorldSettingInfo {
    uint32_t size;     /* set it to sizeof(EmfWorldSettingInfo) */
    uint32_t type;     /* EMF_WS_FLOAT / _BOOL / _ENUM / _MINUTES / _WHOLE */
    const char* key;   /* as enshrouded_server.json names it; these strings stay valid */
    const char* alias; /* the short name, or "" */
    const char* group; /* "Player", "Survival", "Enemies", "Rates", "Time", "World" or "Other" */
    double min, max;   /* what world_setting_set accepts */
    double value;      /* now */
    double world;      /* the world's own (as it loaded, before this session's changes and kept values) */
    uint32_t flags;    /* EMF_WS_CHANGED | EMF_WS_NEEDS_CLIENT */
    uint32_t pad;
} EmfWorldSettingInfo;

/* v23 change_component: add a component to an entity, or remove one, through the engine's own command (the record
 * the game's own programs push). Only the components EMF lists (docs/modding/reference.md, "v23"). */
#define EMF_COMPONENT_ADD 1u    /* add (only when absent: a present component is never overwritten: ALREADY) */
#define EMF_COMPONENT_REMOVE 2u
#define EMF_COMPONENT_PERSISTENT 0x01u /* accepted: a component the world save keeps (or removing DoNotSave) outlives
                                          the mod in that world; required for those */
#define EMF_COMPONENT_EXPERT 0x02u     /* reserved (refused in this version) */
#define EMF_COMPONENT_FAILED 0u   /* the entity is gone, the game did not apply it, the world ended... (see why) */
#define EMF_COMPONENT_DONE 1u     /* present (add) / absent (remove) after the step that applied it */
#define EMF_COMPONENT_ALREADY 2u  /* add: it was there already (data untouched); remove: it was not there */
typedef void (*EmfComponentCallback)(EmfWorld* world, uint32_t entity, uint32_t hash, uint32_t status, const char* why,
                                     void* user);
typedef struct EmfComponentChange {
    uint32_t size;             /* sizeof(EmfComponentChange) */
    uint32_t op;               /* EMF_COMPONENT_ADD or EMF_COMPONENT_REMOVE */
    uint32_t entity;
    uint32_t hash;             /* the component's hash (generated headers: emf::gen::ecs::X::kHash) */
    const void* data;          /* add: data_size bytes of the component, or NULL for zeros */
    uint32_t data_size;        /* add with data: the component's size in this game build (X::kSize) */
    uint32_t flags;            /* EMF_COMPONENT_PERSISTENT */
    EmfComponentCallback cb;   /* optional: once, on the server world's thread, after the step that applied it */
    void* user;
} EmfComponentChange;

/* v24 mod windows: a window EMF's overlay draws from a mod's description (window_open); one at a time takes the mouse
 * and keyboard. Widgets, in order, top to bottom: */
#define EMF_UI_TEXT      0u /* a line of text; wraps at the window's width */
#define EMF_UI_HEADING   1u /* a heading */
#define EMF_UI_BUTTON    2u /* a button: a click sends EMF_UI_CLICK with its key */
#define EMF_UI_ROW       3u /* a list row, full width: "Name (detail)" shows the detail dim at the right; clickable */
#define EMF_UI_SLOT      4u /* an item-like square (about 3 lines high): its text, a count in the corner */
#define EMF_UI_BAR       5u /* a bar filled to value/1000, its text on it */
#define EMF_UI_SEPARATOR 6u
#define EMF_UI_IMAGE     7u /* (v26) the picture `icon`, `height` text lines high (0 = 6), turning at `fps`; clickable
                               with a key */
/* EmfUiWidget.flags */
#define EMF_UI_SAME_LINE 0x01u /* on the line of the widget before (slots in a row, buttons side by side) */
#define EMF_UI_DISABLED  0x02u /* drawn dim, no events */
#define EMF_UI_SELECTED  0x04u /* lit: a selected row or slot, a "tab" button */
#define EMF_UI_DIM       0x08u
#define EMF_UI_EMPTY     0x10u /* slot: the empty look */
#define EMF_UI_LOCKED    0x20u /* slot: the locked look */
typedef struct EmfUiWidget {   /* no size field: EmfWindowDef.widget_size is the array's stride */
    uint32_t type;             /* EMF_UI_* */
    uint32_t flags;
    const char* key;           /* 1-48 bytes, sent back in events ("call:Wolf 2"); NULL or "" = no events */
    const char* text;          /* up to 256 bytes, or NULL */
    const char* tooltip;       /* shown while the pointer rests on it, or NULL; up to 512 bytes */
    uint32_t count;            /* slot: the number in its corner (0 = none) */
    uint32_t value;            /* bar: 0..1000 (more is 1000) */
    uint32_t rgba;             /* text colour 0xRRGGBBAA, 0 = the look's */
    uint32_t icon;             /* (v26) a picture from image_load, 0 = none: EMF_UI_IMAGE's picture; a slot's, inside the
                                  square; a row's, a square before the text (v24/v25: reserved, 0) */
    uint32_t item;             /* reserved (game item icons come later): 0 */
    uint32_t pad;
    /* v26 (a v24/v25 mod's widget_size ends before these: they read as 0) */
    uint32_t fps;              /* a turning picture's frames per second, 0..60 (0: its first frame) */
    float height;              /* EMF_UI_IMAGE: in text lines, 0..40 (0 = 6) */
} EmfUiWidget;
#define EMF_UI_PLACE_CENTRE 0u
#define EMF_UI_PLACE_LEFT   1u
#define EMF_UI_PLACE_RIGHT  2u /* e.g. beside the game's open inventory */
/* EmfUiEvent.kind */
#define EMF_UI_CLICK       1u
#define EMF_UI_RIGHT_CLICK 2u
#define EMF_UI_CLOSED      3u /* always the last event of an opened window: free `user` now */
/* EmfUiEvent.reason (EMF_UI_CLOSED) */
#define EMF_UI_BY_PLAYER   0u /* Esc, the close key, the window's X */
#define EMF_UI_BY_MOD      1u /* window_close, or window_open with the same id replaced it */
#define EMF_UI_BY_CORE     2u /* the world ended, the overlay turned off, the mod was turned off */
/* EmfUiEvent.flags */
#define EMF_UI_NO_WORLD    0x1u /* CLOSED with no world running: world is NULL; only free your data */
typedef struct EmfUiEvent {
    uint32_t size;             /* sizeof(EmfUiEvent) of the core */
    uint32_t kind;             /* EMF_UI_CLICK / _RIGHT_CLICK / _CLOSED */
    const char* window;        /* the window's id; valid during the call */
    const char* key;           /* the widget's key ("" for CLOSED) */
    const char* from_key;      /* reserved (drag and drop comes later): "" */
    uint32_t reason;           /* CLOSED: EMF_UI_BY_* */
    uint32_t flags;            /* EMF_UI_NO_WORLD */
} EmfUiEvent;
typedef void (*EmfUiCallback)(EmfWorld* world, const EmfUiEvent* event, void* user);
typedef struct EmfWindowDef {
    uint32_t size;             /* sizeof(EmfWindowDef) */
    uint32_t flags;            /* none yet: 0 */
    const char* id;            /* 1-48 of A-Z a-z 0-9 _ . -, starting with the mod's id and a dot ("beastmaster.stable") */
    const char* title;         /* up to 64 bytes, or NULL */
    const EmfUiWidget* widgets;
    uint32_t count;            /* 0..256 */
    uint32_t widget_size;      /* sizeof(EmfUiWidget) as the mod was built: the core steps through the array by it and
                                  reads fields past it as 0 */
    uint32_t close_key;        /* a keyboard key's generic virtual-key code that also closes it (the key that opened
                                  it; acts on a press), 0 = Esc only; no mouse buttons, no VK_LSHIFT-style codes */
    uint32_t place;            /* EMF_UI_PLACE_* */
    float width;               /* in text heights (the console's text size scales it); 0 = about 28 */
    uint8_t world_kind;        /* events run after the next step of a world of this kind (SERVER or CLIENT) */
    uint8_t pad[3];
    EmfUiCallback cb;          /* window_open only; window_update keeps the first */
    void* user;
} EmfWindowDef;

/* v27 mod messages: a mod on a player's game and the same mod on the server send each other messages on named
 * channels (reference.md "v27"). In a game the player hosts (or an offline one) they go in process. */
#define EMF_NET_TO_SERVER   0x01u  /* players' games send it, the server receives it */
#define EMF_NET_TO_PLAYERS  0x02u  /* the server sends it, players' games receive it */
#define EMF_NET_MAX_MESSAGE 16384u /* bytes; a channel's default limit is 4096 */
typedef struct EmfNetFrom {
    uint32_t size;          /* sizeof(EmfNetFrom) of the core */
    uint32_t player;        /* server: the sender's player entity in the server world; a player's game: 0 */
    uint64_t account;       /* server: the sender's account from the server's own records (never from the message);
                               0 in process */
    uint32_t peer_version;  /* the channel version the other side has */
    uint8_t admin;          /* server: 1 = the host's own game (or a [chat] admin) */
    uint8_t local;          /* 1 = the same process (the host's own game, an offline game) */
    uint8_t pad[2];
} EmfNetFrom;
typedef void (*EmfNetCallback)(EmfWorld* world, const EmfNetFrom* from, const void* data, uint32_t size, void* user);
typedef struct EmfNetChannel {
    uint32_t size;             /* sizeof(EmfNetChannel) */
    uint32_t version;          /* 1..65535: nothing is delivered between sides with different versions */
    const char* name;          /* "<your mod id>/<name>" (mod id: your DLL's file name); 1-48 and 1-32 of a-z 0-9 _ . - */
    uint32_t flags;            /* EMF_NET_TO_SERVER | EMF_NET_TO_PLAYERS */
    uint32_t max_size;         /* 1..EMF_NET_MAX_MESSAGE; 0 = 4096 */
    uint32_t per_10s;          /* messages one sender may send in 10 s; 0 = 100 */
    uint32_t pad;
    EmfNetCallback on_server;  /* the server world's thread, after its step (components, spawn allowed) */
    EmfNetCallback on_player;  /* a player's game's client world thread, after its step */
    void* user;
} EmfNetChannel;

/* v28 on_player: who joined, left or died on the server world (the host's game or a dedicated server) */
#define EMF_PLAYER_JOINED 1u /* in the world and ready to play (the host's own player at once), once per arrival; 1 s
                                after a world's load event everyone there (the host first, then by entity id) */
#define EMF_PLAYER_LEFT   2u /* gone from the world; back as another entity: left, then joined. None when the world
                                itself ends */
#define EMF_PLAYER_DIED   3u /* a joined player died */
typedef struct EmfPlayerEvent {
    uint32_t size;     /* sizeof(EmfPlayerEvent) of the core */
    uint32_t kind;     /* EMF_PLAYER_* */
    uint64_t account;  /* the player's account (as player_account and the command sender give it) */
    uint32_t player;   /* their entity in the server world (left: the one they had) */
    uint32_t killer;   /* died: the root source of the last hit on them within 10 s (not themselves), else 0 */
    uint8_t host;      /* 1 = the host's own player */
    uint8_t pad[7];
    char name[64];     /* their chat name (UTF-8), "" where players' chat is off or it is not known */
} EmfPlayerEvent;
typedef void (*EmfPlayerCallback)(EmfWorld* world, const EmfPlayerEvent* e, void* user);

/* v31 hits: one landing hit on an entity, as the server world applies it (the game's applyHitToEntity and HitEvent) */
typedef struct EmfHit {
    uint32_t size;            /* sizeof(EmfHit) of the core */
    uint32_t attacker;        /* the root source: the shooter of an arrow, the caster of a spell; 0 = none */
    uint32_t source;          /* the direct source: a projectile, a trap, the attacker itself */
    uint32_t target;          /* the actor hit (the part's owner, else the entity hit) */
    uint32_t part;            /* the entity hit; == target for most hits */
    int32_t amount;           /* health damage: > 0 damage, < 0 healing. Before the hit: writable */
    int32_t stamina;          /* stamina damage of a blocked hit (a blocked or parried hit never changes health: its
                                 amount is 0). Before the hit: writable */
    uint32_t flags;           /* the game's hit flags: EMF_HIT_* */
    uint16_t damage_types;    /* bit per type: 0 cut, 1 pierce, 2 blunt, 3 fire, 4 ice, 5 shroud, 6 lightning, 8 poison,
                                 9 wood, 10 stone, 11 metal, 12 explosion */
    uint8_t attacks;          /* the attack's kinds, a bit per kind: 1 melee, 2 ranged, 4 magic, 8 tool, 16 other,
                                 32 area, 64 skill shot (the game's mask: most hits have one bit; 0 = none, as a
                                 damage() hit) */
    uint8_t kind;             /* EMF_HIT_KIND_*: 0 an entity hit; 1 fall, 2 shroud, 3 cold, 4 drowning (no attacker,
                                 source, flags but EMF_HIT_INSTANT, types or attack kind; target = part) */
    uint32_t weapon_category; /* the game's WeaponCategoryId hash, 0 = none */
    uint32_t weapon_item;     /* the weapon's item id, 0 = none or not known */
    uint32_t program;         /* the impact program (the attack, skill or spell) */
    uint32_t impact;          /* the swing or projectile: hits of one share it */
    uint16_t reaction;        /* the game's HitReactionType (0 none, 2 light ...). Before the hit: writable */
    uint8_t target_faction;
    uint8_t from_mod;         /* 1 = a damage() hit */
    int32_t health_before;    /* the target's health before the hit, -1 = not known */
    int32_t health_after;     /* after the hit: at delivery; before the hit: what it will be as the hit stands; -1 */
    float position[3];        /* where it hit, metres */
    uint8_t cancel;           /* before the hit: 1 = drop it silently (no damage, number, reaction, aggro or credit) */
    uint8_t pad[3];
} EmfHit;
#define EMF_HIT_BLOCKED 0x1u
#define EMF_HIT_PARRIED 0x2u
#define EMF_HIT_CRIT 0x8u
#define EMF_HIT_BACKSTAB 0x20u
#define EMF_HIT_WEAKSPOT 0x40u
#define EMF_HIT_KILLED 0x2000u   /* after the hit only: the killing blow (kinds 1-4: it left no health) */
#define EMF_HIT_HEALING 0x4000u
#define EMF_HIT_TERRAIN 0x8000u  /* the game's terrain and building hits: never passed to mods */
#define EMF_HIT_BUILDING 0x10000u
#define EMF_HIT_INSTANT 0x01000000u /* kinds 2 and 4: the game set health to 0 at once (deadly shroud; a creature out
                                       of air) */
#define EMF_HIT_KIND_HIT 0u      /* a landing entity hit */
#define EMF_HIT_KIND_FALL 1u     /* fall damage, once per landing */
#define EMF_HIT_KIND_SHROUD 2u   /* shroud damage, every server step while the shroud timer is out */
#define EMF_HIT_KIND_COLD 3u     /* cold damage, every server step while body heat is 0 */
#define EMF_HIT_KIND_DROWNING 4u /* drowning, every server step while oxygen is 0 */
#define EMF_HIT_KINDS_ALL 0x1Fu  /* EmfHitRule.kinds: a bit per kind (1 << kind) */
/* A rule the core applies to every landing hit before the hit callbacks, without calling the mod. */
#define EMF_HIT_PLAYERS 0xFFFFFFF1u   /* attacker / target: any player */
#define EMF_HIT_CREATURES 0xFFFFFFF2u /* any creature (not a player, has health) */
#define EMF_HIT_OBJECTS 0xFFFFFFF3u   /* any placed object (Destructible) */
/* v32 buffs: the game's own buffs and debuffs (potions, food, Wet, Poisoned, auras, terrain debuffs) */
#define EMF_BUFF_FOOD 0u    /* EmfBuffType.slot / EmfBuff.slot: the slot group */
#define EMF_BUFF_BUFF 1u
#define EMF_BUFF_DEBUFF 2u
#define EMF_BUFF_RESTED 3u
#define EMF_BUFF_TERRAIN 4u
#define EMF_BUFF_HIDDEN 0x01u           /* EmfBuffType.flags: not shown in the game's UI */
#define EMF_BUFF_SAVED 0x02u            /* kept in the world save (potions, Wet, food); combat buffs are not */
#define EMF_BUFF_DESPAWN_ON_DEATH 0x04u /* ends when its target dies (EMF does not check that a target is alive) */
#define EMF_BUFF_FOOD_POISON 0x08u
#define EMF_BUFF_SHOW_AS_TAG 0x10u      /* shown as a tag on an enemy's health bar */
#define EMF_BUFF_APPLIED 1u             /* EmfBuffEvent.kind */
#define EMF_BUFF_REFRESHED 2u           /* its timer started again (given again, a reset-time type) */
#define EMF_BUFF_REMOVED 3u             /* cleansed (EmfBuffEvent.cleansed) or ended */
#define EMF_BUFF_REFUSED 4u             /* a buff_apply / buff_remove that did not happen: EmfBuffEvent.why */
typedef struct EmfBuffType {
    uint32_t size;            /* sizeof(EmfBuffType) of the core */
    uint32_t id;              /* the buff type id (stable: store ids, not indexes) */
    uint8_t slot;             /* EMF_BUFF_FOOD .. EMF_BUFF_TERRAIN */
    uint8_t apply_type;       /* given again: 0 refused, 1 its timer starts again, 2 and 3 replaced by a new one */
    uint16_t faction_exclude; /* a bit per faction the type is never given to (bit 7 = players) */
    uint32_t flags;           /* EMF_BUFF_HIDDEN ... */
    int64_t default_ns;       /* its default time, nanoseconds */
    uint32_t follow_up;       /* the type given when this one ends (Soaked -> Wet), 0 = none */
    uint32_t name_tag;        /* the game's text tags of its name and description (0 = none): buff_text */
    uint32_t desc_tag;
    uint32_t pad;
} EmfBuffType;
typedef struct EmfBuff {      /* one active buff of an entity */
    uint32_t size;            /* sizeof(EmfBuff) of the core */
    uint32_t type;            /* its type id */
    uint32_t entity;          /* the buff's own entity */
    uint8_t slot;             /* EMF_BUFF_FOOD .. EMF_BUFF_TERRAIN */
    uint8_t index;            /* its place in that slot group */
    uint8_t cleansed;         /* 1: being removed (gone after the next step) */
    uint8_t pad;
    uint32_t level;           /* from its source's level, 0 = none */
    uint32_t pad2;
    int64_t total_ns;         /* its whole time, -1 = not known yet (the step it was given) */
    int64_t remaining_ns;     /* the time left, -1 = not known yet */
} EmfBuff;
typedef struct EmfBuffEvent {
    uint32_t size;            /* sizeof(EmfBuffEvent) of the core */
    uint32_t kind;            /* EMF_BUFF_APPLIED, _REFRESHED, _REMOVED, _REFUSED */
    uint32_t target;          /* the entity that has (had) the buff */
    uint32_t type;
    uint32_t buff_entity;     /* the buff's own entity, 0 when refused */
    uint8_t slot;
    uint8_t cleansed;         /* REMOVED: 1 cleansed (always for buff_remove; the game's own cleanses when seen) */
    uint8_t replaced;         /* REMOVED: 1 when a new buff of this type took its place in the same step (given
                                 again: a replace type); it comes before that APPLIED */
    uint8_t pad;
    int64_t total_ns;         /* -1 = not known (yet) */
    int64_t remaining_ns;
    const char* why;          /* REFUSED: why (valid during the callback); else NULL */
} EmfBuffEvent;
typedef void (*EmfBuffCallback)(EmfWorld* world, const EmfBuffEvent* ev, void* user);

/* v33 markers: drawn by EMF on a player's game, over the 3D view and on the game's world map; a server sends them to
 * players' games with marker_send (players need EMF). Positions in world metres: x east, y up, z north. */
#define EMF_MARKER_WORLD 0x01u      /* EmfMarker.flags: drawn over the 3D view */
#define EMF_MARKER_GAME_MAP 0x02u   /* on the game's map screen (EMF draws it there, with its text; with game_icon the
                                       game's own icon of that type instead of the shape) */
#define EMF_MARKER_EDGE_ARROW 0x04u /* off screen or behind: an arrow at the screen's edge */
#define EMF_MARKER_DISTANCE 0x08u   /* the distance under the text ("42 m") */
#define EMF_MARKER_MINIMAP 0x10u    /* v35: a canvas's EMF_CANVAS_MARKERS command draws it (a minimap) */
#define EMF_MARKER_DIAMOND 0u       /* EmfMarker.shape, drawn when there is no icon */
#define EMF_MARKER_CIRCLE 1u
#define EMF_MARKER_SQUARE 2u
#define EMF_MARKER_NONE 3u          /* text only */
typedef struct EmfMarker {
    uint32_t size;      /* sizeof(EmfMarker) */
    uint32_t entity;    /* follow this entity (its position in the player's game; 0 = the fixed position x, y, z) */
    const char* id;     /* 1-48 of A-Z a-z 0-9 _ . - : , per mod: set again = update */
    double x, y, z;     /* the position (with entity: where it is drawn until the game knows the entity) */
    const char* text;   /* the label, UTF-8, may be NULL; cut at 64 bytes */
    float height;       /* metres above the position or the entity's origin */
    float max_distance; /* fades out toward it and is hidden beyond it (metres from the player); 0 = no limit */
    uint32_t rgba;      /* 0xRRGGBBAA of the shape (the icon's tint); 0 = the HUD's accent colour */
    uint32_t icon;      /* a picture from image_load (v26) instead of the shape, 0 = the shape; local markers only */
    uint32_t flags;     /* EMF_MARKER_WORLD ... */
    uint8_t shape;      /* EMF_MARKER_DIAMOND ... */
    uint8_t pad[3];
    uint32_t game_icon; /* EMF_MARKER_GAME_MAP: a game map icon type (marker_types) drawn by the game, 0 = EMF's shape */
    uint32_t pad2;
} EmfMarker;
#define EMF_MAP_TYPE_FAST_TRAVEL 0x01u /* EmfMapMarkerType.flags: a fast travel destination (shrines, altars) */
#define EMF_MAP_TYPE_CUSTOM 0x02u      /* one of the types players place themselves */
/* v34 the map screen's mouse, map actions, the ground under a spot */
#define EMF_MAP_SHIFT 1u /* EmfMapClick.mods */
#define EMF_MAP_CTRL 2u
#define EMF_MAP_ALT 4u
typedef struct EmfMapClick {
    uint32_t size;   /* sizeof(EmfMapClick) of the core */
    uint32_t button; /* 0 left, 1 right */
    uint32_t mods;   /* EMF_MAP_SHIFT ... held with it */
    uint32_t pad;
    double x, z;     /* the spot, world metres */
} EmfMapClick;
typedef void (*EmfMapClickCallback)(const EmfMapClick* click, void* user);
/* A map action a player did (on the server world's thread). */
typedef struct EmfMapDone {
    uint32_t size;      /* sizeof(EmfMapDone) of the core */
    uint32_t player;    /* their entity in the server world */
    uint8_t admin;      /* 1 = the host's own game, or an account in [chat] admins (everyone=1 too) */
    uint8_t pad[7];
    double x, z;        /* the spot, world metres, inside the world */
    const char* action; /* the action's id; valid during the callback */
} EmfMapDone;
typedef void (*EmfMapActionCallback)(EmfWorld* world, const EmfMapDone* done, void* user);
#define EMF_GROUND_OK 0u         /* EmfGround.status: y is the ground */
#define EMF_GROUND_NOT_LOADED 1u /* nobody is near the spot: hop_y is a height over it where a player may be teleported
                                    (the game loads the place around them); ask again once they are there */
#define EMF_GROUND_FAILED 2u     /* why says why */
#define EMF_FOG_NONE 0u          /* EmfGround.fog: the shroud a player standing there would be in */
#define EMF_FOG_TEMPORARY 1u     /* shroud the players removed */
#define EMF_FOG_DANGEROUS 2u     /* the shroud: its timer runs */
#define EMF_FOG_DEATH 3u         /* deeper shroud than this world's progress allows: a player there dies */
#define EMF_FOG_UNKNOWN 255u     /* not read (this game build, or it did not load in time) */
typedef struct EmfGround {
    uint32_t size;   /* sizeof(EmfGround) of the core */
    uint32_t status; /* EMF_GROUND_* */
    double x, y, z;  /* the spot; y: the ground (EMF_GROUND_OK), else 0 */
    double hop_y;    /* EMF_GROUND_NOT_LOADED: see above; else 0 */
    const char* why; /* not OK: why, in plain English ("" when OK); valid during the callback */
    uint32_t fog;    /* OK: EMF_FOG_* at the spot (1 and 2 m over the ground, and 3 m around): the worst */
    float deadly;    /* OK: the share of deadly shroud there, 0..1 (EMF_FOG_DEATH from 0.25) */
    /* From EMF 0.6.8 (check size covers water_y): OK and 1 when the spot is under water at least 1 m deep; water_y is
     * the water's surface (y stays the ground under it; fog is read at the surface). */
    uint32_t water;
    uint32_t pad;
    double water_y;
} EmfGround;
/* The ground under a spot, once: on the server world's thread. */
typedef void (*EmfGroundCallback)(EmfWorld* world, const EmfGround* ground, void* user);
typedef struct EmfMapMarkerType {
    uint32_t size;   /* sizeof(EmfMapMarkerType) of the core */
    uint32_t id;     /* for EmfMarker.game_icon */
    uint32_t flags;  /* EMF_MAP_TYPE_* */
    uint32_t pad;
    char name[48];   /* the game's internal name, NUL-terminated */
} EmfMapMarkerType;

/* v35 a minimap's parts: the player's view, the game's own map markers, canvases (drawing panels a mod owns). These
 * four structs keep their layout in later versions (arrays of them are passed without an element size): what grows
 * comes as new functions. */
#define EMF_VIEW_CAMERA 1u /* EmfPlayerView.flags: camera_yaw_deg is set (a frame of the 3D view was seen) */
typedef struct EmfPlayerView {
    uint32_t size;        /* sizeof(EmfPlayerView) of the core */
    uint32_t flags;       /* EMF_VIEW_CAMERA */
    double x, y, z;       /* the local player, world metres */
    float heading_deg;    /* where the player faces: 0 north (+z), 90 east (+x), 0..360 */
    float camera_yaw_deg; /* where the camera looks, the same way */
    float world_size;     /* the map's width and height in metres (10240 on Embervale) */
    float pad;
} EmfPlayerView;
#define EMF_GAME_MARKER_SHOWN 1u /* EmfGameMarker.flags: the game's map shows it */
typedef struct EmfGameMarker {
    uint32_t size;  /* sizeof(EmfGameMarker) of the core */
    uint32_t type;  /* the game's map icon type (EmfMarker.game_icon's numbers) */
    uint32_t flags; /* EMF_GAME_MARKER_SHOWN */
    uint32_t pad;
    double x, y, z;
} EmfGameMarker;
#define EMF_CANVAS_TOP_LEFT 0u /* EmfCanvas.corner */
#define EMF_CANVAS_TOP_RIGHT 1u
#define EMF_CANVAS_BOTTOM_LEFT 2u
#define EMF_CANVAS_BOTTOM_RIGHT 3u
#define EMF_CANVAS_RECT 0u /* EmfCanvas.shape */
#define EMF_CANVAS_CIRCLE 1u
typedef struct EmfCanvas {
    uint32_t size;   /* sizeof(EmfCanvas) */
    uint8_t corner;  /* EMF_CANVAS_TOP_LEFT ...: x, y are measured from it, inward */
    uint8_t shape;   /* EMF_CANVAS_RECT or EMF_CANVAS_CIRCLE: everything is clipped to the rectangle; with a circle the
                        FOG cells and MARKERS stay inside it (the game's map and shapes do not: paint the corners) */
    uint8_t hidden;  /* 1: kept, not drawn */
    uint8_t pad;
    float x, y, w, h; /* units of a 1080-pixel-high screen; w, h 8..1080 */
} EmfCanvas;
/* A canvas's drawing, in canvas units (0, 0 its top left .. w, h). rgba 0xRRGGBBAA. */
#define EMF_CANVAS_RECT_CMD 1u /* a[0..3] x, y, w, h; a[4] line width (0 = filled) */
#define EMF_CANVAS_CIRCLE_CMD 2u /* a[0..2] cx, cy, r; a[3] line width (0 = filled) */
#define EMF_CANVAS_LINE 3u /* a[0..3] x1, y1, x2, y2; a[4] width */
#define EMF_CANVAS_TRI 4u /* a[0..5] three points, filled */
#define EMF_CANVAS_TEXT 5u /* a[0..1] x, y (its centre); a[2] size (units); s the text (UTF-8) */
#define EMF_CANVAS_IMAGE 6u /* `image` a picture from image_load (v26); a[0..3] x, y, w, h; a[4..7] u0, v0, u1, v1; a[8] turn (degrees); rgba the tint */
#define EMF_CANVAS_FOG 7u /* the fog of war: a[0..1] the world x, z at the canvas centre, a[2] metres per unit; rgba the unexplored colour */
#define EMF_CANVAS_MARKERS 8u /* the markers flagged EMF_MARKER_MINIMAP: a[0..1] the world x, z at the centre, a[2] metres per unit */
#define EMF_CANVAS_MAP 9u /* the game's own map (north up): a[0..1] the world x, z at the centre, a[2] metres per unit */
typedef struct EmfCanvasCmd {
    uint8_t kind; /* EMF_CANVAS_RECT_CMD ... */
    uint8_t pad[3];
    uint32_t rgba;
    uint32_t image; /* IMAGE: a picture from image_load (v26); else 0 */
    uint32_t pad2;
    float a[10];
    const char* s; /* TEXT: the text; else NULL */
} EmfCanvasCmd;

/* v36 remove_building: a placed object removed the way a player's pick-up does */
#define EMF_REFUND_NONE 0u      /* nothing back */
#define EMF_REFUND_DROP 1u      /* the item as a loose pickup at the object */
#define EMF_REFUND_PLAYER 2u    /* into `player`'s inventory; a drop at the object when it does not fit */
#define EMF_REMOVE_FORCE 0x01u  /* remove it even when it holds items (they drop at its spot); never a Flame Altar */
typedef struct EmfRemoveBuilding {
    uint32_t size;     /* sizeof(EmfRemoveBuilding) */
    uint32_t entity;
    uint32_t refund;   /* EMF_REFUND_* */
    uint32_t player;   /* EMF_REFUND_PLAYER: who gets the item */
    uint32_t flags;    /* EMF_REMOVE_* */
    uint32_t refunded; /* out: the item id given back (0: none) */
    char why[120];     /* out: why it was refused ("" when removed) */
} EmfRemoveBuilding;

/* v37 path_find: a path on the game's own path grid (it knows terrain, buildings, placed objects and doors) */
#define EMF_PATH_WALK 0x0001u        /* the game's PathMoveType bits */
#define EMF_PATH_JUMP_UP 0x0004u
#define EMF_PATH_JUMP_DOWN 0x0008u
#define EMF_PATH_JUMP_ACROSS 0x0010u
#define EMF_PATH_OPEN_DOOR 0x0020u
#define EMF_PATH_SWIM 0x0040u
#define EMF_PATH_FOUND 1u     /* a path: see `reaches` (with `closest` it may end at the nearest reachable point) */
#define EMF_PATH_NO_PATH 2u
#define EMF_PATH_TOO_LONG 3u  /* the search ran out of nodes (raise max_nodes, or ask for a nearer point) */
#define EMF_PATH_NO_START 4u  /* no path grid at `from` (not loaded: too far from players, or inside something) */
#define EMF_PATH_NO_TARGET 5u
#define EMF_PATH_NO_MEMORY 6u
#define EMF_PATH_CANCELLED 7u /* path_cancel, the owner went, or the session ended */
#define EMF_PATH_OTHER 8u     /* the game gave another answer, or none in time */
/* v38 doors: door_state's answer, with flags */
#define EMF_DOOR_NONE 0u    /* not a door (no ServerToggleState and DirectionalInteraction), or not the server world */
#define EMF_DOOR_CLOSED 1u
#define EMF_DOOR_OPEN 2u
#define EMF_DOOR_LOCKED 0x10u /* flag: EMF holds it locked (no E prompt for anyone; kept with the world) */
#define EMF_DOOR_LOGIC 0x20u  /* flag: a switch drives it (the logic system owns its state): EMF does not touch it */
/* v39 enemy orders: a creature with an enemy AI sent at any entity for a while (a player, a creature, a door, a gate,
 * a placed object). While it holds, the target is its pick with its target switches off and an alarmed entry in its
 * aggro list, and an enemy at rest is put in its fight state: it walks up and attacks (seen: a Vukah walked 30 m to a
 * door). The game's hits on a placed door do 0: `damage` adds to each of the enemy's hits on the target (a hit rule;
 * seen: one hit with 200 broke a wooden door). Nothing is saved. */
#define EMF_ORDER_ALLIES 0x1u /* the creature may be a player's ally (a tamed beast, faction PlayerAlly) */
typedef struct EmfEnemyOrder {
    uint32_t size;    /* sizeof(EmfEnemyOrder) */
    uint32_t enemy;   /* the creature that attacks (it has DynamicAggro and SelectedTargets); never a player */
    uint32_t target;  /* any entity with a position */
    uint32_t flags;   /* EMF_ORDER_* */
    float seconds;    /* how long; 0 = until the target is gone or dead, the enemy is gone, or it is cancelled */
    int32_t damage;   /* > 0: added to each of the enemy's hits on the target (0..100000) */
    char why[96];     /* refused: why (NUL-terminated) */
} EmfEnemyOrder;
typedef struct EmfPathQuery {
    uint32_t size;        /* sizeof(EmfPathQuery) */
    uint32_t owner;       /* an entity the query belongs to (the game needs one); 0 = the first player */
    float from[3];        /* metres */
    float to[3];
    uint16_t moves;       /* EMF_PATH_*; 0 = walk */
    uint8_t max_jump_up;  /* metres; 0 = no limit (the game's rule) */
    uint8_t max_jump_down;
    float width;          /* the room the unit needs, metres (0 = the game's default) */
    float height;
    uint8_t closest;      /* 1 = fall back to the closest reachable point (the game's march does this) */
    uint8_t pad[3];
    uint32_t max_nodes;   /* 0 = 2000; at most 20000 (the game's own default is 500) */
} EmfPathQuery;
typedef struct EmfPathPoint {
    float x, y, z;
    uint32_t move; /* how it is reached: 0 walk, 2 jump up, 3 jump down, 4 jump across, 5 a door */
} EmfPathPoint;
typedef struct EmfPathResult {
    uint32_t size;    /* sizeof(EmfPathResult) */
    uint32_t status;  /* EMF_PATH_FOUND ... */
    uint8_t reaches;  /* 1: the last point is at `to` (within 1.5 m across and 3 m up or down) */
    uint8_t pad[3];
    uint32_t count;   /* points (at most 512) */
    float length;     /* metres from `from` along the points */
} EmfPathResult;
typedef void (*EmfPathCallback)(EmfWorld* world, uint32_t id, const EmfPathResult* result, const EmfPathPoint* points,
                                void* user);
/* v40 move orders: a creature with the enemy AI (an enemy or a tamed beast) walked to points through the game's own
 * guard order on a hidden waypoint EMF puts at each point in turn. It walks the game's way (its path, its jumps when its
 * kind can jump) and may fight on the way; EMF_MOVE_PEACEFUL makes it ignore players. Afterwards it guards whom it
 * guarded before (a tamed beast: its owner); any other creature that arrived lives there (its home is the last point).
 * Nothing is saved. */
#define EMF_MOVE_ALLIES 0x1u   /* the creature may be a player's ally (a tamed beast, faction PlayerAlly) */
#define EMF_MOVE_PEACEFUL 0x2u /* its fights against players are cleared on the way */
#define EMF_MOVE_ARRIVED 0u    /* EmfMoveCallback status: at the last point */
#define EMF_MOVE_STUCK 1u      /* no metre of progress for 15 s while not fighting (a wall, no path, the game gave up) */
#define EMF_MOVE_TIMEOUT 2u    /* its seconds ran out */
#define EMF_MOVE_GONE 3u       /* the creature died or went (or became a player's ally without EMF_MOVE_ALLIES) */
#define EMF_MOVE_CANCELLED 4u  /* enemy_move_cancel, a new move or enemy order on it, the world closing (world NULL) */
typedef struct EmfMoveOrder {
    uint32_t size;               /* sizeof(EmfMoveOrder) */
    uint32_t enemy;              /* the creature (it has DynamicEnemy01BehaviorState); never a player or a boss */
    uint32_t flags;              /* EMF_MOVE_* */
    uint32_t count;              /* points: 1..16 */
    const EmfPathPoint* points;  /* walked in order; x, y, z are read (move is ignored); path_find's points fit
                                    (thin a longer path to 16) */
    float arrive;                /* a point is reached within this many metres across (and that + 2 up or down), or
                                    within the creature's guard distance + 1 m once it guards the waypoint (it stops
                                    about 3 m short); 0 = 2; 0.5..50 */
    float seconds;               /* 0 = no time limit (it still ends stuck); at most 3600 */
    char why[96];                /* refused: why (NUL-terminated) */
} EmfMoveOrder;
typedef void (*EmfMoveCallback)(EmfWorld* world, uint32_t id, uint32_t status, void* user);

typedef struct EmfHitRule {
    uint32_t size;             /* sizeof(EmfHitRule) */
    uint32_t attacker;         /* 0 = anyone, an entity, or EMF_HIT_PLAYERS / _CREATURES / _OBJECTS */
    uint32_t target;           /* the same; an entity matches the actor hit or the part hit */
    uint32_t flags_all;        /* the hit has all these EMF_HIT_* flags (EMF_HIT_CRIT: crits only) */
    uint16_t damage_types_any; /* the hit has one of these damage types (EmfHit.damage_types bits); 0 = any */
    uint8_t attacks;           /* the hit has one of these attack kinds (EmfHit.attacks bits); 0 = any */
    uint8_t cancel;            /* 1 = cancel the hit */
    float scale;               /* amount (and a blocked hit's stamina) *= scale; 1 = unchanged */
    int32_t add;               /* then amount += add */
    int32_t min_health;        /* > 0: the target keeps at least this much health (1 = cannot die from hits) */
    uint8_t healing;           /* 0 = match damage hits only, 1 = healing hits only */
    uint8_t kinds;             /* the kinds it is about, a bit each (1 << EMF_HIT_KIND_*); 0 = entity hits only */
    uint8_t pad[2];
} EmfHitRule;
typedef void (*EmfHitBeforeCallback)(EmfWorld* world, EmfHit* hit, void* user);
typedef void (*EmfHitCallback)(EmfWorld* world, const EmfHit* hit, void* user);

/* v13 mod_info: what the loader found */
#define EMF_MOD_LOADED 0u
#define EMF_MOD_FAILED 1u   /* its DLL did not load, or emf_mod_load returned false or faulted */
#define EMF_MOD_DISABLED 2u /* switched off (enabled=0) */
#define EMF_MOD_REJECTED 3u /* not loaded: a broken mod.json, a missing dependency, a newer API needed... */
#define EMF_MOD_SCRIPT 4u   /* a script mod ("main" in mod.json), run by a script host mod */
#define EMF_MOD_PENDING 5u  /* not loaded yet: the loader is still working (seen from emf_mod_load) */
typedef struct EmfModInfo {
    uint32_t size;     /* set to sizeof(EmfModInfo) */
    uint32_t status;   /* EMF_MOD_* */
    char id[49];
    char name[64];
    char version[24];
    char source[128];  /* "clock\" (a folder in emf/mods) or "old_mod.dll" (a loose DLL) */
    char main[64];     /* a script mod's entry file ("main.lua"), else "" */
} EmfModInfo;

/* A mod ECS program (v3). It runs once per tick on worlds of world_kind and may read and send engine
 * events. Event names are the short type names ("HitEvent", "StartGuardingEvent"). */
typedef struct EmfProgramDef {
    uint32_t size;                 /* sizeof(EmfProgramDef) */
    const char* name;              /* unique program name */
    uint8_t world_kind;            /* EMF_WORLD_SERVER (host only) or EMF_WORLD_CLIENT */
    EmfProgramCallback fn;         /* fn(program_context, user), on a game worker thread */
    void* user;
    const char* const* events_in;  /* NULL-terminated list of events to read, or NULL */
    const char* const* events_out; /* NULL-terminated list of events to send, or NULL */
    /* ---- v8 (read only when size covers them) ---- */
    const char* const* singletons; /* NULL-terminated singleton names the program reads, as the game's programs name
                                      them ("g38_items::ItemRegistry", "BalancingRegistry"), or NULL. At most 16. */
    const char* const* other_read; /* NULL-terminated component short names ("CurrentTransform", "Inventory") the
                                      program reads on OTHER entities, or NULL. At most 16. Per-entity components
                                      only: for a split component name its Dynamic half ("DynamicActiveNpcState", not
                                      "ActiveNpcState"); a program naming a template-only component is left out of
                                      that world (logged). On a client world, declaring a component may change what
                                      the game asks the host to replicate (not tested online): prefer server programs. */
    /* ---- v16 (read only when size covers it) ---- */
    const char* const* other_write; /* NULL-terminated component short names the program WRITES on OTHER entities
                                       (program_write_other), or NULL. At most 16. Per-entity components only, as for
                                       other_read. A component may be in both lists. */
} EmfProgramDef;

/* v25 E prompts: the game's own "E <verb>" prompt on an entity a mod spawns or names, and a callback when a player
 * (the host, a joined player, anyone on a dedicated server) accepts it (reference.md "v25"). An offer is the three
 * components the game adds to its own objects at run time (InteractionOfferRuntime, ClientInteractionOffer,
 * CheckPlayerInRange); none of them is saved, so an uninstalled mod leaves plain entities behind. */
/* The game's own verbs (loca tag ids): every player sees the word, with or without EMF. */
#define EMF_VERB_USE      0x26437C13u
#define EMF_VERB_OPEN     0x0C2E4515u
#define EMF_VERB_TALK     0xD9861C4Bu
#define EMF_VERB_READ     0x631937DCu
#define EMF_VERB_SIT      0xDFB7E778u
#define EMF_VERB_SLEEP    0xC4E0F1CBu
#define EMF_VERB_PET      0x6D5B6297u
#define EMF_VERB_TOUCH    0x18A9222Cu
#define EMF_VERB_ACTIVATE 0x811C5D9Eu
#define EMF_VERB_PRESS    0x6F362030u
#define EMF_VERB_ENTER    0x785FF179u
#define EMF_VERB_INSPECT  0x260798EBu
#define EMF_VERB_TAKE     0x33E582F6u
#define EMF_VERB_COLLECT  0x9095E8BDu
#define EMF_VERB_REST     0x62F329EBu
#define EMF_VERB_RELEASE  0xAE94F76Au
/* What the player does on E (an action sequence's hash). */
#define EMF_SEQUENCE_TOUCH  0x3E5189BFu /* Interaction_TouchDevice: a short touch, no screen (the default) */
#define EMF_SEQUENCE_BUTTON 0xAF614FD8u /* Interaction_ButtonPress */
#define EMF_OFFER_LOW_PRIORITY 1u /* another target near the crosshair wins a tie */
#define EMF_OFFER_BUILD_MODE   2u /* offered in build mode too */
typedef struct EmfOffer {
    uint32_t size;      /* sizeof(EmfOffer) */
    uint32_t verb;      /* EMF_VERB_* (any loca tag id); 0 = "Use" */
    const char* text;   /* or the mod's own words (UTF-8, at most 63 bytes): for now the prompt shows no word ("E"
                           alone; it still works); NULL = verb */
    uint32_t sequence;  /* EMF_SEQUENCE_*; 0 = EMF_SEQUENCE_TOUCH */
    uint8_t range_m;    /* the game considers it when a player is this near; 0 = 32 */
    uint8_t flags;      /* EMF_OFFER_* */
    uint16_t reserved;
} EmfOffer;
/* A player accepted the offer on `entity`: on the server world's thread, for every player (`player` is their entity). */
typedef void (*EmfOfferCallback)(EmfWorld* world, uint32_t player, uint32_t entity, void* user);

typedef struct EmfApi {
    uint32_t version; /* EMF_API_VERSION of the core */
    uint32_t size;    /* sizeof(EmfApi) of the core */

    /* printf-style line into emf/emf_core.log, prefixed with the mod name. Thread-safe. */
    void (*log)(const char* mod, const char* fmt, ...);

    /* Most recently ticking world of a kind, or NULL (no server world on a client of someone else's game). */
    EmfWorld* (*server_world)(void);
    EmfWorld* (*client_world)(void);
    uint8_t (*world_kind)(EmfWorld* world);
    /* The world's tick counter. 0 on a game build EMF does not know whose tick fields it could not confirm (logged once). */
    uint32_t (*world_tick)(EmfWorld* world);
    /* Current ECS time in ns (tick * tick length), what game programs see as "now". 0 in the same case as world_tick. */
    int64_t (*ecs_time_ns)(EmfWorld* world);

    /* Named world singleton (e.g. "g38_daytime::Daytime"), or NULL. */
    void* (*singleton)(EmfWorld* world, const char* name);
    /* Local player's EntityId from the client world (LocalPlayerData+4), or 0. */
    uint32_t (*local_player)(void);
    /* Component data of an entity by its types.json qualifiedHash, or NULL. size_out may be NULL. */
    void* (*get_component)(EmfWorld* world, uint32_t hash, uint32_t entity, uint64_t* size_out);
    /* Calls cb for every entity id in the world (snapshot taken first). */
    void (*for_each_entity)(EmfWorld* world, EmfEntityCallback cb, void* user);

    /* cb runs after every simulation step of every world (server, prediction, client). */
    void (*on_tick)(EmfWorldCallback cb, void* user);
    /* cb runs once, after the next simulation step of a world of `kind`. Callable from any thread. */
    void (*run_on_world)(uint8_t kind, EmfWorldCallback cb, void* user);

    /* Address of a resolved engine signature by name (see src/common/signatures.cpp), or NULL. */
    void* (*resolve)(const char* sig_name);
    /* Hooks a resolved signature by name and enables it. Returns false if unknown/unusable. Since v14 hooks chain like
     * hook_pattern's (a signature EMF or another mod already hooks can be hooked too). */
    bool (*hook)(const char* sig_name, void* detour, void** original);

    /* ---- v2 ---- */
    /* Wraps an ECS program's update function by program name (see docs/ecs_programs.md):
     * pre(ctx,user) runs before the game's update, post(ctx,user) after. Runs on game worker threads.
     * Callable before any world exists (installed when the program registers). A name no world
     * registers is logged once the worlds are set up. Programs that share one update function
     * (identical code) all trigger the hook; a second hook on the same function fails. */
    bool (*hook_program)(const char* name, EmfProgramCallback pre, EmfProgramCallback post, void* user);
    /* Adds a mod ECS "system program": fn(ctx,user) runs once per tick on worlds of `world_kind`
     * (EMF_WORLD_SERVER = host only, EMF_WORLD_CLIENT), iterating no entities. Call it from
     * emf_mod_load: once the first world has registered its programs, further calls are refused
     * (false + log line). At most 16 mod programs. Experimental. */
    bool (*register_program)(const char* name, uint8_t world_kind, EmfProgramCallback fn, void* user);
    /* Spawns a template (16-byte GUID as stored in the game) at (x,y,z) meters and returns the new
     * entity id, or 0 (logged) if refused. SERVER world only, and only on the world passed to an
     * on_tick/run_on_world callback. */
    uint32_t (*spawn)(EmfWorld* world, const uint8_t template_guid[16], double x, double y, double z);
    /* Destroys an entity on a SERVER world (same calling rules as spawn). */
    bool (*destroy)(EmfWorld* world, uint32_t entity);
    /* cb(world_slot_id) after the host wrote a world save (runs on the game's save thread). */
    void (*on_save)(EmfSaveCallback cb, void* user);
    /* cb(world_slot_id) after the host loaded a world save, before the world starts (server thread). */
    void (*on_load)(EmfSaveCallback cb, void* user);

    /* ---- v3: programs with events (docs/engine_parts/re_events.md) ---- */
    /* Like register_program, plus the events the program reads (events_in) and sends (events_out). At most
     * 24 events in total. Same timing rule: call it from emf_mod_load. The core copies the definition. When
     * a world is set up, a program whose events do not all exist in that world is left out of it (logged). */
    bool (*register_program_def)(const EmfProgramDef* def);
    /* Inside the program's own callback only, with the program_context passed to it: sends one event
     * declared in events_out. Returns the event (zeroed; timeStamp already set for events that have one, i.e.
     * derive from GameEvent) for you to fill in before the callback returns, or NULL (logged once): called
     * elsewhere, not declared, expected_size (sizeof your struct) differs from the game's size, or the game's
     * event buffer is full. expected_size 0 skips the size check: only for tools that do not know the layout,
     * since after a game update you could then write a stale layout. Events of 256 bytes or more cannot be
     * sent. Game programs see it on the next tick. */
    void* (*emit_event)(void* program_context, const char* event_name, uint32_t expected_size);
    /* Inside the program's own callback only: calls cb for each event of this type the game queued for
     * this tick (declared in events_in). Returns the number of events passed to cb; 0 (logged once) when
     * refused for the same reasons as emit_event, or for event types whose size is not a multiple of 8
     * (their queue layout is not confirmed yet). Event pointers are valid only during cb. */
    uint32_t (*read_events)(void* program_context, const char* event_name, uint32_t expected_size, EmfEventCallback cb,
                            void* user);

    /* ---- v4: gameplay event callbacks ---- */
    /* cb(world, event, size, user) for every engine event `event_name` (short type name, e.g. "EntityDiedEvent",
     * "HitEvent", "PickupEvent") that happens in worlds of world_kind (EMF_WORLD_SERVER = host only, or
     * EMF_WORLD_CLIENT). The event is a copy, valid during cb. cb runs after that world's simulation step, on
     * its thread, like on_tick: reading components and spawn/destroy are allowed there. expected_size = sizeof
     * your struct (0 = do not check; events of another size are then still delivered). Call from
     * emf_mod_load: once the first world is set up it is refused (false + log line). At most 24 different
     * events per world kind across all mods; names the game does not know are logged and never delivered. */
    bool (*on_event)(uint8_t world_kind, const char* event_name, uint32_t expected_size, EmfWorldEventCallback cb,
                     void* user);

    /* ---- v5: items ---- */
    /* The game's item definitions, from the world's item registry (any world). The registry fills while the world
     * loads, so an early call can return nothing or only part of the list. Each call walks every item (about
     * 4200): look ids up once (find_item) and keep them. */
    void (*for_each_item)(EmfWorld* world, EmfItemCallback cb, void* user);
    /* Item id by the game's debug name: exact match first, else the first case-insensitive one. 0 if none. */
    uint32_t (*find_item)(EmfWorld* world, const char* name);
    /* Fills *out (out->size must be at least sizeof(EmfItemInfo)); false for an unknown item. */
    bool (*item_info)(EmfWorld* world, uint32_t item_id, EmfItemInfo* out);
    /* Drops count (1..65535) x item as a loose pickup 1 m above `player`; the game's own loot code puts it in the
     * inventory of whoever picks it up (popup, knowledge, PickupEvent). Same rules as spawn: SERVER world, inside an
     * on_tick/run_on_world/on_event callback on the world passed to it. Returns the pickup entity id, or 0 (logged). */
    uint32_t (*give_item)(EmfWorld* world, uint32_t player, uint32_t item_id, uint32_t count);
    /* spawn with up to 15 extra spawn components after the transform (e.g. a Level or PickupItem). Same rules as
     * spawn. Do not pass CurrentTransform: x, y, z give it. */
    uint32_t (*spawn_ex)(EmfWorld* world, const uint8_t template_guid[16], double x, double y, double z,
                         const EmfComponentInit* components, uint32_t count);

    /* ---- v6: developer commands ---- */
    /* Adds a command typed into the EMF console window or emf/commands.txt. name: 1-32 of [A-Za-z0-9_.-], unique
     * ignoring case (prefix it with your mod, e.g. "mymod.heal"); help: shown by "help" after the name, e.g.
     * "<amount>: heal the player". cb runs on the host's server world after its step, like on_tick (components,
     * spawn, give_item allowed). A crashing command is disabled. Callable any time. */
    bool (*register_command)(const char* name, const char* help, EmfCommandCallback cb, void* user);
    /* printf-style line back to whoever typed the running command (console window and core log). Outside a
     * command it does nothing. */
    void (*command_reply)(const char* fmt, ...);

    /* ---- v7: template data ---- */
    /* The entity's template data for a component (types.json qualifiedHash), e.g. ActiveNpcState or
     * Enemy01BehaviorState: the defaults every entity of that template SHARES. *size_out (optional) gets the game's
     * own size of that component, 0 if unknown; compare it with your struct before trusting the layout. NULL if the
     * entity or its template lacks the component (or on a game build without the function).
     * The game keeps this memory READ-ONLY: writing through the pointer faults (and disables your callback). Change
     * it with write_memory. A change applies to every entity of the template in the server AND the client world
     * (they share the data) until the game restarts; it is never saved. Only code that reads the template each time
     * sees a change (values copied at spawn keep their old value). Same thread rules as get_component. */
    void* (*get_template_component)(EmfWorld* world, uint32_t hash, uint32_t entity, uint64_t* size_out);

    /* ---- v7: item definitions ---- */
    /* The game's ItemInfo for an item in this world's registry (*size_out = 2088), or NULL. Layout: see
     * docs/engine_parts/inventory_crafting.md (+0x14 u16 maxStackSize, +0xcc category, +0x5d8 flags). READ-ONLY
     * memory like template data: change it with write_memory (or set_item_max_stack). Shared by every stack of the
     * item and by both worlds; changes last until the game restarts and are never saved by EMF. */
    void* (*item_data)(EmfWorld* world, uint32_t item_id, uint64_t* size_out);
    /* Sets an item's maximum stack size (1-65535) in every live world (server and client). False for 0, an unknown
     * item, or before a world has loaded its items. Stacks already larger stay as they are; the game saves stack
     * counts as they are, so a game without the change may see oversized stacks. Call it from a world callback
     * (on_tick, a command): it reads the worlds' item registries on the calling thread. */
    bool (*set_item_max_stack)(uint32_t item_id, uint16_t max);
    /* Copies n bytes into game DATA, e.g. a field of get_template_component or item_data. The game keeps its
     * resource memory (templates, item definitions) read-only; those pages are made writable for the copy and set
     * back. Only private heap memory is unlocked: code, EXE/DLL constants, file views, no-access or unmapped memory
     * are refused (false, nothing written). Same thread rules as the data you write. */
    bool (*write_memory)(void* dst, const void* src, uint64_t n);

    /* ---- v8: singletons and other entities inside mod programs ---- */
    /* Inside the program's own callback only: a singleton declared in EmfProgramDef.singletons, or NULL (logged once:
     * not declared, called elsewhere, or the engine gave a pointer that does not match the world's singleton). */
    void* (*program_singleton)(void* program_context, const char* name);
    /* Inside the program's own callback only: `entity`'s component `component` (declared in EmfProgramDef.other_read)
     * through the game's own other-entity accessor, or NULL when the entity has none. Read it only; the pointer is
     * valid during the callback. expected_size = sizeof your struct (0 = no check). The first read through each
     * accessor in a world is compared with getComponent: on a mismatch that accessor is refused from then on (logged),
     * so a layout surprise after a game update cannot hand you another component's data. */
    void* (*program_read_other)(void* program_context, const char* component, uint32_t entity,
                                uint32_t expected_size);

    /* ---- v8: items straight into an inventory ---- */
    /* Puts count (1-65535) x item into `entity`'s inventory (the local player, a companion, any entity with an
     * initialized InventorySetup) through the game's own inventory transaction on the next server tick: stacks are
     * topped up first, gear items get their own item entity, the receiver gets the "+N" popup and knowledge unlocks.
     * HOST only: `world` must be the server world. Returns false (logged, cb not called) when refused at once: not a
     * server world, entity 0, unknown item, bad count, 64 requests already waiting, or the feature is off on this game
     * build. Otherwise cb(world, entity, item, count, added, user) runs after that server step on its thread (like
     * on_event; components and spawn allowed there), with added < count when the inventory was full (0 when nothing
     * fit, the entity has no inventory, or the core's emf_inventory program has not run in that world for 2 s).
     * Requests still waiting when a session ends get cb with added 0 once the next server world starts. cb may be
     * NULL. Callable from any thread. */
    bool (*put_items)(EmfWorld* world, uint32_t entity, uint32_t item_id, uint32_t count, EmfPutItemsCallback cb,
                      void* user);

    /* ---- v9 ---- */
    /* Number of the hosted session: 0 before the first server world, +1 each time a server world is set up (a new
     * game, a reload, another world). A reload often reuses the world object's ADDRESS, so compare this number, not
     * the EmfWorld pointer, to notice a new session (entity ids are reused across sessions). It changes before the
     * new session's first tick. Any thread. */
    uint32_t (*server_session)(void);

    /* ---- v10: radial wheel and on-screen notes ---- */
    /* Opens a radial wheel in the middle of the screen (the overlay draws it; while it is open the mouse steers the
     * highlight instead of the camera and keys go to the wheel; releases of keys held when it opened still reach the
     * game). False (logged) when refused: bad definition, the console or another wheel is open, or the overlay
     * cannot draw. Callable from any thread. */
    bool (*wheel_open)(const EmfWheel* wheel);
    /* printf-style line shown at the top of the screen for a few seconds (and in the console and the log). Any
     * thread. */
    void (*notify)(const char* fmt, ...);

    /* ---- v11: items with a level and rarity, and taking items ---- */
    /* put_items with an item level and rarity (gear). Same rules as put_items. */
    bool (*put_items_ex)(EmfWorld* world, const EmfItemsRequest* request);
    /* Removes `count` of `item` from `entity`'s inventory through the game's inventory transaction (removeItems),
     * trying request->slot first, then the rest of its inventories (backpack, action bars, equipment). All or nothing:
     * if fewer are there, nothing is taken (done = 0). A gear item's own data entity (pide: its level, perks, gem) is
     * destroyed by the game: put_items_ex can create a new one with the same item id, level and rarity. Host only. */
    bool (*take_items)(EmfWorld* world, const EmfItemsRequest* request);

    /* ---- v12: text kept on screen ---- */
    /* Keeps a line of text on screen until it is changed or removed: `id` names it (1-48 of A-Z a-z 0-9 _ . -; prefix it
     * with your mod, e.g. "clock.time"), `text` replaces it (up to 160 characters; NULL or "" removes the line), `place`
     * is EMF_HUD_TOP_RIGHT / _BOTTOM_RIGHT / _BOTTOM_LEFT (lines of one place are listed by id), rgba = 0xRRGGBBAA (0 =
     * the default colour). At most 32 lines across all mods. Not shown while the console is open, while no world runs (main
     * menu, loading) or when the overlay is off (the lines are kept). Text longer than 160 bytes is cut. False (logged) for a bad id or place, or when 32 other lines exist. Any thread. */
    bool (*hud_set)(const char* id, const char* text, uint32_t place, uint32_t rgba);

    /* ---- v13: settings and mod info ---- */
    /* Declares (or redeclares) one of `mod`'s settings (settings in mod.json are declared before emf_mod_load runs; use
     * this for mods without a manifest). The value in the mod's ini is read now: out-of-range numbers are clamped, values
     * that do not fit fall back to the default. False (logged) for a bad mod id or definition. Any thread. */
    bool (*setting_declare)(const char* mod, const EmfSettingDef* def);
    /* A setting's current value as a number (bool 1/0, choice = index), or `fallback` when it is not declared. */
    int64_t (*setting_int)(const char* mod, const char* key, int64_t fallback);
    double (*setting_float)(const char* mod, const char* key, double fallback);
    /* Copies the value as text into buf (cut to size-1 bytes, NUL-terminated) and returns its full length; 0 (and "")
     * when it is not declared. To tell an empty value (a cleared key or text) from an undeclared setting, pass size >= 2
     * and buf[1] != 0: a declared empty value sets buf[1] to 0 as well, an undeclared setting leaves it alone (a
     * core of EMF 0.2.0 or older leaves it alone in both cases). emf::setting_text does this. */
    uint32_t (*setting_text)(const char* mod, const char* key, char* buf, uint32_t size);
    /* Changes a setting (the same checks as the Mods tab) and saves it in the mod's ini. False (logged) when the value is
     * not valid or the setting is not declared. Change callbacks run as for a change in the Mods tab. Any thread. */
    bool (*setting_set)(const char* mod, const char* key, const char* value);
    /* cb(world, mod, key, user) after each change of one of `mod`'s settings (NULL: every mod's), from the Mods tab, the
     * console's set command or setting_set: after the next simulation step of any world, on its thread (components may
     * be read there; for gameplay changes check the world kind or use run_on_world). While no world runs (main menu)
     * the call waits. A callback that faults is switched off (logged). */
    void (*on_setting_changed)(const char* mod, EmfSettingCallback cb, void* user);
    /* What the loader found for mod `id` (any status). False for an unknown id or out->size < sizeof(EmfModInfo).
     * Every mod is listed from the first emf_mod_load on; mods that load later show EMF_MOD_PENDING until then. */
    bool (*mod_info)(const char* id, EmfModInfo* out);
    /* The same by position (0, 1, 2... until it returns false), sorted by name. */
    bool (*mod_info_at)(uint32_t index, EmfModInfo* out);

    /* ---- v14: patterns, hooks, build info ---- */
    /* The address a pattern finds in the game exe (see EmfPattern), or 0 (logged: no match, several matches, a bad
     * pattern, a result outside the exe). It matches the exe as it is on disk (so a function another hook already
     * patched is still found) and returns the live address. It scans the section each call: find once, keep the
     * address. Any thread. */
    uintptr_t (*find_pattern)(const EmfPattern* pattern);
    /* find_pattern + hook: `detour` replaces the function found; *original (may be NULL) gets the way to call the
     * original, set before the hook is live. If the function is already hooked (by EMF or another mod) the hooks
     * chain: yours runs first and *original calls the previous one. `name` labels it in the console's "hooks" list.
     * False (logged, nothing changed) when the pattern fails or the hook cannot be made. Hooks stay for the whole
     * process. Hook from emf_mod_load, or know that the function may run on other threads while you hook. */
    bool (*hook_pattern)(const char* name, const EmfPattern* pattern, void* detour, void** original);
    /* The same for an address you found yourself; it must be executable code of a loaded image (the exe or a DLL). */
    bool (*hook_address)(const char* name, void* address, void* detour, void** original);
    /* Which game build runs. False when out->size < sizeof(EmfGameBuild). Any thread. */
    bool (*game_build)(EmfGameBuild* out);

    /* ---- v15: commands from players ---- */
    /* Inside a command callback: who typed the command (a player in the game's chat on a dedicated server or in a game
     * you host, or the console). False outside a command or when out->size < sizeof(EmfCommandSender). */
    bool (*command_sender)(EmfCommandSender* out);

    /* ---- v16: writes the players see ---- */
    /* Inside the program's own callback only: another entity's component, declared in EmfProgramDef.other_write, to
     * WRITE into now. The game's own writeOther gives it and marks it changed, the way the game's programs write (for
     * the players' copies and the save); a pointer from get_component is not tracked. In a game you host the local
     * client sees untracked writes too; what tracking adds for players who joined is not tested yet.
     * Call it right before writing, once per entity per tick. expected_size 0 = do not check; NULL if the entity has
     * none, the size differs, the component is not declared for writing, or outside a program (logged once). */
    void* (*program_write_other)(void* program_context, const char* component, uint32_t entity,
                                 uint32_t expected_size);

    /* ---- v17: commands any player may run ---- */
    /* register_command with options (EmfCommandDef). With EMF_COMMAND_PLAYERS every player may run the command from the
     * game's chat; without it, only the accounts in [chat] admins (or everyone with [chat] everyone=1), as for
     * register_command. The console, emf/commands.txt and the host's own chat run every command. Registering a name that
     * exists again with the same cb and user updates its help and flags (a script host re-declaring a command after a
     * reload); any other duplicate is refused. False (logged) for a bad definition, unknown flags or a taken name. */
    bool (*register_command_ex)(const EmfCommandDef* def);

    /* ---- v18: attributes and damage (docs/engine_parts/re_attributes_menu.md) ---- */
    /* An attribute of `entity` (EMF_ATTR_*, or another id of its Health, Stamina or Mana component) read the way the game
     * reads it, through its attribute registry. SERVER world only (a joined client has no Health component: read
     * NetworkHealth there). False (*out untouched) when the entity has no such attribute, its component is not set up
     * yet, or the feature is off on this game build (logged once). Same thread rules as get_component. */
    bool (*attribute_get)(EmfWorld* world, uint32_t entity, uint32_t attr_id, int32_t* out);
    /* Adds `delta` to the attribute through the game's own attrAdd, which also updates the values computed from it; the
     * game's death program and NetworkHealth (what players see) pick the change up on the next step. Clamped: damage
     * stops at 0, a heal of current health, stamina or mana at its maximum; a heal of an attribute without a readable
     * maximum is refused.
     * It is not a hit: no kill credit or XP, no hit reaction, no damage numbers, no killing blow (a prop is not
     * destroyed); use damage for that. SERVER world. Inside a world callback on that world it runs at once; anywhere
     * else it runs after the server world's next step (true = queued; a refusal then is logged). False (logged) when
     * refused. */
    bool (*attribute_add)(EmfWorld* world, uint32_t entity, uint32_t attr_id, int32_t delta);
    /* `amount` (> 0) of true damage (no armour, block, crit or base protection) from `attacker` to `target`, as a game
     * hit: on the next step the game's own hit code applies it, with a HitEvent (damage numbers, always a light hit
     * reaction, aggro toward the attacker), the killing blow, and kill credit and XP for the attacker when it is a player
     * (0 = no attacker, no aggro). The target needs CombatFlags.handleHits (creatures and players have it), Health, and
     * no CombatFlags.immune. Only on game builds whose layouts EMF checked (1273043). SERVER world. Inside a world callback on
     * that world the target is checked at once; anywhere else at the server world's next step (true = queued; a refusal
     * then is logged). False (logged) when refused or off on this game build. */
    bool (*damage)(EmfWorld* world, uint32_t attacker, uint32_t target, int32_t amount);

    /* ---- v19: chat lines and saves on a server (docs/modding/server.md) ---- */
    /* A chat line that only `player` (a player entity of the server world) sees, the way EMF's command replies reach a
     * player: their game shows it under their own name ("Ember: <text>"; it shows no line without a speaker). Players'
     * chat must be on: always on a dedicated server; in a game you host with [chat] players=1. In a game you host a line
     * for the host itself becomes a notify (the host's own game does not show the server's lines). '\n' splits lines: at
     * most EMF_CHAT_MAX_LINES per call (the next becomes "...", the rest are dropped; the log has every line), each cut at
     * EMF_CHAT_LINE_BYTES bytes; control characters become '?'. A mod may send at most 30 lines in 10 s (more are refused
     * and logged). The line reaches the player after the server's next network step. Inside a callback on `world` it is
     * queued at once, anywhere else after the server world's next step. True when queued; false (logged) when refused:
     * not a server world, no such player or no account for them, players' chat is off, the limit, or the server's chat
     * functions were not found on this game build. */
    bool (*chat_tell)(EmfWorld* world, uint32_t player, const char* text);
    /* chat_tell for every player connected to the server (on a dedicated server all of them; in a game you host the guests,
     * and a notify for the host). Same line rules and limit (a broadcast counts its lines once). Any thread. True when
     * queued (or, in a game you host without [chat] players=1, shown to the host only); false (logged) when refused. */
    bool (*chat_broadcast)(const char* text);
    /* The player's name, UTF-8, cut to size-1 bytes and NUL-terminated: the name their connection carries, which the
     * game's chat shows as the speaker (their platform name, e.g. their Steam name; not the character's name). Read from
     * the server's connection records, so players' chat must be on (as chat_tell). False (buf = "") when it is not known:
     * not a player entity of this server world, no connection record for them, or players' chat is off. Server world;
     * same thread rules as get_component. */
    bool (*player_name)(EmfWorld* world, uint32_t player, char* buf, uint32_t size);
    /* cb(world, account, entity, text, user) for every chat line a player sends to the server that is not an EMF command
     * (a "/word" that is no command counts), on the server world's thread after its next step. Read-only: the line is
     * shown as usual. Where players' chat is on (as chat_tell). At most 256 lines wait for that step (more are dropped,
     * logged). Callable any time from any thread. False (logged) when players' chat is off here or cb is NULL. A callback
     * that faults is switched off (logged). */
    bool (*on_chat)(EmfChatCallback cb, void* user);
    /* Asks the game to save the world now (the save its 30 s timer makes; on_save follows once it is written). Host or
     * dedicated server. Inside a server world callback it asks at once, anywhere else after the server world's next step.
     * The game skips the request while a save is already running or the world is not running. True when asked (or
     * queued); false (logged) when the save function was not found on this game build, no save was loaded in this session
     * (EMF knows the game's server instance from its load), or this game hosts no world. */
    bool (*request_save)(void);

    /* ---- v20: teleport (docs/engine_parts/re_teleport.md) ---- */
    /* Moves an entity as the game's fast travel does. EMF hands the game a Teleport for it and the game's own
     * teleport code moves it on a following step (the ground search with FIND_GROUND, the destination loaded around a
     * player, fall damage, gliding, climbing and NPC behaviour reset); every player's game snaps it to the new place,
     * without rubber-banding (tested live in a hosted game and on a dedicated server with a joined player, on villagers and
     * on an enemy). SERVER world only: the host or a dedicated server (a joined client gets false). Any thread.
     * True = queued: it happens within 1-3 server steps (longer while FIND_GROUND waits for the destination to load); then
     * cb(world, result, user) runs after that step, on the server world's thread (components, spawn and destroy allowed
     * there), with MOVED and where the entity is now, or FAILED and why. False (logged once per kind of problem; cb not
     * called) when refused at once: not the server world, bad numbers or flags, a teleport of that entity already under
     * way, 64 under way, or teleport is off on this game build (it needs the QueuePush signature and a build whose layouts
     * EMF checked: 1273043). Called inside a callback on that world, the entity is checked at once too (no
     * CurrentTransform, LastTransform or TeleportCount, dead: false); called anywhere else that check comes after the next
     * step and a refusal reaches cb as FAILED. It waits (at most 5 s) while a game teleport of the entity is under way or a
     * player is not ready for gameplay (respawning, loading), and gives up (FAILED) when the game has not moved the entity
     * 10 s after EMF's push (EMF then takes the Teleport off again, so a player is never left frozen). Every accepted
     * request gets exactly one LAST answer: when its session ends first (a reload, back to the menu) it comes with
     * EMF_TELEPORT_SESSION_ENDED, at once (not when the game process just ends).
     * NPCs: the game removes an enemy moved away from every player within a second (keep NPC teleports near a player);
     * villagers stay and keep their behaviour. A save during a teleport just drops it (Teleport is not saved). */
    bool (*teleport)(EmfWorld* world, const EmfTeleport* request);

    /* ---- v21: world settings (docs/modding/world_settings.md) ---- */
    /* The running world's settings (the game's ecs::GameSettings: the values of enshrouded_server.json's
     * "gameSettings", or of the world's preset), changed while it runs. SERVER world only: the host or a dedicated
     * server (a joined client gets false). Any thread. key = a setting's name as enshrouded_server.json has it
     * ("playerHealthFactor", case-insensitive) or its short name ("loot", "day_minutes"; the list is in the docs).
     * Values are numbers: factors as they are, durations in minutes, enums by their number, on/off as 1/0.
     * A change is queued and written on the next server step; several changes in one step are one write, and the game
     * sends the new settings to every player (at most once a second). Changes last until the world is left, unless
     * EMF_WS_PERSIST keeps them for this world. EMF never edits enshrouded_server.json. */
    bool (*world_setting_get)(const char* key, double* value); /* now; false: unknown key or no server world */
    bool (*world_setting_set)(const char* key, double value, uint32_t flags); /* true: queued for the next server
        step, flags EMF_WS_PERSIST or 0; false (logged, with the allowed range): unknown key, out of range (never
        clamped), other flag bits, no server world */
    bool (*world_setting_reset)(const char* key, uint32_t flags); /* back to the world's own value; key NULL or
        "all": every setting. With EMF_WS_PERSIST the kept value is forgotten too */
    bool (*on_world_setting_changed)(EmfWorldSettingCallback cb, void* user); /* cb runs once per changed setting
        after the server step wrote it (server world's thread), whoever changed it: a command, a mod, a kept value
        applied after a load. False: cb NULL or no world settings on this core */
    bool (*world_setting_info)(uint32_t index, EmfWorldSettingInfo* out); /* the settings one by one from index 0:
        false past the last one, without a server world, or when out->size is too small */

    /* ---- v22: a player's level (with the EMF_ATTR_XP* / EMF_ATTR_LEVEL attributes above) ---- */
    /* The XP level `level` needs before the next one, from the game's own curve (what the game keeps as
     * EMF_ATTR_XP_REQUIRED at that level); 0 for a level outside 1..cap, without a server world, or when the game's
     * function is not found on this build. Any thread. */
    uint32_t (*player_xp_required)(EmfWorld* world, uint32_t level);
    /* The highest level a player can reach (the game's level cap; pending XP at the cap is thrown away); 0 when unknown. */
    uint32_t (*player_level_cap)(EmfWorld* world);
    /* Sets a player's level to `level` (1..cap) with `xp` towards the next one (cut to below its requirement), the way the
     * game's own admin "set level" does: the level, the XP, the requirement for that level, and no pending XP (it would
     * level the player up again). Lowers as well as raises; no level-up event (to raise with the game's level-ups, give
     * XP: EMF_ATTR_XP_PENDING). The skill points follow the level (the game computes them); after lowering, a skill tree
     * that costs more points than the level gives is reset by the game at the next load. Saved with the character. SERVER world; inside a world callback on that world at once, elsewhere after
     * the server world's next step (true = queued, a later refusal is logged). False (logged) when refused. */
    bool (*player_level_set)(EmfWorld* world, uint32_t player, uint32_t level, uint32_t xp);

    /* ---- v23: add a component to an entity, or remove one (EmfComponentChange above) ---- */
    /* Adds a component to an entity, or removes one, through the engine's own command. Only those EMF lists
     * (reference.md "v23"): in this version ScaleComponent (uniformScale 0.1-10) and InteractionPause, removed only by
     * the mod that added them this session; the ones the world save keeps are refused until checked in game (then
     * with EMF_COMPONENT_PERSISTENT). A ScaleComponent added later is not drawn: spawn with it (spawn_ex). SERVER world, never a player. True = queued: checked after the server
     * world's next step, applied in the step after, and cb answers once (EMF_COMPONENT_DONE / _ALREADY / _FAILED with
     * why) on the server world's thread; when the world ended first (a new world, the menu) it answers _FAILED with a
     * NULL world, on the thread that ended it. False (logged) when refused at once; cb is not called then. */
    bool (*change_component)(EmfWorld* world, const EmfComponentChange* change);

    /* ---- v24: mod windows (EmfWindowDef above; reference.md "v24") ---- */
    /* Opens a window drawn by EMF's overlay (copied, every string too). With the open window's id it replaces it (that
     * window's cb gets CLOSED, BY_MOD). One window at a time: false (logged) while another id is open, a wheel is open,
     * the overlay is off or cannot draw (always on a dedicated server), no world of def->world_kind runs (a client in
     * someone else's game has no server world), or the definition is bad. It may
     * open while the console is open: it shows under the console and gets the input when the console closes. Events
     * (CLICK, RIGHT_CLICK, the last one CLOSED) reach cb after the next step of a world of def->world_kind, on its
     * thread; CLOSED with no world running comes at once with EMF_UI_NO_WORLD and a NULL world. Any thread. */
    bool (*window_open)(const EmfWindowDef* def);
    /* New contents for the open window def->id (cb and user are ignored; the scroll stays). False when it is not open,
     * for example because the player closed it: it never reopens a window. Any thread. */
    bool (*window_update)(const EmfWindowDef* def);
    /* Closes the window `id`; its cb gets CLOSED (BY_MOD). False when it is not open. Any thread. */
    bool (*window_close)(const char* id);

    /* ---- v25: E prompts (EmfOffer above; reference.md "v25") ---- */
    /* Spawns a template (spawn_ex's rules: from a world callback on the server world) with an offer on it. The entity
     * id, or 0 (logged). A spawned prop may stay in the world save, without its offer: after a load, offer it again
     * rather than spawning a second one. */
    uint32_t (*spawn_offer)(EmfWorld* world, const uint8_t template_guid[16], double x, double y, double z,
                            const EmfOffer* offer, EmfOfferCallback cb, void* user);
    /* Puts an offer on an entity of the server world (from a world callback on it): it shows after a few steps. Refused
     * (false, logged): a player, an entity without a position, one with an offer of the game's own (a chest, a bench),
     * one with another mod's offer, your own with other options. The entity needs a collider, or no prompt shows.
     * EMF drops the offer (silently) when the entity goes or loses it, and never puts it back by itself: offer again
     * when your mod finds the entity, and after a load. */
    bool (*offer)(EmfWorld* world, uint32_t entity, const EmfOffer* offer, EmfOfferCallback cb, void* user);
    /* Takes the mod's offer off the entity. False when it has none of this mod's. */
    bool (*offer_remove)(EmfWorld* world, uint32_t entity);

    /* ---- v26: pictures in windows (reference.md "v26") ---- */
    /* A picture for EmfUiWidget.icon: a .png file, or a folder whose .png files (sorted by name) are the frames of a
     * turning picture, all the same size (at most 512 x 512 px, 64 frames). `path`: relative to the calling mod's folder
     * (the folder of its DLL), absolute, or "portrait:<key>" for EMF's portrait cache (<game>/emf/cache/portraits/<key>/,
     * key: A-Z a-z 0-9 _ . -). Decoded before it returns; the same path again gives the same handle. 0 when refused
     * (logged once per path) and on a dedicated server (no windows). Pictures stay until the game closes (at most 128,
     * 64 MB). Any thread. */
    uint32_t (*image_load)(const char* path);
    /* True while your mod's offer (v25) on `entity` is in EMF's hands: offered, not removed, not dropped. After a drop
     * offer again: your own dropped offer is taken back when its parts are still EMF's, without being refused for "the
     * game's own offer". Any thread. */
    bool (*offer_active)(EmfWorld* world, uint32_t entity);

    /* ---- v27: mod messages (EmfNetChannel above; reference.md "v27") ---- */
    /* A channel of your mod; false (logged) for a bad definition or a name that is taken. Any thread. */
    bool (*net_channel)(const EmfNetChannel* def);
    /* A player's game -> the server. True: on its way. False (logged once per reason): no server has welcomed this game
     * yet, too large, over the rate, a channel that does not go to the server. Any thread. */
    bool (*net_to_server)(const char* channel, const void* data, uint32_t size);
    /* The server -> one player's game (`player`: its entity in the server world). False when that game has not said
     * hello with this channel. From a world callback on the server world. */
    bool (*net_to_player)(EmfWorld* world, uint32_t player, const char* channel, const void* data, uint32_t size);
    /* The server -> every player whose game has the channel (the host's own game included): how many. */
    uint32_t (*net_to_players)(EmfWorld* world, const char* channel, const void* data, uint32_t size);
    /* The channel version a player's game has (server: world, player), or the server's (a player's game: NULL, 0); 0 =
     * none. */
    uint32_t (*net_peer)(EmfWorld* world, uint32_t player, const char* channel);

    /* ---- v28: player events (EmfPlayerEvent above; reference.md "v28") ---- */
    /* `cb` for each player who joins, leaves or dies on the server world (the host's game or a dedicated server; its
     * thread, after its step). False for a null cb. "Died" needs the call before the first world starts (the game's
     * events are wired then, as on_event's); later only joined and left come (logged). Any thread. */
    bool (*on_player)(EmfPlayerCallback cb, void* user);

    /* ---- v29: the game's item names and language (the game exe only: the dedicated server has no text) ---- */
    /* The item's name as the game shows it, in the player's language. flags 0: for EMF's screens (windows, wheels, HUD
     * lines, the console), English where they cannot draw the language's script (Japanese, Korean, Chinese, Thai);
     * EMF_ITEM_NAME_GAME_TEXT: the player's language always (for the game's own chat). Copies at most size-1 bytes and a
     * NUL into buf (UTF-8, markup removed); returns the name's full length in bytes, 0 when there is none (the dedicated
     * server, before the game's text is known, an item without a name, an unknown item): use the debug name then. Any
     * thread but the render thread. */
    uint32_t (*item_name)(EmfWorld* world, uint32_t item_id, uint32_t flags, char* buf, uint32_t size);
    /* The language the game shows text in: a keen::LanguageId (1 En_Us, 3 De_De, 11 Ja_Jp, ...; a language without a
     * table reports En_Us), 0 on the dedicated server or before the game's text is known. code_out (may be NULL) gets
     * "en_us", "de_de", ... ("" for 0). */
    uint32_t (*game_language)(char* code_out, uint32_t size);

    /* ---- v30: character names and chat lines without a speaker ---- */
    /* The player's character name (the one the game's Social tab shows), UTF-8, cut to size-1 bytes and NUL-terminated.
     * It may already be there at the join (from the character's last visit to this world) or come when the player's game
     * sends it, a few seconds later, and it can change during a session: read it when you need it, never cache it. It is
     * display text a player's game set, not an identity: key data by the account. A player's grave carries the name too.
     * False (buf = "") when it is not known, for an entity without one, or on a client world. Server world (a game you
     * host or the dedicated server; players' chat need not be on); same thread rules as get_component. */
    bool (*player_character_name)(EmfWorld* world, uint32_t player, char* buf, uint32_t size);
    /* chat_tell with flags: EMF_CHAT_SYSTEM shows the line without a speaker's name (the game's session-info line, as for
     * a player joining), still only to that player, also on a server with text chat off; a prefix in the text ("[Raid] ")
     * is the only label there is. 0: chat_tell. Other bits: refused (logged). Players without EMF see it the same way. */
    bool (*chat_tell_ex)(EmfWorld* world, uint32_t player, const char* text, uint32_t flags);
    /* chat_broadcast with flags, as chat_tell_ex. */
    bool (*chat_broadcast_ex)(const char* text, uint32_t flags);

    /* ---- v31: hits (EmfHit, EmfHitRule; server world only: the host's game or the dedicated server) ---- */
    /* `cb` after the server world's step, on its thread, once for each hit the server applied to an entity, from the
     * game's HitEvent: the final numbers, EMF_HIT_KILLED on the killing blow, health_after = the entity's health then
     * (health_before -1). Not for terrain or building hits; a joined player's game sees none. Works on any game build
     * where on_event works. False for a null cb or 16 already (logged). */
    bool (*on_hit)(EmfHitCallback cb, void* user);
    /* `cb` inline for each landing entity hit on the server world, after the rules and before the game applies it:
     * change amount, stamina, reaction or cancel. It runs on the game's impact thread inside the world's step: be fast,
     * change only the hit, read only the hit's entities (their Health, Stamina, Mana); no spawn, kill or component
     * writes; damage() only queues. Once per hit, also for a target of several parts. A faulting callback is switched
     * off. False when the hook is not available on this game build, for a null cb or 16 already (logged). */
    bool (*on_hit_before)(EmfHitBeforeCallback cb, void* user);
    /* A rule the core applies to every landing entity hit before the on_hit_before callbacks, in id order, without
     * calling the mod (EmfHitRule; emf::hit_rule() in C++ fills the defaults). Its id, or 0 when refused (logged: a bad
     * rule, 64 rules already, the hook not available on this game build). Any thread; it counts from the next hit. */
    uint32_t (*hit_rule_add)(const EmfHitRule* rule);
    bool (*hit_rule_remove)(uint32_t id);  /* false: no rule with that id */

    /* ---- v32: buffs (EmfBuffType, EmfBuff, EmfBuffEvent) ---- */
    /* The game's buff types in `world` (any world kind): the number there is; fills `out` up to `cap` (out may be
     * NULL to count). 0 when the world's buff registry cannot be read. Where no program runs (on_tick, commands). */
    uint32_t (*buff_types)(EmfWorld* world, EmfBuffType* out, uint32_t cap);
    bool (*buff_type)(EmfWorld* world, uint32_t id, EmfBuffType* out);  /* false: no such type */
    /* A type's name (which 0) or description (which 1) in the player's language, NUL-terminated (cut to fit). The
     * game's text: the game only, not the dedicated server (false there, for no text or a 0 tag). Not on the render
     * thread. */
    bool (*buff_text)(uint32_t id, uint32_t which, char* buf, uint32_t cap);
    /* An entity's active buffs on any world (server or client: they are replicated), in slot order: the number there
     * is; fills `out` up to `cap`. Where no program runs. */
    uint32_t (*buffs_of)(EmfWorld* world, uint32_t entity, EmfBuff* out, uint32_t cap);
    /* Gives `target` a buff on the server world (the host's game or the dedicated server) through the game's own buff
     * code in the next step: its slot, giving again (timer, replace or refused), faction exclusions, effects, HUD icon,
     * saving. duration_ns 0 = the type's default; source 0 = the target (damage over time scales from the source's
     * stats: give a creature as source); item: an item type id (find_item; not an item entity), 0 = none (food types
     * take their stats and time from the eaten item); an unknown item id is refused.
     * `done` (may be NULL) after that step on the server world's thread: APPLIED or REFUSED with why. False when
     * refused at once (logged): not the server world, an unknown type, 64 waiting, not available on this game build. */
    bool (*buff_apply)(EmfWorld* world, uint32_t target, uint32_t type, int64_t duration_ns, uint32_t source,
                       uint32_t item, EmfBuffCallback done, void* user);
    /* Removes `target`'s buff of that type the game's way (cleansed: its effects stop, it is gone after the next
     * step). `done` as buff_apply: REMOVED, or REFUSED ("not there"). */
    bool (*buff_remove)(EmfWorld* world, uint32_t target, uint32_t type, EmfBuffCallback done, void* user);
    /* `cb` after each server step on its thread for every change of a player's buffs, whoever made it: APPLIED,
     * REFRESHED, REMOVED (not REFUSED). From a comparison with the step before (the first sight of a player says
     * nothing). False for a null cb or 16 already. */
    bool (*on_buff)(EmfBuffCallback cb, void* user);

    /* ---- v33: markers (EmfMarker, EmfMapMarkerType) ---- */
    /* Sets (or updates) the calling mod's marker `m->id` on this game: over the 3D view (EMF_MARKER_WORLD) and/or on
     * the game's world map (EMF_MARKER_GAME_MAP). Any thread. False (logged once per kind) for a bad id or numbers,
     * the mod's 257th marker or the game's 1025th, or where no game draws (the dedicated server). */
    bool (*marker_set)(const EmfMarker* m);
    bool (*marker_remove)(const char* id);  /* false: the mod has no such marker */
    uint32_t (*marker_clear)(void);         /* the mod's own markers on this game; how many were removed */
    /* The game's map icon types for EmfMarker.game_icon that this world's own map entries used so far (id only: the
     * name is empty): the number there is; fills `out` up to `cap` (may be NULL to count). 0 before the map list was
     * seen, on the dedicated server, or where the game's list cannot be read. */
    uint32_t (*marker_types)(EmfMapMarkerType* out, uint32_t cap);
    /* A world point in display pixels with the camera of the newest frame: false when it is behind the camera or no
     * frame is known yet (a menu, a loading screen, the dedicated server). Any thread. */
    bool (*world_to_screen)(double x, double y, double z, float* px, float* py);
    /* Server (the host's game or the dedicated server): sends markers to one player's game (player = their entity in
     * the server world) or to every player (0), as if that game's mod had set them; a followed entity's position is
     * refreshed while it is too far for that game to know. How many players it went to (players without EMF get
     * nothing). At most 256 per call. */
    uint32_t (*marker_send)(uint32_t player, const EmfMarker* m, uint32_t count);
    uint32_t (*marker_unsend)(uint32_t player, const char* id);  /* removes a sent marker from that game (0 = all) */

    /* ---- v34: the map screen's mouse, map actions, the ground (EmfMapClick) ---- */
    /* The world x, z under the mouse while the game's map is open: false when it is not, or the mouse is off the map. */
    bool (*map_cursor)(double* x, double* z);
    /* `cb` for each click on the map screen of this game (the game gets it too), on the game window's thread (its
     * window procedure: keep it short, do not call the game). False for a null
     * cb or 16 already. */
    bool (*on_map_click)(EmfMapClickCallback cb, void* user);
    /* Server end (the host's game or the dedicated server): a map action players do by holding Shift and the left mouse
     * button on their map for hold_ms (0 = 1000; a ring fills at the mouse; the game does not get that click). EMF on
     * each player's game does the gesture and sends the spot; `cb` gets the player and x, z on the server world's
     * thread. Players need EMF on their game. An id again replaces it; a null cb removes the caller's action with that id
     * (false: it had none). False: a bad id (1-48 of A-Z a-z 0-9 _ . - :), 16 actions already, or no mod messages on
     * this build. */
    bool (*map_action)(const char* id, const char* label, uint32_t hold_ms, EmfMapActionCallback cb, void* user);
    /* Server world: the ground under (x, z), found with the game's voxel ray after the next steps (the place may need
     * loading: up to a few seconds); `cb` once with the answer. False when refused at once: not the server world, x, z
     * outside the world, 64 waiting, or not available on this game build. */
    bool (*ground_at)(EmfWorld* world, double x, double z, EmfGroundCallback cb, void* user);

    /* ---- v35: a minimap's parts (EmfPlayerView, EmfGameMarker, EmfCanvas) ---- */
    /* The local player's position, heading and camera yaw, and the map's size: false on the dedicated server, at the
     * main menu and before the player is known. Set out->size to sizeof(EmfPlayerView) first. Any thread. */
    bool (*player_view)(EmfPlayerView* out);
    /* The fog of war at (x, z) on this game's map: 0 unexplored .. 1 explored. False without it (the dedicated server,
     * before a world, or the first moment after a mod asks: EMF reads it while a canvas exists or a mod asked in the
     * last 10 s). Any thread (a copy refreshed twice a second). */
    bool (*fog_at)(double x, double z, float* explored);
    /* The game's own map markers in this game's world (altars, shrines, places found), as its map list had them last:
     * how many there are; fills `out` up to `cap` (may be NULL to count). At most 512. */
    uint32_t (*map_markers)(EmfGameMarker* out, uint32_t cap);
    /* A canvas: a drawing panel of the calling mod, drawn by EMF's overlay over the game's HUD (hidden where the
     * markers hide: the map, menus, the console). An id again changes it; a NULL c removes it. False: a bad id (1-48 of
     * A-Z a-z 0-9 _ . - :), bad numbers, or 32 canvases already. */
    bool (*canvas_set)(const char* id, const EmfCanvas* c);
    /* Its drawing, replacing the last list (EMF keeps it and draws it every frame). At most 2048 commands (more are
     * left out); bad numbers leave a command out. False for an unknown canvas. Any thread. */
    bool (*canvas_draw)(const char* id, const EmfCanvasCmd* cmds, uint32_t count);

    /* ---- v36: removing a placed object like a pick-up ---- */
    /* Removes req->entity, an object a player placed (it has a UsedItem), with the same engine call as destroy, and
     * gives its item back the way the game's pick-up does: {UsedItem item, 1}, only when placing that item used it up.
     * req->refund: EMF_REFUND_NONE, _DROP (a loose pickup at the object) or _PLAYER (put_items to req->player; when
     * nothing fit, a drop at the object). Refused, nothing changed, req->why says why (and the log): a player, a Flame
     * Altar (even with force: use destroy if you mean it), an object no player placed (use destroy), one holding
     * items in its own or linked inventories unless EMF_REMOVE_FORCE (the items drop at its spot), a refund player who is not
     * a player, unknown refund or flags. Like destroy it plays no pick-up sound and sends no BuildingPickUpEvent.
     * req->refunded gets the item id given back (0: none; with _PLAYER the inventory's answer comes after the step).
     * Same calling rules as destroy (SERVER world, inside a world callback on it). */
    bool (*remove_building)(EmfWorld* world, EmfRemoveBuilding* req);

    /* ---- v37: paths on the game's own path grid ---- */
    /* Asks the game for a path from q->from to q->to (it moves nothing). The answer comes once, after a server step
     * or more: cb(world, id, result, points, user) on the server world's thread (components, spawn allowed there);
     * points is valid during the call only. Check result->reaches: with q->closest the game also answers FOUND for a
     * path that ends at the nearest reachable point (its own march does that and stops at walls). Returns the query
     * id, or 0 (logged) when refused: not the server world (joined games have no path grid), bad numbers, from and to
     * more than 200 m apart (the game does not search farther), no owner (q->owner 0 and no player), 32 queries of
     * EMF already open, or the feature is off on this game build. The grid exists where the world is loaded (near
     * players); a query elsewhere ends NO_START, NO_TARGET or OTHER (no answer in 10 s). Each query is a search in the
     * game's own jobs, shared with every walking creature: do not ask again every step (the game's own enemies ask
     * at most every 0.5 s). Any thread. */
    uint32_t (*path_find)(EmfWorld* world, const EmfPathQuery* q, EmfPathCallback cb, void* user);
    /* Ends a query: its callback still comes, with EMF_PATH_CANCELLED. False for an unknown or answered id. */
    bool (*path_cancel)(uint32_t id);

    /* ---- v38: doors and gates ---- */
    /* A door's state: EMF_DOOR_NONE (not a door, or not the server world), _CLOSED or _OPEN, with the flags
     * EMF_DOOR_LOCKED and EMF_DOOR_LOGIC. A door is an entity with ServerToggleState and DirectionalInteraction (player
     * doors and gates). Inside a world callback on the server world (host or dedicated server). */
    uint32_t (*door_state)(EmfWorld* world, uint32_t entity);
    /* Opens (open 1) or closes (open 0) it at the next server step: players see it swing, the game's collider and path
     * grid follow (enemies' paths change). side: 0 = the game's default (back), 1 front, 2 back. False (logged) when
     * refused: not the server world, not a door, a switch drives it (EMF_DOOR_LOGIC: the logic system owns it), it is
     * already so, a bad side, or the feature is off on this game build. No open/close sound is played. Same calling
     * rule as door_state. */
    bool (*door_set)(EmfWorld* world, uint32_t entity, uint32_t open, uint32_t side);
    /* Locks (1) or unlocks (0) it from the next step: no E prompt for any player while locked (EMF keeps it so each
     * step), and the door keeps its state (a player's toggle already under way when the lock came is undone). EMF
     * keeps the lock with the world (from 0.6.8: by the door's place, in emf\config\emf_doors\<slot>.txt): after a
     * load the door at that place is locked again. door_set still works on a locked door and changes the state it
     * keeps. Same rules. */
    bool (*door_lock)(EmfWorld* world, uint32_t entity, uint32_t locked);

    /* ---- v39: enemy orders (EmfEnemyOrder) ---- */
    /* Sends order->enemy at order->target: written at once and after every server step until it ends (its time, the
     * target gone or dead, the enemy gone, cancelled, a new order on the same enemy, the world closing); then the game
     * picks for it again. Returns the order's id (from 1), or 0 with order->why: not a server world or not inside a
     * world callback on it, an enemy without an enemy AI, a player, a player's ally without EMF_ORDER_ALLIES, no such
     * target, a dead target, bad numbers, more than 256 orders, damage while hits are off on this game build. */
    uint32_t (*enemy_order)(EmfWorld* world, EmfEnemyOrder* order);
    /* Ends an order (at once inside a server world callback, else at the next step). False: no such order. */
    bool (*enemy_order_cancel)(uint32_t id);
    /* What `enemy` goes for now (its pick: SelectedTargets slot 0), 0 = nothing (or not a creature). */
    uint32_t (*enemy_target)(EmfWorld* world, uint32_t enemy);

    /* ---- v40: move orders (EmfMoveOrder) ---- */
    /* Walks order->enemy to its points (see EMF_MOVE_*). Returns the move's id (from 1), or 0 with order->why: not a
     * server world or not inside a world callback on it, not a creature with the enemy AI, a player, a boss, a player's
     * ally without EMF_MOVE_ALLIES, bad points or numbers, more than 256 moves, no waypoint (spawning is off on this
     * game build). Once it holds, a move or enemy order already on the creature ends (cancelled). cb (may be NULL) is
     * called once when the move ends, after the move is gone (it may start another): after a server step on the server
     * world's thread; inside enemy_move_cancel, or inside the enemy_move or enemy_order that replaced it; or with
     * world NULL when the world closes. */
    uint32_t (*enemy_move)(EmfWorld* world, EmfMoveOrder* order, EmfMoveCallback cb, void* user);
    /* Ends a move (its callback gets EMF_MOVE_CANCELLED): at once inside a world callback on the server world, else
     * at the next step. False: no such move. */
    bool (*enemy_move_cancel)(uint32_t id);
    /* The id of `enemy`'s move, 0 = it is not moving. */
    uint32_t (*enemy_moving)(EmfWorld* world, uint32_t enemy);
} EmfApi;

#define EMF_ITEM_NAME_GAME_TEXT 1u /* v29 item_name: the player's language even where EMF's screens cannot draw it */
#define EMF_CHAT_SYSTEM 1u         /* v30 chat_tell_ex / chat_broadcast_ex: a line without a speaker's name */

typedef bool (*EmfModLoadFn)(const EmfApi* api);

#ifdef __cplusplus
}
#endif

#endif /* EMF_API_H */
