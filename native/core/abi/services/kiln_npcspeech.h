/* SPDX-License-Identifier: MIT
 * kiln.npcspeech v1: Kiln's lines in the game's own speech bubble over a friendly NPC (villagers, craftspeople, their
 * assistants: every NPC with an NpcDialog). Game client only: the bubble, its text and the line's voice-over are client
 * data (docs/re/npc-speech.md). The host decides and sends the decision to Kiln clients (Kiln.Npcs, channel
 * kiln:npcspeech); this table is what a client applies:
 *   - an override per NPC: Suppress (no greeting bubble, no voice-over of a greeting) or Replace (the bubble shows one of
 *     the given loca tags instead of the game's pick, no voice-over);
 *   - a Say per NPC: one loca tag in the bubble for a time, shown even when the NPC is not the player's target.
 * Texts are loca tags that kiln.content's loca hooks answer: text() registers one (per language). Hooks go in with the
 * first override or Say and stay in (they answer nothing without one). World ends: the caller clears (clear_all).
 * Since 0.2.0-dev.32 (appended after status; the table's version stays 1): how a Replace picks at each greeting, the
 * dialogue menu's farewell and own line (kinds), the host's picks (next), and what the game reported (events).
 * Evidence: docs/re/npc-speech.md; usage: docs/kiln/services/npc-speech.md. */
#ifndef KILN_NPCSPEECH_H
#define KILN_NPCSPEECH_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_NPCSPEECH_SERVICE "kiln.npcspeech"
#define KILN_NPCSPEECH_VERSION 1u

#define KILN_SPEECH_GAME 0u     /* the game's own lines (clears the override) */
#define KILN_SPEECH_SUPPRESS 1u /* no greeting bubble and no greeting voice-over */
#define KILN_SPEECH_REPLACE 2u  /* one of the given tags instead of the game's line; no voice-over */

#define KILN_SPEECH_MAX_LINES 32u
#define KILN_SPEECH_MAX_SAY_MS 60000u

typedef struct KilnNpcSpeechStatus {
    uint32_t size;        /* in: sizeof(KilnNpcSpeechStatus) */
    uint32_t resolved;    /* bit per part found on this build: 1 NpcBubble, 2 BubbleDraw, 4 the record layout (voice), 8 texts (kiln.content) */
    uint32_t installed;   /* 1 NpcBubble, 2 BubbleDraw: hooks in now */
    uint32_t overrides;   /* NPCs with an override now */
    uint32_t says;        /* NPCs with a running Say now */
    uint32_t texts;       /* tags registered through text() */
    uint64_t bubbles;     /* bubble draws of NPCs with an override or Say */
    uint64_t changed;     /* of those, bubbles Kiln changed (text or visibility) */
    uint64_t muted;       /* voice-over entries Kiln muted (put back when the override ends) */
} KilnNpcSpeechStatus;

/* ---- since 0.2.0-dev.32 ---- */
#define KILN_SPEECH_KIND_GREETING 0u /* the speech bubble's greeting (set()'s override) */
#define KILN_SPEECH_KIND_FAREWELL 1u /* the dialogue menu's farewell when it closes (feature npcspeech.farewell) */
#define KILN_SPEECH_KIND_MENU 2u     /* the dialogue menu's own line when it opens (feature npcspeech.menu_line) */

#define KILN_SPEECH_PICK_CHANGE 0u /* a new random line when the game picks a new one: set()'s rule since 31.16 */
#define KILN_SPEECH_PICK_RANDOM 1u /* a new random line at each greeting (farewell, menu open); not the same twice in a row */
#define KILN_SPEECH_PICK_ORDER 2u  /* the lines in turn, one per greeting, from seed % n */
#define KILN_SPEECH_PICK_SEEDED 3u /* the line of a pure mix of seed, the NPC and the open's number (0, 1, ...): the same everywhere */
#define KILN_SPEECH_PICK_HOST 4u   /* the line next() gave last (KILN_SPEECH_LINE_GAME: the game's own); none yet: as RANDOM */

#define KILN_SPEECH_LINE_GAME (-1) /* next(): the game's own line this time (the menu's line: none) */

#define KILN_SPEECH_EVENT_OPEN 0u  /* a HOST-picked line showed: kind, line (its index, -1 the game's), value = opens so far */
#define KILN_SPEECH_EVENT_TOPIC 1u /* a Kiln topic was picked in the dialogue menu: value = the topic's id */

/* A greeting's "open": the bubble shows after at least this long hidden (or for the first time), or the game picks a new
 * line (a dialogue closed). */
#define KILN_SPEECH_REOPEN_MS 1000u

#define KILN_SPEECH_MAX_TOPICS 16u /* Kiln topics per NPC */

