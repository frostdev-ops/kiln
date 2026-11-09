/* SPDX-License-Identifier: MIT
 * kiln.ui v1 (client only): an in-game overlay drawn with Dear ImGui on top of the game's frames, the game window's
 * input (keys for key bindings, mouse + keyboard capture while a Kiln screen is open), and an immediate-mode drawing
 * table for the managed Kiln.Ui. v2 appends right clicks on items and a radial wheel (the last slots of the table).
 *
 * How (docs/re/client-render.md):
 *  - The game loads vulkan-1.dll at run time and presents with vkQueuePresentKHR on queue family 0 (no static Vulkan
 *    or D3D imports; "--enable-dxgi" is an opt-in it does not use by default). Under Proton vulkan-1's exports forward
 *    to winevulkan, whose functions are what vkGetDeviceProcAddr hands out: Kiln hooks those after vulkan-1.dll
 *    loads (a hook on LoadLibraryExW). On Windows Kiln wraps vkGetInstanceProcAddr / vkGetDeviceProcAddr instead.
 *  - Each present of the game window's swapchain: when something is to be drawn, Kiln records ImGui into the image
 *    (a render pass that loads and keeps the game's picture), submits it on the present queue after the game's
 *    semaphores, and presents waiting on its own semaphore. When nothing is drawn the present passes through
 *    untouched: no ImGui frame, no submit (zero cost).
 *  - Input: the game window's WndProc is subclassed. Raw input (WM_INPUT, mouse + keyboard, no RIDEV_NOLEGACY) and
 *    the legacy key/mouse messages are swallowed while Kiln captures input; key releases always reach the game.
 *    While capturing, the game's SetCursorPos / ClipCursor calls (its import slots) are ignored so the cursor is free.
 * Threads
 *  - Frame callbacks run on the game's render thread (the thread calling vkQueuePresentKHR), inside the ImGui frame.
 *    The drawing functions below work only there (elsewhere they do nothing and return false).
 *  - Key callbacks run on the window thread (the game's main thread) while the message is being handled.
 *  - status, set_*, key_name: any thread.
 * Native subscribers must unsubscribe before they unload. */
#ifndef KILN_UI_H
#define KILN_UI_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_UI_SERVICE "kiln.ui"
#define KILN_UI_VERSION 2u /* v2 appends is_item_clicked and the radial wheel (after add_swapchain_usage): check header.size */

/* Colours are 0xRRGGBBAA. */

/* Window flags (subset of ImGuiWindowFlags, same values). */
enum {
    KILN_UI_WINDOW_NO_TITLE_BAR = 1 << 0,
    KILN_UI_WINDOW_NO_RESIZE = 1 << 1,
    KILN_UI_WINDOW_NO_MOVE = 1 << 2,
    KILN_UI_WINDOW_NO_SCROLLBAR = 1 << 3,
    KILN_UI_WINDOW_NO_COLLAPSE = 1 << 5,
    KILN_UI_WINDOW_ALWAYS_AUTO_RESIZE = 1 << 6,
    KILN_UI_WINDOW_NO_BACKGROUND = 1 << 7,
    KILN_UI_WINDOW_NO_INPUTS = (1 << 9) | (1 << 18) | (1 << 19), /* NoMouseInputs | NoNavInputs | NoNavFocus */
};

/* Conditions for set_next_window_* (ImGuiCond values). */
enum {
    KILN_UI_COND_ALWAYS = 1,
    KILN_UI_COND_ONCE = 2,
    KILN_UI_COND_FIRST_USE = 4,
    KILN_UI_COND_APPEARING = 8,
};

/* KilnUiKey.mods / set_menu_key mods */
enum {
    KILN_UI_MOD_CTRL = 1u,
    KILN_UI_MOD_SHIFT = 2u,
    KILN_UI_MOD_ALT = 4u,
};

