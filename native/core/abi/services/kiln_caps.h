/* SPDX-License-Identifier: MIT
 * kiln.caps v1 (in-core service): the capability registry. One table that says, by name, what works in this process
 * on this game build and side, and why something does not.
 *
 * A capability is a name with a state (available, unavailable, pending), a reason code, a reason text and a source.
 * The core fills a row for every sigdb feature and for every service; services, plugins and the managed layer add
 * their own with report(). The managed side reads it as Kiln.Capabilities (Is, Get, Why, All); the launcher and the
 * docs tools read <game>/kiln/state/capabilities.json (schema/state-capabilities.schema.json), which the core alone
 * writes: once when boot completes, then after a change, at most once a second.
 *
 * This service answers in safe idle too (like kiln.profile): there every feature row says "policy" and why.
 *
 * Names (case-sensitive; 1 to 96 bytes of [A-Za-z0-9._/-]):
 *   - a sigdb feature keeps its sigdb name: "items.fuel", "net.kick", "tick", "world.lifecycle";
 *   - "kiln.<service>" says that the service table of that name is here: "kiln.items", "kiln.hooks";
 *   - "<service>.<thing>" is a row a service (native or managed) registers for one of its abilities that is not a
 *     sigdb feature: "items.moves", "anim.player_states". Use the sigdb feature's name where one exists, so there is
 *     one row per ability and not two;
 *   - anything else a plugin or mod reports starts with its own id: "<id>.<thing>".
 *
 * Reports and the row that is shown. Each owner holds at most one report per name; the core's own report (owner 0)
 * comes from sigdb and the service lists. The row a reader sees is the worst report for that name: unavailable beats
 * pending, pending beats available; among equals the core's report wins, then the oldest. So a service can say that
 * "items.fuel" is off although its symbols resolved (its hook failed), and nobody can report a capability available
 * whose symbols are missing. When an owner is revoked its reports go, and each row shows what is left; a row with no
 * report left is removed.
 *
 * Static and session rows. Most rows are fixed when boot completes. A row that depends on the session (no world
 * yet, world data not loaded) is reported KILN_CAP_PENDING until it is decided and may go back to pending when the
 * world stops. A name that was ever reported pending carries KILN_CAP_FLAG_SESSION from then on. A mod manifest may
 * only require rows without that flag ("features" in kiln.mod.json): the loader refuses a session row as a
 * requirement and says so. Report a session row as pending before the mods load (kiln_plugin_main, on_ready, the
 * managed service's start), not only once a world is there.
 * A name the core itself reports (a sigdb feature, "kiln.<service>") is static: report() refuses PENDING for it. A
 * service may still report such a name unavailable (its hook failed at boot). The part of an ability that depends on
 * the session gets a row of its own ("items.fuel" stays what this build can do; "items.fuel_program" could say
 * whether it runs right now), so a manifest can go on requiring the static one.
 *
 * Threads: every function may be called on any thread. Strings in a KilnCapRow filled by at() or find() point into
 * storage of the calling thread and stay valid until that thread's next call of at() or find(); copy what you keep.
 * A subscriber runs on the thread that caused the change (the reporter's, the thread that revoked an owner, a core
 * thread), after the registry's lock was released; the row it gets is valid for the call only. Two changes close
 * together may arrive in either order: generation(), at() and find() say what is true now. Do not block in it.
 */
#ifndef KILN_CAPS_H
#define KILN_CAPS_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_CAPS_SERVICE "kiln.caps"
#define KILN_CAPS_VERSION 1u

#define KILN_CAP_NAME_MAX 96u  /* bytes in a name */
#define KILN_CAP_TEXT_MAX 512u /* bytes in a reason text; report() cuts a longer one at a UTF-8 boundary */
#define KILN_CAP_OWNER_MAX 256u /* reports one owner may hold (the core's own are not counted) */

/* KilnCapRow.state. capabilities.json: "unavailable", "available", "pending". */
enum {
    KILN_CAP_UNAVAILABLE = 0, /* does not work here; reason and text say why */
    KILN_CAP_AVAILABLE = 1,   /* works; reason is NONE, text may carry a note ("v3") */
    KILN_CAP_PENDING = 2,     /* depends on the session and is not decided yet; reason says what it waits for */
};

/* KilnCapRow.reason. capabilities.json writes the name in the comment. New codes are appended; a reader treats a code
 * it does not know as "unavailable for another reason" and shows the text. */