/* One topic of Kiln's on an NPC's talk page (feature npcspeech.topics), after the game's own. */
typedef struct KilnSpeechTopic {
    uint32_t id;    /* its line id: not 0 or 1 (the game's farewell and missing-requirement ids), unique per NPC */
    uint32_t label; /* its text in the topic list (a tag) */
    uint32_t reply; /* the NPC's answer in the speech box when picked (a tag); 0: none (the pick is reported, no box) */
    uint8_t hint;   /* the list's style: 0 None, 1 Hint, 2 Attention, 3 MainQuest */
    uint8_t close;  /* 1: the dialogue ends after the reply (the game's PlayOutro) */
    uint8_t reserved[2];
} KilnSpeechTopic;

typedef struct KilnSpeechEvent {
    uint32_t npc;
    uint8_t type; /* KILN_SPEECH_EVENT_* */
    uint8_t kind; /* KILN_SPEECH_KIND_* (OPEN) */
    uint16_t reserved;
    int32_t line;   /* OPEN: the index shown, -1 the game's line */
    uint32_t value; /* OPEN: opens of that kind of that NPC so far, this one included; TOPIC: the topic's id */
} KilnSpeechEvent;

typedef struct KilnNpcDialogStatus {
    uint32_t size;      /* in: sizeof(KilnNpcDialogStatus) */
    uint32_t resolved;  /* bit per part found on this build: 1 npcspeech.farewell, 2 npcspeech.menu_line, 4 npcspeech.topics */
    uint32_t installed; /* 1 DialogUpdate, 2 BuildTopics, 4 SayLine: hooks in now */
    uint32_t lines;     /* NPCs with a farewell or menu line override now */
    uint32_t topics;    /* NPCs with Kiln topics now */
    uint32_t queued;    /* events waiting for events() */
    uint64_t opens;     /* greetings, farewells and menu lines Kiln picked a line for */
    uint64_t farewells; /* farewells Kiln showed */
    uint64_t menu_lines; /* menu lines Kiln showed (menu_say too) */
    uint64_t topic_rows; /* topics Kiln added to talk pages */
    uint64_t picks;     /* Kiln topics picked */
    uint64_t dropped;   /* events dropped: nobody read them (512 wait at most) */
} KilnNpcDialogStatus;

typedef struct KilnNpcSpeechApi {
    KilnServiceHeader header; /* { sizeof(KilnNpcSpeechApi), KILN_NPCSPEECH_VERSION } */
    /* The effective override of one NPC (the caller resolves owners and priorities). mode KILN_SPEECH_*; tags/n the lines
     * of Replace (1..KILN_SPEECH_MAX_LINES, nonzero), ignored otherwise. Any thread. KILN_ERR_UNAVAILABLE: the bubble
     * hooks are not on this build (or not on a dedicated server), or (Replace) kiln.content's loca hooks are not here,
     * so no tag would show a text; KILN_ERR_INVALID_ARGUMENT: a bad mode, entity 0, no or too many tags. */
    KilnResult(KILN_CALL* set)(KilnEntity npc, uint32_t mode, const uint32_t* tags, uint32_t n);
    /* One tag in the NPC's bubble for ms (1..KILN_SPEECH_MAX_SAY_MS); tag 0 or ms 0 ends a running Say. A new Say replaces
     * the old one. Any thread. Errors as set() (KILN_ERR_UNAVAILABLE also without kiln.content's loca hooks). */
    KilnResult(KILN_CALL* say)(KilnEntity npc, uint32_t tag, uint32_t ms);
    /* The texts of a loca tag: "text" or {"en": "...", "de": "..."} ("*" = every language), UTF-8 JSON. The same tag and
     * texts again change nothing. Any thread. KILN_ERR_UNAVAILABLE: kiln.content's loca hooks are not here. */
    KilnResult(KILN_CALL* text)(uint32_t tag, const char* json, size_t len);
    /* Ends every override and Say (a world's end, a reset). Voice-overs come back the next time their NPC's bubble runs.
     * Any thread. */
    KilnResult(KILN_CALL* clear_all)(void);
    /* Any thread. */
    KilnResult(KILN_CALL* status)(KilnNpcSpeechStatus* out);

    /* ---- since 0.2.0-dev.32 (appended; check header.size >= offsetof(KilnNpcSpeechApi, dialog_status) + sizeof(void*)
     * before calling these: a 31.16-31.21 table is 48 bytes) ---- */
    /* The effective override of one NPC's line of `kind` (KILN_SPEECH_KIND_*) and how it picks (KILN_SPEECH_PICK_*, seed).
     * GREETING: as set(), with the pick (set() is GREETING with PICK_CHANGE); FAREWELL and MENU take KILN_SPEECH_GAME or
     * KILN_SPEECH_REPLACE only. The same mode, tags and pick again keep the NPC's open count (a rotation goes on).
     * Any thread. KILN_ERR_UNAVAILABLE: that kind's feature is not on this build (or kiln.content's loca hooks are not
     * here for a Replace); KILN_ERR_INVALID_ARGUMENT: entity 0, a bad kind, mode or pick, no or too many tags, a zero tag. */
    KilnResult(KILN_CALL* set_lines)(KilnEntity npc, uint32_t kind, uint32_t mode, const uint32_t* tags, uint32_t n, uint32_t pick,
                                     uint32_t seed);
    /* PICK_HOST lines: the index (0..n-1) the next open of that kind shows, or KILN_SPEECH_LINE_GAME; it stays until the
     * next call. Any thread. KILN_ERR_INVALID_ARGUMENT: no PICK_HOST lines of that kind for the NPC, or an index past them. */
    KilnResult(KILN_CALL* next)(KilnEntity npc, uint32_t kind, int32_t line);
    /* Up to max events since the last call, oldest first (*n of them). The queue keeps 512; older ones are dropped
     * (KilnNpcDialogStatus.dropped). Any thread. */
    KilnResult(KILN_CALL* events)(KilnSpeechEvent* out, uint32_t max, uint32_t* n);
    /* Any thread. */
    KilnResult(KILN_CALL* dialog_status)(KilnNpcDialogStatus* out);
    /* One text (a tag) in the speech box of the NPC's dialogue menu on this game: now if it is open, else when it opens
     * within 5 s; once. Tag 0 cancels a waiting one. For a host's reply to one player (its client calls this). Any
     * thread. KILN_ERR_UNAVAILABLE: npcspeech.menu_line is not on this build (or kiln.content's loca hooks are not here);
     * KILN_ERR_INVALID_ARGUMENT: entity 0. */
    KilnResult(KILN_CALL* menu_say)(KilnEntity npc, uint32_t tag);
    /* The NPC's Kiln topics (0..KILN_SPEECH_MAX_TOPICS; replaces the last call's, n 0 clears): rows after the game's own
     * on its talk page (a villager with none gets one), each pick an event (KILN_SPEECH_EVENT_TOPIC). Any thread.
     * KILN_ERR_UNAVAILABLE: npcspeech.topics is not on this build (or kiln.content's loca hooks are not here);
     * KILN_ERR_INVALID_ARGUMENT: entity 0, too many, an id 0 or 1 or twice, no label, a bad hint. */
    KilnResult(KILN_CALL* topics)(KilnEntity npc, const KilnSpeechTopic* topics, uint32_t n);
} KilnNpcSpeechApi;