typedef struct KilnUiStatus {
    uint32_t size;          /* set to sizeof(KilnUiStatus) before calling */
    uint8_t hooked;         /* Vulkan hooks installed (vulkan-1.dll loaded) */
    uint8_t device;         /* the game's Vulkan device has been seen */
    uint8_t swapchain;      /* a swapchain of the game window exists */
    uint8_t window;         /* the game window's WndProc is subclassed */
    uint8_t menu_open;
    uint8_t capturing;      /* input goes to Kiln right now */
    uint8_t drawing;        /* the overlay drew a frame in the last second */
    uint8_t wine;           /* running under Wine/Proton */
    uint32_t width, height; /* swapchain extent */
    uint32_t format;        /* VkFormat of the swapchain */
    uint32_t color_space;   /* VkColorSpaceKHR */
    float dpi_scale;
    uint32_t subscribers;
    uint64_t presents;      /* game presents seen */
    uint64_t frames_drawn;  /* presents Kiln drew into */
    uint64_t swapchains;    /* swapchains created by the game */
    double frame_ms;        /* CPU time of the last overlay frame (ImGui + callbacks + record + submit) */
    char error[128];        /* last overlay error, "" = none */
} KilnUiStatus;

typedef struct KilnUiFrame {
    uint32_t size;
    float width, height;    /* pixels */
    float dpi_scale;
    float delta_seconds;
    uint8_t menu_open;
    uint8_t capturing;
    uint8_t reserved[2];
    uint64_t frame;         /* overlay frames since start */
} KilnUiFrame;

typedef struct KilnUiKey {
    uint32_t size;
    uint32_t vk;            /* Windows virtual-key code */
    uint32_t scan;          /* scan code (extended bit 0x100) */
    uint32_t mods;          /* KILN_UI_MOD_* held at the time */
    uint8_t down;           /* 1 press, 0 release */
    uint8_t repeat;         /* auto-repeat of a held key */
    uint8_t text_input;     /* a Kiln text field has the keyboard */
    uint8_t captured;       /* Kiln captures input right now */
} KilnUiKey;

/* ---- appended: the game's own UI (docs/re/game-ui.md) ---- */

/* The game's top-level UI state (keen::UiState, reflected). */
enum {
    KILN_UI_STATE_NONE = 0,
    KILN_UI_STATE_PRELOAD = 1,    /* title / start-up screens */
    KILN_UI_STATE_PREGAME = 2,    /* the main menu (landing, character and world selection, settings) */
    KILN_UI_STATE_LOAD = 3,       /* loading a world */
    KILN_UI_STATE_LORE_INTRO = 4,
    KILN_UI_STATE_LORE_OUTRO = 5,
    KILN_UI_STATE_CREDITS = 6,
    KILN_UI_STATE_PLAY = 7,       /* in game: hud_screen says which screen is open */
};

/* A few keen::HudScreenType values (reflected enum, 54 values on 26404966; all are passed through). */
enum {
    KILN_UI_HUD_INGAME = 1,
    KILN_UI_HUD_SYSTEM_MENU = 2,  /* the pause (Escape) menu */
    KILN_UI_HUD_SETTINGS = 3,
    KILN_UI_HUD_INGAME_MENU = 4,  /* the tabbed menu: character, map, journal, crafting, backpack, skills */
    KILN_UI_HUD_MAP = 5,
    KILN_UI_HUD_JOURNAL = 6,
    KILN_UI_HUD_CHARACTER = 7,
    KILN_UI_HUD_PLAYER_CRAFTING = 8,
    KILN_UI_HUD_BACKPACK = 9,
    KILN_UI_HUD_SKILL_TREE = 10,
    KILN_UI_HUD_STORAGE_CHEST = 22,
    KILN_UI_HUD_STATION_CRAFTING = 25,
    KILN_UI_HUD_NPC_DIALOG = 26,
    KILN_UI_HUD_NPC_CRAFTING = 27,
    KILN_UI_HUD_FACTORY_CRAFTING = 28,
    KILN_UI_HUD_TEXT_CHAT = 43,
    KILN_UI_HUD_NO_UI = 51,
};

