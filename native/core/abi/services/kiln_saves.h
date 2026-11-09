/* SPDX-License-Identifier: MIT
 * kiln.saves v1: world save events, the game's own "save now", and the world's identity.
 * Evidence: docs/re/saves.md. Usage: docs/kiln/services/saves.md. Managed face: managed/services/Kiln.Saves
 * (Saves, WorldData, PlayerData, EntityData).
 *
 * The game's save, as seen from Kiln (dedicated server, svn 1285601):
 *  1. requestServerSave(instance) marks a save pending: the 30 s autosave timer, the shutdown state, a player leaving
 *     the server, or Kiln's request(). Kiln hooks it and reports KILN_SAVE_EVENT_SAVING when the game accepted it.
 *  2. In the next server-instance tick (after that tick's sim step) the game snapshots the ECS world and hands the
 *     blobs to its Save2Thread. SNAPSHOT (when CAN_SNAPSHOT_STAGE) runs synchronously at the first safe Post after the
 *     request, after that last simulation's jobs and before its pending snapshot. Restage data there without repeating
 *     SAVING handlers. No later restage occurs while Save2 writes. Shutdown without another sim keeps the initial stage.
 *  3. The Save2Thread writes <saveDirectory>/world<N>.ksd (+ its backup/ copy); Kiln reports KILN_SAVE_EVENT_WRITTEN
 *     (or _FAILED) at the start of the next step. The world file's header revision is then the event's revision.
 *
 * Threads
 *  - status, request, next_event, path: any thread.
 *  - The pump (set_pump) runs on the tick thread: synchronously inside the game's save request for SAVING (after
 *    Kiln waited for the world's jobs, so game state may be read like in Post), else at the start of a step (Pre) when
 *    events are queued. SNAPSHOT runs in Post after world jobs finish. A faulting pump is removed (logged).
 *
 * Revisions: the u32 at world file +0x1C, +1 for each successful write. SAVING carries the revision the save will get
 * (current + 1); a failed write leaves the revision unchanged, so the next save carries the same number again. */
#ifndef KILN_SAVES_H
#define KILN_SAVES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SAVES_SERVICE "kiln.saves"
#define KILN_SAVES_VERSION 1u

/* KilnSaveEvent.reason / KilnSavesStatus.last_reason / request(reason) */
enum {
    KILN_SAVE_REASON_UNKNOWN = 0,     /* another caller (or another hook chained in front of Kiln's) */
    KILN_SAVE_REASON_AUTOSAVE = 1,    /* the game's timer (every 30 s of real time while the world runs) */
    KILN_SAVE_REASON_SHUTDOWN = 2,    /* the server is stopping (SIGINT, console quit) */
    KILN_SAVE_REASON_PLAYER_LEFT = 3, /* a player's session ended (the game saves when someone leaves) */
    KILN_SAVE_REASON_REQUEST = 4,     /* request() (Saves.Request, an admin "save now") */
    KILN_SAVE_REASON_BACKUP = 5,      /* request() for a backup (Saves.Backup) */
};

/* KilnSaveEvent.kind */
enum {
    KILN_SAVE_EVENT_SAVING = 1,  /* the game accepted a save request; it snapshots the world next */
    KILN_SAVE_EVENT_WRITTEN = 2, /* the world file is complete (revision = the file's new revision) */
    KILN_SAVE_EVENT_FAILED = 3,  /* the write failed (result = the game's error byte) or a request timed out */
    KILN_SAVE_EVENT_LOADED = 4,  /* the server finished loading the world save (or started a new world) */
    KILN_SAVE_EVENT_SNAPSHOT = 5, /* appended v1: first safe Post after the request, before the pending ECS snapshot; same target revision */
};

/* KilnSaveEvent.flags */
enum {
    KILN_SAVE_EVENT_SYNC = 1u << 0,      /* SAVING: delivered inside the game's request, before its snapshot */
    KILN_SAVE_EVENT_HAS_GUID = 1u << 1,  /* world_guid is valid */
    KILN_SAVE_EVENT_NEW_WORLD = 1u << 2, /* LOADED: no world file existed (a new world; its file appears at the first save) */
    KILN_SAVE_EVENT_TIMEOUT = 1u << 3,   /* FAILED: Kiln's request was not accepted within 60 s */
};

typedef struct KilnSaveEvent {
    uint32_t size;         /* sizeof(KilnSaveEvent) */
    uint32_t kind;         /* KILN_SAVE_EVENT_* */
    uint64_t seq;          /* 1, 2, 3 ... per process */
    uint32_t reason;       /* KILN_SAVE_REASON_* (of the save this event belongs to) */
    uint32_t flags;        /* KILN_SAVE_EVENT_* flags */
    uint64_t step;         /* server-world step when it happened (0: before the first step) */
    int64_t unix_ms;       /* UTC milliseconds */
    uint32_t revision;     /* SAVING: the revision this save will get; WRITTEN: the new one; LOADED: the loaded one */
    uint32_t result;       /* WRITTEN/FAILED: the game's result byte (0 = ok) */
    uint16_t slot;         /* the game's world slot id (low byte = index + 1, high byte = storage; 0 = unknown) */
    uint16_t reserved16;
    uint32_t world_unix;   /* WRITTEN/LOADED: the world file header's UTC seconds (+0x20); 0 = unknown */
    uint8_t world_guid[16]; /* the world's GUID (Windows byte order, as .NET Guid(byte[])) */
    double duration_ms;    /* WRITTEN/FAILED: accepted request -> file complete */
    uint64_t save_seq;     /* WRITTEN/FAILED: seq of the SAVING event of this save (0 = unknown) */
} KilnSaveEvent;