#ifdef __cplusplus
}
#endif
#ifdef __cplusplus
static_assert(sizeof(KilnSpeechEvent) == 16, "KilnSpeechEvent is 16 bytes");
static_assert(sizeof(KilnSpeechTopic) == 16, "KilnSpeechTopic is 16 bytes");
static_assert(offsetof(KilnNpcSpeechApi, topics) == 88 && sizeof(KilnNpcSpeechApi) == 96, "topics is appended at 88");
static_assert(sizeof(KilnNpcDialogStatus) == 72, "KilnNpcDialogStatus is 72 bytes");
static_assert(offsetof(KilnNpcSpeechApi, status) == 40, "31.16's table ends with status at 40 (48 bytes)");
static_assert(offsetof(KilnNpcSpeechApi, set_lines) == 48, "set_lines is appended at 48");
static_assert(offsetof(KilnNpcSpeechApi, next) == 56 && offsetof(KilnNpcSpeechApi, events) == 64 &&
                  offsetof(KilnNpcSpeechApi, dialog_status) == 72 && offsetof(KilnNpcSpeechApi, menu_say) == 80,
              "next, events, dialog_status and menu_say follow");
#else
_Static_assert(sizeof(KilnSpeechEvent) == 16, "KilnSpeechEvent is 16 bytes");
_Static_assert(sizeof(KilnSpeechTopic) == 16, "KilnSpeechTopic is 16 bytes");
_Static_assert(offsetof(KilnNpcSpeechApi, topics) == 88 && sizeof(KilnNpcSpeechApi) == 96, "topics is appended at 88");
_Static_assert(sizeof(KilnNpcDialogStatus) == 72, "KilnNpcDialogStatus is 72 bytes");
_Static_assert(offsetof(KilnNpcSpeechApi, status) == 40, "31.16's table ends with status at 40 (48 bytes)");
_Static_assert(offsetof(KilnNpcSpeechApi, set_lines) == 48, "set_lines is appended at 48");
_Static_assert(offsetof(KilnNpcSpeechApi, next) == 56 && offsetof(KilnNpcSpeechApi, events) == 64 &&
                   offsetof(KilnNpcSpeechApi, dialog_status) == 72 && offsetof(KilnNpcSpeechApi, menu_say) == 80,
               "next, events, dialog_status and menu_say follow");
#endif
#endif