/* KilnUiScreen.crafting */
enum {
    KILN_UI_CRAFTING_NONE = 0,
    KILN_UI_CRAFTING_MENU = 1,     /* a crafting menu (the player's own or a station's) built this frame */
    KILN_UI_CRAFTING_JOURNAL = 2,  /* the journal's recipe book */
};

/* menus for menu_add */
enum {
    KILN_UI_MENU_MAIN = 1,   /* the main menu's entry list (next to Play, Settings, Quit) */
    KILN_UI_MENU_PAUSE = 2,  /* the system (pause) menu opened with Escape in game */
};

/* menu_set flags */
enum {
    KILN_UI_ENTRY_HIDDEN = 1u,
    KILN_UI_ENTRY_ATTENTION = 2u,  /* the game's attention marker (pause menu only) */
};

/* push_style */
enum {
    KILN_UI_STYLE_KILN = 0,
    KILN_UI_STYLE_GAME = 1,
};

typedef struct KilnUiScreen {
    uint32_t size;        /* set to sizeof(KilnUiScreen) before calling screen() */
    uint8_t ui_state;     /* KILN_UI_STATE_* */
    uint8_t hud_screen;   /* keen::HudScreenType while ui_state is PLAY, else 0 */
    uint8_t crafting;     /* KILN_UI_CRAFTING_* */
    uint8_t available;    /* the game-UI hooks run (feature ui.screens) */
    uint32_t interaction; /* play data +0x2e48: for a crafting station its keen::WorkshopId (seen at run time: the
                             Workbench gives 0x4da88f73); other uses not known; 0 = none */
    uint32_t station;     /* the crafting menu's station: its keen::WorkshopId (0 = the player's own crafting) */
    uint32_t workshop;    /* keen::WorkshopId of the crafting menu (0 = the player's own crafting) */
    uint32_t recipe;      /* recipe id selected in the crafting menu, 0 = none */
    uint32_t language;    /* keen::LanguageId of the game's texts (1 en-us, 3 de-de, ...; 0 = unknown) */
    uint32_t ui_thread;   /* thread id of the game's UI */
    uint64_t frame;       /* game UI frames since start */
} KilnUiScreen;

typedef void(KILN_CALL* KilnUiScreenFn)(const KilnUiScreen* now, const KilnUiScreen* before, void* user);
typedef void(KILN_CALL* KilnUiTickFn)(const KilnUiScreen* now, void* user);
typedef void(KILN_CALL* KilnUiMenuFn)(KilnHandle entry, void* user);

/* The frame the game is about to present, for subscribe_pre_overlay. Vulkan handles are void* so that this header needs no Vulkan headers. */
typedef struct KilnUiPreOverlay {
    uint32_t size;
    uint32_t image_index;
    void* instance;                 /* VkInstance */
    void* physical_device;          /* VkPhysicalDevice */
    void* device;                   /* VkDevice */
    void* get_instance_proc_addr;   /* PFN_vkGetInstanceProcAddr: the real one */
    void* get_device_proc_addr;     /* PFN_vkGetDeviceProcAddr: the real one */
    void* queue;                    /* VkQueue the game presents on */
    void* swapchain;                /* VkSwapchainKHR */
    void* image;                    /* VkImage being presented, in PRESENT_SRC_KHR once wait_semaphores signal */
    void* const* wait_semaphores;   /* VkSemaphore[wait_count] */
    uint32_t wait_count;
    uint32_t queue_family;
    uint32_t format;                /* VkFormat */
    uint32_t color_space;           /* VkColorSpaceKHR */
    uint32_t width, height;
    uint32_t usage;                 /* VkImageUsageFlags the swapchain was created with */
    uint32_t reserved;
} KilnUiPreOverlay;

/* Render thread, inside the game's vkQueuePresentKHR. Keep it short: the game's frame waits. See subscribe_pre_overlay. */
typedef bool(KILN_CALL* KilnUiPreOverlayFn)(const KilnUiPreOverlay* frame, void** out_semaphore, void* user);
/* Any thread, with no present running: what was made for this swapchain has to go. swapchain NULL: the whole device goes. */
typedef void(KILN_CALL* KilnUiSwapchainGoneFn)(void* device, void* swapchain, void* user);