/* KilnSavesStatus.state */
enum {
    KILN_SAVES_STATE_NONE = 0,    /* no server world yet */
    KILN_SAVES_STATE_LOADING = 1, /* the world steps, the save is still loading */
    KILN_SAVES_STATE_RUNNING = 2, /* loaded, no save in progress */
    KILN_SAVES_STATE_SAVING = 3,  /* a save was accepted and is not written yet */
};

/* Appended v1: SNAPSHOT reaches the pump after world jobs and before the pending ECS snapshot. */
#define KILN_SAVES_CAN_SNAPSHOT_STAGE (1u << 5)

/* KilnSavesStatus.features */
enum {
    KILN_SAVES_CAN_REQUEST = 1u << 0, /* request() works (needs the game's save request + its instance) */
    KILN_SAVES_CAN_WRITE = 1u << 1,   /* WRITTEN/FAILED events (hook on the Save2Thread's world write) */
    KILN_SAVES_CAN_LOAD = 1u << 2,    /* LOADED events and the slot/GUID from the game's memory */
    KILN_SAVES_CAN_REASON = 1u << 3,  /* reasons autosave/shutdown/player-left are told apart */
    KILN_SAVES_CLIENT_HOSTED = 1u << 4, /* a game client hosting its own world: the world file lives in the client's
                                           Steam cloud (userdata/<id>/1203620/remote) or local (Saved Games\keen games\
                                           Enshrouded\default) slot folder; staged restores are refused */
};

typedef struct KilnSavesStatus {
    uint32_t size;             /* set to sizeof(KilnSavesStatus) before calling */
    uint32_t features;         /* KILN_SAVES_CAN_* */
    uint32_t state;            /* KILN_SAVES_STATE_* */
    uint32_t last_reason;      /* reason of the newest accepted save */
    uint8_t world_guid[16];
    uint32_t has_guid;         /* 1: world_guid is valid (from the game's memory, else the world file header) */
    uint32_t revision;         /* newest written (or loaded) revision */
    uint16_t slot;             /* the game's world slot id (0 = unknown) */
    uint16_t pending_request;  /* 1: request() waits for the next step (or for a running save to finish) */
    uint32_t world_unix;       /* header time of the newest written/loaded world file */
    int64_t last_written_unix_ms;  /* 0 = none this session */
    int64_t last_accepted_unix_ms; /* newest accepted request */
    double last_duration_ms;       /* accepted request -> file complete, newest save */
    int64_t autosave_interval_ns;  /* the game's autosave interval (0 = unknown) */
    uint64_t accepted;         /* save requests the game accepted this session */
    uint64_t refused;          /* requests the game refused (a save was running, or the world was not running) */
    uint64_t written;          /* successful world writes */
    uint64_t failed;           /* failed writes + timed-out requests */
    uint64_t last_seq;         /* newest event seq */
    uint64_t step;             /* current server-world step */
} KilnSavesStatus;

/* path(kind): absolute UTF-8 paths (Windows form, as the game sees them) */
enum {
    KILN_SAVES_PATH_SAVE_DIR = 0,   /* the server's saveDirectory (enshrouded_server.json; default <game>/savegame). On a
                                       game client: the slot folder of the hosted world (Steam cloud folder before one) */
    KILN_SAVES_PATH_WORLD_FILE = 1, /* <save dir>/world<N>.ksd of the running world (NOT_FOUND until known) */
    KILN_SAVES_PATH_DATA = 2,       /* <game>/kiln/data/worlds: Kiln's per-world sidecar root */
    KILN_SAVES_PATH_BACKUPS = 3,    /* <game>/kiln/backups: Saves.Backup output */
};

/* The pump: `kind` is the kind of the newest queued event (a hint; read them with next_event). Tick thread. */
typedef void(KILN_CALL* KilnSavesPumpFn)(uint32_t kind, void* user);

typedef struct KilnSavesApi {
    KilnServiceHeader header; /* { sizeof(KilnSavesApi), KILN_SAVES_VERSION } */
    /* any thread */
    KilnResult(KILN_CALL* status)(KilnSavesStatus* out);
    /* any thread. Asks the game for a world save at the start of the next step (after a save that is running now has
     * finished). reason: KILN_SAVE_REASON_REQUEST or _BACKUP. UNAVAILABLE: the request feature is off on this build.
     * Before the world has loaded the request waits; one the game does not accept within 60 s ends in a FAILED event
     * with KILN_SAVE_EVENT_TIMEOUT. Requests made while one is pending are merged. Watch for SAVING/WRITTEN with this
     * reason. */
    KilnResult(KILN_CALL* request)(uint32_t reason);
    /* any thread. The oldest queued event with seq > after_seq (the queue keeps the newest 256). NOT_FOUND: none. */
    KilnResult(KILN_CALL* next_event)(uint64_t after_seq, KilnSaveEvent* out);
    /* One pump (the managed Kiln.Saves). fn = NULL removes it. Returns the newest seq in *last_seq (events up to it
     * are history: the pump is not called for them). */
    KilnResult(KILN_CALL* set_pump)(KilnOwner owner, KilnSavesPumpFn fn, void* user, uint64_t* last_seq);
    /* any thread. KILN_SAVES_PATH_*; BUFFER_TOO_SMALL with *out_len = needed. */
    KilnResult(KILN_CALL* path)(int32_t kind, char* buffer, size_t capacity, size_t* out_len);
    /* Also call the pump every `steps` server-world steps in Pre, with kind 0 (0 = only for events). Any thread. */
    KilnResult(KILN_CALL* set_pump_interval)(uint32_t steps);
} KilnSavesApi;

#ifdef __cplusplus
}
#endif
#endif