enum {
    KILN_CAP_REASON_NONE = 0,              /* "none": only with KILN_CAP_AVAILABLE */
    KILN_CAP_REASON_SYMBOL_MISSING = 1,    /* "symbol-missing": a symbol or constant of the feature did not resolve on
                                              this game build. Text: "symbol <name>: <sigdb's reason>" or
                                              "constant <name>: <sigdb's reason>", the first one that failed */
    KILN_CAP_REASON_SERVICE_MISSING = 2,   /* "service-missing": the service is not here (its DLL is not installed, was
                                              skipped, failed to load, or its init said no) */
    KILN_CAP_REASON_SERVICE_OLDER = 3,     /* "service-older": the service is here but older than the caller needs (its
                                              version is below the minimum asked for, or its table is smaller) */
    KILN_CAP_REASON_WRONG_SIDE = 4,        /* "wrong-side": server only on a client, or client only on a server */
    KILN_CAP_REASON_NEEDS_KILN_CLIENT = 5, /* "needs-kiln-client": works only for players whose client runs Kiln */
    KILN_CAP_REASON_POLICY = 6,            /* "policy": switched off by configuration or by the build policy (safe
                                              idle on an unverified build, kiln.json, a service's own config) */
    KILN_CAP_REASON_FAULTED = 7,           /* "faulted": it was on and faulted, or its hook could not be installed */
    KILN_CAP_REASON_OFFLINE = 8,           /* "offline": no game in this process (the test kit, a tool) */
    KILN_CAP_REASON_SESSION = 9,           /* "session": waits for the session (no world yet, world data not loaded);
                                              the usual reason of a PENDING row */
    KILN_CAP_REASON_UNKNOWN = 10,          /* "unknown": no row of this name. Never stored in the registry: find()
                                              answers KILN_ERR_NOT_FOUND; the managed Capabilities.Get returns it */
};

/* KilnCapRow.flags */
enum {
    KILN_CAP_FLAG_SESSION = 1u << 0, /* the name was reported PENDING at least once: it depends on the session and a
                                        manifest may not require it. capabilities.json: "session": true */
};

/* KilnCapsChangedFn.change */
enum {
    KILN_CAP_CHANGE_SET = 1,     /* the row is new, or its state, reason, text, source or flags changed */
    KILN_CAP_CHANGE_REMOVED = 2, /* the row's last report went away; `row` holds what it said last */
};

/* One row as a reader sees it (the worst report for the name, see above). */
typedef struct KilnCapRow {
    uint32_t size;   /* set by the caller: sizeof(KilnCapRow) */
    uint32_t state;  /* KILN_CAP_* */
    uint32_t reason; /* KILN_CAP_REASON_* */
    uint32_t flags;  /* KILN_CAP_FLAG_* */
    KilnStr name;
    KilnStr text;    /* the reason in words, for a log line or the launcher; empty when there is nothing to say */
    KilnStr source;  /* who made the report that is shown: "sigdb" (a feature row), "core" (a service row the core
                        derived), else the reporting owner's name ("service:kiln-items.dll", "host", "mod:<id>") */
} KilnCapRow;

typedef void(KILN_CALL* KilnCapsChangedFn)(const KilnCapRow* row, uint32_t change, void* user);

typedef struct KilnCapsApi {
    KilnServiceHeader header; /* { sizeof(KilnCapsApi), KILN_CAPS_VERSION } */

    /* Rows, sorted by name (byte order). count() and at() are not one transaction: read generation() before and
     * after a walk and walk again when it moved. at(): KILN_ERR_NOT_FOUND past the end; KILN_ERR_INVALID_ARGUMENT
     * when out is NULL or out->size is smaller than the v1 struct. */
    uint32_t(KILN_CALL* count)(void);
    KilnResult(KILN_CALL* at)(uint32_t index, KilnCapRow* out);

    /* The row of `name`. KILN_ERR_NOT_FOUND when nobody reported it (out is left alone). */
    KilnResult(KILN_CALL* find)(KilnStr name, KilnCapRow* out);

    /* Sets `owner`'s report for `name` (replacing its earlier one). state: KILN_CAP_*; reason: KILN_CAP_REASON_*,
     * NONE exactly when state is AVAILABLE; text: optional words (cut at KILN_CAP_TEXT_MAX).
     * KILN_ERR_INVALID_ARGUMENT: a bad name, state or reason, UNKNOWN as the reason, owner 0 (the core's own), or
     * PENDING for a name the core reports itself (see "Static and session rows");
     * KILN_ERR_REVOKED: the owner is gone; KILN_ERR_UNAVAILABLE: the owner already holds KILN_CAP_OWNER_MAX reports.
     * A report that changes nothing a reader sees raises no change. */
    KilnResult(KILN_CALL* report)(KilnOwner owner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text);

    /* Calls `fn` for every change of a row a reader sees, from now on (no call for the rows that exist already).
     * Goes away with its owner, or with unsubscribe(). A subscriber that faults is removed. */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnCapsChangedFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);

    /* Counts the changes a reader can see, from 1. Equal values mean equal rows. */
    uint64_t(KILN_CALL* generation)(void);
} KilnCapsApi;

#ifdef __cplusplus
}
#endif
#endif /* KILN_CAPS_H */