/* Render thread, inside the ImGui frame. */
typedef void(KILN_CALL* KilnUiFrameFn)(const KilnUiFrame* frame, void* user);
/* Window thread. Return true to keep the key from the game. */
typedef bool(KILN_CALL* KilnUiKeyFn)(const KilnUiKey* key, void* user);

/* ---- v2: right clicks and the radial wheel ---- */

/* is_item_clicked: mouse buttons (ImGuiMouseButton values) */
enum {
    KILN_UI_MOUSE_LEFT = 0,
    KILN_UI_MOUSE_RIGHT = 1,
    KILN_UI_MOUSE_MIDDLE = 2,
};

/* wheel limits */
enum {
    KILN_UI_WHEEL_MAX_ITEMS = 64,  /* entries in one wheel */
    KILN_UI_WHEEL_PAGE_ITEMS = 8,  /* entries on one page: the mouse wheel turns the page */
};

/* wheel_open: the definition (every string is copied; labels are cut at 256 bytes, the title at 64) */
typedef struct KilnUiWheel {
    uint32_t size;          /* sizeof(KilnUiWheel) */
    uint32_t flags;         /* 0 (reserved; other bits are refused) */
    KilnStr title;          /* shown in the middle of the wheel; empty = none */
    const KilnStr* items;   /* `count` labels. "Name (details)" shows the details on a second line */
    uint32_t count;         /* 1..KILN_UI_WHEEL_MAX_ITEMS */
    uint32_t hold_key;      /* 0, or a keyboard key's generic virtual-key code (VK_SHIFT, VK_CONTROL, VK_MENU, a letter...;
                               no mouse buttons, no VK_LSHIFT-style codes: refused) that the player holds while the wheel is
                               open: releasing it picks the highlighted entry (in the centre: cancels). A tap (released within
                               300 ms with nothing highlighted) leaves the wheel open for a click. 0, or a key that is not
                               held when the wheel opens: no key, a left click picks */
} KilnUiWheel;

/* Called once when the wheel ends, on the render thread inside the overlay frame (keep it short; when the overlay turns
 * off after an error, at the next present with -1). choice = the index
 * into `items`, or -1 when the player cancelled (Esc or a right click, or a release in the centre). The wheel is closed
 * before the call, so fn may open another wheel. */
typedef void(KILN_CALL* KilnUiWheelFn)(int32_t choice, void* user);

typedef struct KilnUiApi {
    KilnServiceHeader header; /* { sizeof(KilnUiApi), KILN_UI_VERSION } */
    KilnResult(KILN_CALL* status)(KilnUiStatus* out);
    KilnResult(KILN_CALL* subscribe_frame)(KilnOwner owner, KilnUiFrameFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* subscribe_key)(KilnOwner owner, KilnUiKeyFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    /* Things to draw (HUD lines, windows, toasts). 0 = the overlay idles: presents pass through untouched. Each calling
     * native plugin (DLL; all managed code counts as one caller) sets its own count; the overlay uses the sum. */
    void(KILN_CALL* set_demand)(uint32_t layers);
    /* Interactive Kiln windows open: > 0 = Kiln takes mouse and keyboard (as when the Kiln menu is open). A count per
     * calling plugin, summed, as for set_demand. */
    void(KILN_CALL* set_capture)(uint32_t interactive);
    /* The Kiln menu: -1 toggle, 0 close, 1 open. Its key toggles it from the window thread (default Insert). */
    void(KILN_CALL* set_menu)(int32_t open);
    void(KILN_CALL* set_menu_key)(uint32_t vk, uint32_t mods);
    /* Name of a virtual key ("F8", "Insert", "A"), NUL-terminated. Any thread. */
    void(KILN_CALL* key_name)(uint32_t vk, char* buffer, size_t capacity);

    /* ---- drawing: only inside a frame callback ---- */
    bool(KILN_CALL* begin)(KilnStr title, bool* open, uint32_t flags);
    void(KILN_CALL* end)(void);
    void(KILN_CALL* text)(KilnStr text);
    void(KILN_CALL* text_colored)(uint32_t rgba, KilnStr text);
    void(KILN_CALL* text_wrapped)(KilnStr text);
    void(KILN_CALL* text_disabled)(KilnStr text);
    bool(KILN_CALL* button)(KilnStr label, float width, float height);
    bool(KILN_CALL* small_button)(KilnStr label);
    bool(KILN_CALL* checkbox)(KilnStr label, bool* value);
    bool(KILN_CALL* slider_float)(KilnStr label, float* value, float min, float max);
    bool(KILN_CALL* slider_int)(KilnStr label, int32_t* value, int32_t min, int32_t max);
    /* `buffer` holds NUL-terminated UTF-8 and is edited in place. */
    bool(KILN_CALL* input_text)(KilnStr label, char* buffer, size_t capacity, uint32_t flags);
    bool(KILN_CALL* collapsing_header)(KilnStr label, uint32_t flags);
    bool(KILN_CALL* tree_node)(KilnStr label);
    void(KILN_CALL* tree_pop)(void);
    bool(KILN_CALL* selectable)(KilnStr label, bool selected);
    void(KILN_CALL* separator)(void);
    void(KILN_CALL* separator_text)(KilnStr text);
    void(KILN_CALL* same_line)(float offset, float spacing);
    void(KILN_CALL* spacing)(void);
    void(KILN_CALL* push_id)(KilnStr id);
    void(KILN_CALL* pop_id)(void);
    void(KILN_CALL* set_next_window_pos)(float x, float y, uint32_t cond, float pivot_x, float pivot_y);
    void(KILN_CALL* set_next_window_size)(float width, float height, uint32_t cond);
    void(KILN_CALL* set_next_window_bg_alpha)(float alpha);
    void(KILN_CALL* progress_bar)(float fraction, float width, float height, KilnStr overlay);
    bool(KILN_CALL* begin_table)(KilnStr id, int32_t columns, uint32_t flags);
    void(KILN_CALL* table_setup_column)(KilnStr label, uint32_t flags, float width);
    void(KILN_CALL* table_headers_row)(void);
    void(KILN_CALL* table_next_row)(void);
    bool(KILN_CALL* table_next_column)(void);
    void(KILN_CALL* end_table)(void);
    bool(KILN_CALL* begin_tab_bar)(KilnStr id);
    bool(KILN_CALL* begin_tab_item)(KilnStr label);
    void(KILN_CALL* end_tab_item)(void);
    void(KILN_CALL* end_tab_bar)(void);
    bool(KILN_CALL* begin_child)(KilnStr id, float width, float height, uint32_t flags);
    void(KILN_CALL* end_child)(void);
    bool(KILN_CALL* is_item_hovered)(void);
    void(KILN_CALL* set_tooltip)(KilnStr text);
    /* Foreground (HUD) drawing in pixels; size 0 = the default font size. */
    void(KILN_CALL* draw_text)(float x, float y, uint32_t rgba, float size, KilnStr text);
    void(KILN_CALL* draw_rect)(float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, int32_t filled);
    void(KILN_CALL* text_size)(KilnStr text, float size, float* width, float* height);
    /* The next key pressed (for "press a key" remapping): returns its vk once, 0 while none. Any thread. */
    uint32_t(KILN_CALL* take_pressed_key)(uint32_t* mods);

    /* ================================================================================================================
     * Appended later (game UI integration, docs/re/game-ui.md). Check header.size >= offsetof(KilnUiApi, x) +
     * sizeof(void*) before calling a slot below: an older kiln.ui ends above.
     * ============================================================================================================== */

    /* ---- the game's screens ----
     * screen: the state the game's UI built in its last frame (KilnUiScreen; set out->size first). Any thread.
     *   KILN_ERR_UNAVAILABLE: feature ui.screens is off on this build.
     * subscribe_screen: fn(now, before) on the game's UI thread right after a UI frame whose screen differs from the
     *   previous frame's (ui_state, hud_screen, interaction, crafting, workshop, recipe or language changed).
     * subscribe_ui_tick: fn(now) on the game's UI thread after every UI frame, in the main menu too (the game's UI
     *   builds every frame in every state, worlds or not). Keep it short: it runs inside the game's frame.
     * Both are removed with unsubscribe. */
    KilnResult(KILN_CALL* screen)(KilnUiScreen* out);
    KilnResult(KILN_CALL* subscribe_screen)(KilnOwner owner, KilnUiScreenFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* subscribe_ui_tick)(KilnOwner owner, KilnUiTickFn fn, void* user, KilnHandle* out);

    /* ---- entries in the game's own menus (KILN_UI_MENU_*) ----
     * menu_add: an entry drawn by the game's own menu-entry renderer (its look, sounds, focus and gamepad/keyboard
     *   navigation) right above the menu's Settings entry; entries with a lower `order` come first. `text` is the
     *   label (UTF-8, copied; Kiln picks the language, see KilnUiScreen.language). fn(entry, user) runs on the UI
     *   thread after the frame in which the player activated it. KILN_ERR_UNAVAILABLE: feature ui.main_menu /
     *   ui.system_menu is off. Remove with unsubscribe(entry).
     * menu_set: a new label and KILN_UI_ENTRY_* flags (any thread; the next frame shows it). */
    KilnResult(KILN_CALL* menu_add)(KilnOwner owner, uint32_t menu, int32_t order, KilnStr text, KilnUiMenuFn fn,
                                    void* user, KilnHandle* out);
    KilnResult(KILN_CALL* menu_set)(KilnHandle entry, KilnStr text, uint32_t flags);

    /* ---- looks for Kiln's own windows (inside a frame callback) ----
     * push_style: KILN_UI_STYLE_GAME (the game's colours and, once loaded, the game's own UI font read from its
     *   resources at run time) or KILN_UI_STYLE_KILN (Kiln's ember theme, branding/palette.json); applies to the
     *   windows begun until the matching pop_style. */
    void(KILN_CALL* push_style)(uint32_t style);
    void(KILN_CALL* pop_style)(void);

    /* ---- images (PNG, RGBA) for Kiln windows ----
     * image_load: decodes a PNG (bytes, copied) into a texture Kiln uploads at the next frame. Any thread. 0 = failed.
     * image_size: its pixel size. image: draws it inside a frame callback (width/height 0 = its size);
     * image_button: the same as a button (returns true when clicked). image_free: drops it (owner's images also go
     * when the owner is revoked... call it yourself before unloading). */
    KilnHandle(KILN_CALL* image_load)(KilnOwner owner, const void* png, size_t len);
    bool(KILN_CALL* image_size)(KilnHandle image, uint32_t* width, uint32_t* height);
    void(KILN_CALL* image)(KilnHandle image, float width, float height);
    bool(KILN_CALL* image_button)(KilnStr id, KilnHandle image, float width, float height);
    void(KILN_CALL* image_free)(KilnHandle image);

    /* ---- small extras for full screens ----
     * set_next_window_focus: focus the next window begun (a screen that opens takes the keyboard).
     * combo: a drop-down over `items` (NUL-separated, ends with an empty string); returns true when *index changed.
     * input_multiline: a multi-line text field (buffer edited in place). dummy: empty space of a size.
     * window_rect: position and size of the current window (pixels). */
    void(KILN_CALL* set_next_window_focus)(void);
    bool(KILN_CALL* combo)(KilnStr label, int32_t* index, const char* items_nul_separated);
    bool(KILN_CALL* input_multiline)(KilnStr label, char* buffer, size_t capacity, float width, float height);
    void(KILN_CALL* dummy)(float width, float height);
    void(KILN_CALL* window_rect)(float* x, float* y, float* width, float* height);

    /* ---- capture modes ----
     * set_hover_capture: Kiln panels that sit next to a game screen (e.g. a crafting info panel) take the mouse
     *   only while it is over them: > 0 = on (count of such panels), clicks over a Kiln panel are kept from the
     *   game; the keyboard stays the game's. */
    void(KILN_CALL* set_hover_capture)(uint32_t panels);

    /* ---- full-screen work at present, under the overlay ----
     * subscribe_pre_overlay: fn runs at each present of the game window's swapchain while set_pre_overlay_demand has said > 0, before the overlay.
     *   It may record and submit its own work on `queue`, waiting on wait_semaphores, leaving the image in PRESENT_SRC_KHR. Then it returns true
     *   and sets *out_semaphore, which it signals: the overlay and the present wait on that in place of the game's. Returning false says it
     *   submitted nothing and did not touch the semaphores, and the frame goes on as before. Submit last: nothing may fail after it.
     *   A fault in fn removes the subscription, with a log line. Remove with unsubscribe.
     * gone: see KilnUiSwapchainGoneFn. Called once the overlay's own work on that swapchain has finished.
     * set_pre_overlay_demand: 0 means no call at all, and the present costs one relaxed load. Any thread.
     * add_swapchain_usage: VkImageUsageFlags for every swapchain created from now on, as far as the surface supports them (KilnUiPreOverlay.usage
     *   says what it got). Call it in on_ready: the game creates its first swapchain after that. */
    KilnResult(KILN_CALL* subscribe_pre_overlay)(KilnOwner owner, KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out);
    void(KILN_CALL* set_pre_overlay_demand)(uint32_t count);
    void(KILN_CALL* add_swapchain_usage)(uint32_t usage);

    /* ================================================================================================================
     * v2 (appended; check header.size >= offsetof(KilnUiApi, wheel_active) + sizeof(void*) before calling these)
     * ============================================================================================================== */

    /* ---- right clicks (inside a frame callback) ----
     * is_item_clicked: true on the frame the mouse `button` (KILN_UI_MOUSE_*) went down over the item drawn last (a
     *   button, selectable, image button...). It does not depend on the item's own click rules, so it also sees a right
     *   click on a button. False outside a frame callback, or while something above the item (another window, the
     *   wheel) has the pointer. Costs nothing unless called. */
    bool(KILN_CALL* is_item_clicked)(uint32_t button);

    /* ---- the radial wheel ----
     * One wheel at a time, drawn in the middle of the screen over everything (windows below it get no input). While it is
     * open Kiln takes the mouse and keyboard, as for a window: the mouse steers the highlight (the sector the pointer is
     * in; nothing in the centre), the camera does not move, keys do not reach the game (releases still do), the mouse
     * wheel turns the page (8 entries per page). A left click on a highlighted entry picks it; Esc or a right click
     * cancel; the hold key (KilnUiWheel.hold_key) releases as described there. When it ends fn(choice, user) runs once.
     * wheel_open: UNAVAILABLE when another wheel is open, the Kiln menu is open, or the overlay cannot draw (no window or
     *   swapchain yet; ask wheel_active to tell the first case). INVALID_ARGUMENT for a bad definition or no fn. Any
     *   thread; *out (optional) = the wheel's handle.
     * wheel_close: ends the wheel without calling fn (the caller knows). NOT_FOUND when it already ended, in which case
     *   fn has run or is running. Waits for a running fn unless called from it. Any thread.
     * wheel_active: a wheel is open now (any owner). Any thread.
     * Native callers close their wheel before they unload. */
    KilnResult(KILN_CALL* wheel_open)(KilnOwner owner, const KilnUiWheel* wheel, KilnUiWheelFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* wheel_close)(KilnHandle wheel);
    bool(KILN_CALL* wheel_active)(void);
} KilnUiApi;

/* ====================================================================================================================
 * kiln.ui.tick v1: the game's UI frame as a tick, for Kiln.Host (loads the client's mods in the main menu, where no
 * world steps). fn(frame, user) on the game's UI thread after each UI frame. Registered by kiln-ui.dll when feature
 * ui.screens is on; absent otherwise (then there is no UI tick, and nothing changes).
 * ================================================================================================================== */
#define KILN_UI_TICK_SERVICE "kiln.ui.tick"
#define KILN_UI_TICK_VERSION 1u

typedef void(KILN_CALL* KilnUiFrameTickFn)(uint64_t frame, void* user);

typedef struct KilnUiTickApi {
    KilnServiceHeader header; /* { sizeof(KilnUiTickApi), KILN_UI_TICK_VERSION } */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnUiFrameTickFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* unsubscribe)(KilnHandle handle);
    /* true while no world steps (boot, main menu, loading) and the UI frames still run */
    bool(KILN_CALL* in_menu)(void);
} KilnUiTickApi;

/* ====================================================================================================================
 * kiln.craftmenu v1: the crafting menu's categories (tabs) and subcategories, implemented by kiln.content's hook on
 * the menu's category lookup (native/services/content/newcontent.cpp, docs/re/game-ui.md "Crafting menus").
 *  - The menu shows, per workshop id, the tabs (keen::FbUiCraftingRecipeGroup: icon, label, subcategories) of its UI
 *    configuration, and in each subcategory (keen::FbUiCraftingRecipeSet: label, recipe ids) the recipes the player
 *    has unlocked. A recipe in no subcategory is never shown.
 *  - set_add: a new subcategory with its own label at the end of tab `tab` (index into that workshop's tabs; -1 =
 *    the last) holding `recipes`. Recipes listed in a Kiln subcategory are not also placed automatically.
 *  - tab_add: a new tab (icon copied from tab `icon_from`, -1 = the last) holding one subcategory with `recipes`.
 *  - Labels: `label_texts` = "text" or a JSON object {"en": "...", "de": "...", "*": "..."}; answered in the game's
 *    language by kiln.content's loca hook.
 *  - Limits: the game sizes its menu by the largest tab / subcategory counts in its data (keen::
 *    FbUiCraftingRecipeCollection.maxRecipeGroupCount / maxRecipeSetCount): additions beyond those are refused
 *    (KILN_ERR_UNAVAILABLE, logged).
 *  - Changes apply when the menu next rebuilds its lists (reopen it). Remove with remove(handle).
 *  - subscribe: fn(info) on the UI thread each time the menu looks up a workshop's tabs (when a crafting menu opens
 *    or rebuilds its lists).
 * ================================================================================================================== */
#define KILN_CRAFTMENU_SERVICE "kiln.craftmenu"
#define KILN_CRAFTMENU_VERSION 1u

typedef struct KilnCraftMenuInfo {
    uint32_t size;
    uint32_t workshop;   /* keen::WorkshopId the menu builds (0 = the player's own crafting) */
    uint32_t tabs;       /* tabs of that workshop in the UI configuration (with Kiln's) */
    uint32_t max_tabs;   /* the game's limits (see above) */
    uint32_t max_sets;
    uint32_t reserved;
} KilnCraftMenuInfo;

typedef void(KILN_CALL* KilnCraftMenuFn)(const KilnCraftMenuInfo* info, void* user);

typedef struct KilnCraftMenuApi {
    KilnServiceHeader header; /* { sizeof(KilnCraftMenuApi), KILN_CRAFTMENU_VERSION } */
    KilnResult(KILN_CALL* subscribe)(KilnOwner owner, KilnCraftMenuFn fn, void* user, KilnHandle* out);
    KilnResult(KILN_CALL* set_add)(KilnOwner owner, uint32_t workshop, int32_t tab, KilnStr label_texts,
                                   const uint32_t* recipes, uint32_t count, KilnHandle* out);
    KilnResult(KILN_CALL* tab_add)(KilnOwner owner, uint32_t workshop, int32_t icon_from, KilnStr label_texts,
                                   const uint32_t* recipes, uint32_t count, KilnHandle* out);
    /* removes a set / tab / subscription */
    KilnResult(KILN_CALL* remove)(KilnHandle handle);
    /* JSON: per workshop the tabs and subcategories (labels as tag ids, recipe counts) and Kiln's additions */
    KilnResult(KILN_CALL* describe)(uint32_t workshop, char* json, size_t capacity, size_t* out_len);
} KilnCraftMenuApi;

#ifdef __cplusplus
}
#endif
#endif
