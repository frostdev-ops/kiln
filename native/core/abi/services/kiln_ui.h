/* SPDX-License-Identifier: MIT
 * kiln.ui v1 (client only): an in-game overlay drawn with Dear ImGui on top of the game's frames, the game window's
 * input (keys for key bindings, mouse + keyboard capture while a Kiln screen is open), and an immediate-mode drawing
 * table for the managed Kiln.Ui. v2 appends right clicks on items and a radial wheel; v3 appends font sizes, child
 * regions with flags, cursor and item queries, sized selectables, focus, layered drawing, colour/spacing pushes and
 * key queries; v4 appends the main-menu page (the Kiln menu inside the game's own menu) and a font choice; v5 appends
 * the game window's own input records for native plugins (subscribe_window_input, the last slot of the table).
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
 *  - Window-input callbacks (v5) run on the window thread too, inside the window procedure.
 *  - status, set_*, key_name: any thread.
 * Native subscribers must unsubscribe before they unload. */
#ifndef KILN_UI_H
#define KILN_UI_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_UI_SERVICE "kiln.ui"
#define KILN_UI_VERSION 5u /* v2 appends is_item_clicked and the radial wheel (after add_swapchain_usage); v3 appends layout and
                              look slots for full-display screens (after wheel_active); v4 appends the main-menu page and
                              font choice (after set_scroll_here_y); v5 appends subscribe_window_input (after pop_font):
                              check header.size */

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
    KILN_UI_WINDOW_NO_INPUTS = (1 << 9) | (1 << 16) | (1 << 17), /* NoMouseInputs | NoNavInputs | NoNavFocus */
    /* v3 (same ImGui values; older kiln.ui passes them on to ImGui as well) */
    KILN_UI_WINDOW_NO_SAVED_SETTINGS = 1 << 8,       /* no imgui.ini entry (position and size are the caller's) */
    KILN_UI_WINDOW_NO_BRING_TO_FRONT = 1 << 13,      /* a click does not raise it above other windows */
    KILN_UI_WINDOW_ALWAYS_VERTICAL_SCROLLBAR = 1 << 14,
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

/* ---- v3: layout and look ---- */

/* begin_child_ex child_flags (ImGuiChildFlags values) */
enum {
    KILN_UI_CHILD_BORDERS = 1 << 0,         /* an outline (and the window padding) */
    KILN_UI_CHILD_ALWAYS_PADDING = 1 << 1,  /* the window padding without an outline */
    KILN_UI_CHILD_AUTO_RESIZE_Y = 1 << 5,   /* as tall as its contents (height 0) */
    KILN_UI_CHILD_FRAME_STYLE = 1 << 7,     /* looks like a frame (frame colours and padding) */
    KILN_UI_CHILD_NAV_FLATTENED = 1 << 8,   /* gamepad/keyboard navigation crosses into and out of it like plain widgets */
};

/* selectable_ex flags (ImGuiSelectableFlags values) */
enum {
    KILN_UI_SELECTABLE_SPAN_ALL_COLUMNS = 1 << 1,
    KILN_UI_SELECTABLE_ALLOW_DOUBLE_CLICK = 1 << 2,
    KILN_UI_SELECTABLE_DISABLED = 1 << 3,
    KILN_UI_SELECTABLE_ALLOW_OVERLAP = 1 << 4,  /* widgets drawn over it later take their own clicks */
    KILN_UI_SELECTABLE_HIGHLIGHT = 1 << 5,      /* drawn as if hovered */
};

/* draw_rect_in / draw_text_in layers */
enum {
    KILN_UI_LAYER_WINDOW = 0,
    KILN_UI_LAYER_BACKGROUND = 1,
    KILN_UI_LAYER_FOREGROUND = 2,
};

/* push_color slots (Kiln's own numbers, mapped to the ImGui build Kiln ships) */
enum {
    KILN_UI_COLOR_TEXT = 0,
    KILN_UI_COLOR_TEXT_DISABLED = 1,
    KILN_UI_COLOR_WINDOW_BG = 2,
    KILN_UI_COLOR_CHILD_BG = 3,
    KILN_UI_COLOR_BORDER = 4,
    KILN_UI_COLOR_FRAME_BG = 5,
    KILN_UI_COLOR_FRAME_BG_HOVERED = 6,
    KILN_UI_COLOR_FRAME_BG_ACTIVE = 7,
    KILN_UI_COLOR_BUTTON = 8,
    KILN_UI_COLOR_BUTTON_HOVERED = 9,
    KILN_UI_COLOR_BUTTON_ACTIVE = 10,
    KILN_UI_COLOR_HEADER = 11,          /* selectables: selected */
    KILN_UI_COLOR_HEADER_HOVERED = 12,  /* selectables: hovered */
    KILN_UI_COLOR_HEADER_ACTIVE = 13,   /* selectables: held */
    KILN_UI_COLOR_SEPARATOR = 14,
    KILN_UI_COLOR_SCROLLBAR_BG = 15,
    KILN_UI_COLOR_SCROLLBAR_GRAB = 16,
    KILN_UI_COLOR_NAV_CURSOR = 17,      /* the gamepad/keyboard focus outline */
    KILN_UI_COLOR_CHECK_MARK = 18,
    KILN_UI_COLOR_SLIDER_GRAB = 19,
    KILN_UI_COLOR_COUNT = 20,
};

/* push_var slots: pixels (multiply by the UI scale yourself); _ALIGN ones are 0..1 */
enum {
    KILN_UI_VAR_WINDOW_PADDING = 0,      /* x, y */
    KILN_UI_VAR_FRAME_PADDING = 1,       /* x, y */
    KILN_UI_VAR_ITEM_SPACING = 2,        /* x, y */
    KILN_UI_VAR_ITEM_INNER_SPACING = 3,  /* x, y */
    KILN_UI_VAR_FRAME_ROUNDING = 4,
    KILN_UI_VAR_FRAME_BORDER_SIZE = 5,
    KILN_UI_VAR_CHILD_ROUNDING = 6,
    KILN_UI_VAR_CHILD_BORDER_SIZE = 7,
    KILN_UI_VAR_SELECTABLE_TEXT_ALIGN = 8, /* x, y */
    KILN_UI_VAR_BUTTON_TEXT_ALIGN = 9,     /* x, y */
    KILN_UI_VAR_SCROLLBAR_SIZE = 10,
    KILN_UI_VAR_WINDOW_BORDER_SIZE = 11,
    KILN_UI_VAR_COUNT = 12,
};

/* key_pressed keys (Kiln's own numbers) */
enum {
    KILN_UI_KEY_TAB = 1,
    KILN_UI_KEY_LEFT = 2,
    KILN_UI_KEY_RIGHT = 3,
    KILN_UI_KEY_UP = 4,
    KILN_UI_KEY_DOWN = 5,
    KILN_UI_KEY_PAGE_UP = 6,
    KILN_UI_KEY_PAGE_DOWN = 7,
    KILN_UI_KEY_HOME = 8,
    KILN_UI_KEY_END = 9,
    KILN_UI_KEY_ENTER = 10,
    KILN_UI_KEY_ESCAPE = 11,
    KILN_UI_KEY_BACKSPACE = 12,
    KILN_UI_KEY_F = 13,                 /* the letter F (Ctrl+F: find) */
    KILN_UI_KEY_GAMEPAD_LB = 14,        /* left shoulder */
    KILN_UI_KEY_GAMEPAD_RB = 15,        /* right shoulder */
    KILN_UI_KEY_GAMEPAD_X = 16,         /* face button left (Xbox X) */
    KILN_UI_KEY_GAMEPAD_Y = 17,         /* face button top (Xbox Y) */
    KILN_UI_KEY_GAMEPAD_START = 18,
    KILN_UI_KEY_GAMEPAD_BACK = 19,
    KILN_UI_KEY_COUNT = 20,
};

/* key_pressed flags */
enum {
    KILN_UI_KEYQ_REPEAT = 1u,
    KILN_UI_KEYQ_CLAIM = 2u,
};

/* cursor(): positions are window-local (x, y, as set_cursor_pos takes them) and screen pixels (screen_x/y, as the
 * draw_* calls take them) */
typedef struct KilnUiCursor {
    uint32_t size;                  /* sizeof(KilnUiCursor) */
    float x, y;                     /* where the next widget goes, window-local (scrolling included) */
    float screen_x, screen_y;       /* the same in screen pixels */
    float avail_width, avail_height;/* space left in the current window or child from the cursor */
    float window_x, window_y;       /* the current window's (or child's) top-left, screen pixels */
    float window_width, window_height;
    float font_size;                /* the current font size in pixels (UI scale applied) */
    float line_height;              /* one text line plus the item spacing */
    float scroll_y, scroll_max_y;
} KilnUiCursor;

/* item(): the widget drawn last */
typedef struct KilnUiItem {
    uint32_t size;                  /* sizeof(KilnUiItem) */
    float min_x, min_y, max_x, max_y; /* screen pixels */
    uint8_t hovered;
    uint8_t active;                 /* held (mouse down on it, or a text field being edited) */
    uint8_t focused;                /* has the gamepad/keyboard navigation focus */
    uint8_t clicked;                /* the left button went down over it this frame */
    uint8_t visible;                /* not clipped away */
    uint8_t edited;                 /* its value changed this frame */
    uint8_t activated;              /* became active this frame */
    uint8_t deactivated;            /* stopped being active this frame */
} KilnUiItem;

/* ---- v4: the Kiln menu as a page of the game's main menu (docs/kiln/ui.md "Two hosts", docs/re/game-ui.md
 * "Main-menu pages"). Feature ui.menu_page: the main menu's MODS entry makes the game go to its Credits page slot, whose
 * content kiln.ui hooks and leaves empty, so the game moves its camera and hides its main menu as for its own pages;
 * the managed host draws the Kiln menu into the rectangle below. menu_page_leave makes the game go back. Without the
 * feature (or before a host called menu_page_host(1)) the MODS entry opens the stand-alone Kiln menu as before. ---- */

/* KilnUiMenuPage.state */
enum {
    KILN_UI_PAGE_HIDDEN = 0,
    KILN_UI_PAGE_OPENING = 1,  /* the game was asked to go to the page; it shows from the next UI frame */
    KILN_UI_PAGE_SHOWN = 2,    /* the game shows the page (shown_frame: the last UI frame it did) */
};

/* KilnUiMenuPage.opened_by */
enum {
    KILN_UI_PAGE_BY_ENTRY = 1,  /* the main menu's MODS entry */
    KILN_UI_PAGE_BY_KEY = 2,    /* the menu key while the main menu showed */
};

/* push_font fonts */
enum {
    KILN_UI_FONT_DEFAULT = 0,    /* the current look's font */
    KILN_UI_FONT_GAME = 1,       /* the game's UI font, regular */
    KILN_UI_FONT_GAME_BOLD = 2,  /* the game's UI font, bold (its page titles) */
};

typedef struct KilnUiMenuPage {
    uint32_t size;          /* sizeof(KilnUiMenuPage) */
    uint8_t available;      /* feature ui.menu_page on, its hook in, a host claimed it, and not given up this session */
    uint8_t state;          /* KILN_UI_PAGE_* */
    uint8_t error_popup;    /* the game shows an error popup now (feature ui.error_popup; 0 when unknown) */
    uint8_t metrics;        /* the canvas, viewport and pixels-per-unit fields are the game's (else 0) */
    uint8_t opened_by;      /* KILN_UI_PAGE_BY_* */
    uint8_t reserved[7];
    uint64_t ui_frame;      /* the game's UI frames finished so far */
    uint64_t opened_frame;  /* the UI frame the page was asked for */
    uint64_t shown_frame;   /* the last UI frame the game showed the page */
    float canvas_width, canvas_height;  /* the game's UI canvas in UI units (1920x1080, wider/taller to fit the window) */
    float viewport_x, viewport_y, viewport_width, viewport_height;  /* where the canvas is drawn, pixels */
    float pixels_per_unit;  /* pixels per UI unit */
    float pad_left, pad_top, pad_right, pad_bottom;  /* the game's page padding, UI units (its Play page: 80 / 50) */
    float x0, y0, x1, y1;   /* the page's content rectangle in pixels: the viewport inside the padding */
    uint32_t reserved2;
} KilnUiMenuPage;           /* 104 bytes */

/* ---- v5: the game window's input records, as the window received them (subscribe_window_input) ---- */

/* KilnUiWindowInput.kind */
enum {
    KILN_UI_INPUT_RAW_KEY = 1,       /* a keyboard record of WM_INPUT: make_code, flags, vkey = RAWKEYBOARD.MakeCode,
                                        .Flags, .VKey, unchanged */
    KILN_UI_INPUT_WINDOW_KEY = 2,    /* WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN or WM_SYSKEYUP (message): wparam, lparam
                                        unchanged */
    KILN_UI_INPUT_MOUSE_BUTTON = 3,  /* a mouse button went down or up: button 1 left, 2 middle, 3 right, 4 XBUTTON1,
                                        5 XBUTTON2; down 1 or 0. message: WM_INPUT (a raw mouse record), or the button
                                        message (WM_LBUTTONDOWN ... WM_XBUTTONDBLCLK; a double click counts as down) */
    KILN_UI_INPUT_FOCUS_LOST = 4,    /* the window lost the keyboard focus (message WM_KILLFOCUS, or WM_ACTIVATEAPP
                                        with wparam 0). One record per loss, see subscribe_window_input */
};

typedef struct KilnUiWindowInput {
    uint32_t size;       /* sizeof(KilnUiWindowInput) */
    uint32_t kind;       /* KILN_UI_INPUT_* */
    uint32_t message;    /* the window message the record came with */
    uint32_t make_code;  /* RAW_KEY: RAWKEYBOARD.MakeCode */
    uint32_t flags;      /* RAW_KEY: RAWKEYBOARD.Flags (RI_KEY_BREAK, RI_KEY_E0, RI_KEY_E1) */
    uint32_t vkey;       /* RAW_KEY: RAWKEYBOARD.VKey */
    uint32_t button;     /* MOUSE_BUTTON: 1..5 */
    uint32_t down;       /* MOUSE_BUTTON: 1 pressed, 0 released */
    uint64_t wparam;     /* the message's wParam, unchanged (for every kind) */
    int64_t lparam;      /* the message's lParam, unchanged (WM_INPUT: its HRAWINPUT, valid during the call only) */
} KilnUiWindowInput;     /* 48 bytes */

/* Window thread, inside the window procedure. Keep it short: the game's message loop waits. */
typedef void(KILN_CALL* KilnUiWindowInputFn)(const KilnUiWindowInput* input, void* user);

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

    /* ================================================================================================================
     * v3 (appended; check header.size >= offsetof(KilnUiApi, set_scroll_here_y) + sizeof(void*) before calling these).
     * Layout and look for full-display screens (the Kiln mod manager uses them; so may mods). All of them work only
     * inside a frame callback (elsewhere they do nothing and return false / zeros). Sizes are pixels unless said.
     * ============================================================================================================== */

    /* push_font_size: text drawn until pop_font_size uses this size, in pixels at UI scale 1 (the UI scale is applied
     *   on top, as for the default size); the current font stays (the game's font under KILN_UI_STYLE_GAME). 0 = the
     *   style's default size. Pushes nest; a frame callback that forgets a pop is balanced at the end of the frame. */
    void(KILN_CALL* push_font_size)(float size);
    void(KILN_CALL* pop_font_size)(void);

    /* begin_child_ex: a child region of width x height (0 = the rest, < 0 = the rest minus that much) with
     *   KILN_UI_CHILD_* flags (no border unless KILN_UI_CHILD_BORDERS) and KILN_UI_WINDOW_* flags. Returns whether
     *   it is visible; call end_child either way. */
    bool(KILN_CALL* begin_child_ex)(KilnStr id, float width, float height, uint32_t child_flags, uint32_t window_flags);

    /* set_next_item_width: the width of the next widget (> 0 pixels; < 0 = the rest of the line minus that much). */
    void(KILN_CALL* set_next_item_width)(float width);

    /* cursor: where the next widget goes and the space left (KilnUiCursor; set out->size first).
     * set_cursor_pos: moves the next widget to (x, y), window-local like KilnUiCursor.x/y. */
    void(KILN_CALL* cursor)(KilnUiCursor* out);
    void(KILN_CALL* set_cursor_pos)(float x, float y);

    /* item: the rectangle and state of the widget drawn last (KilnUiItem; set out->size first). */
    void(KILN_CALL* item)(KilnUiItem* out);

    /* selectable_ex: a selectable row of width x height (0 = the rest of the line / one text line) with
     *   KILN_UI_SELECTABLE_* flags. True when clicked or activated (Enter, gamepad A). */
    bool(KILN_CALL* selectable_ex)(KilnStr label, bool selected, uint32_t flags, float width, float height);

    /* set_keyboard_focus_here: the keyboard goes to the next widget (offset 0) or the one after (1)... use it on a text
     *   field to type into it at once. set_item_default_focus: the widget drawn last gets the gamepad/keyboard
     *   navigation focus when the window appears. */
    void(KILN_CALL* set_keyboard_focus_here)(int32_t offset);
    void(KILN_CALL* set_item_default_focus)(void);

    /* draw_rect_in / draw_text_in: like draw_rect / draw_text on a KILN_UI_LAYER_*: WINDOW = the current window's own
     *   list (clipped to it, scrolls with it, under widgets drawn after it), BACKGROUND = behind every window,
     *   FOREGROUND = over everything. thickness 0 fills the rectangle, else draws its outline. Text: size in pixels
     *   (0 = the current font size), wrap_width > 0 wraps at that width, no shadow. */
    void(KILN_CALL* draw_rect_in)(uint32_t layer, float x0, float y0, float x1, float y1, uint32_t rgba, float rounding, float thickness);
    void(KILN_CALL* draw_text_in)(uint32_t layer, float x, float y, uint32_t rgba, float size, float wrap_width, KilnStr text);
    /* text_size_ex: like text_size, wrapped at wrap_width when > 0. */
    void(KILN_CALL* text_size_ex)(KilnStr text, float size, float wrap_width, float* width, float* height);

    /* push_color: a colour of the current look (KILN_UI_COLOR_*) until pop_color(count) takes `count` pushes off.
     * push_var: a spacing or rounding of the current look (KILN_UI_VAR_*; y is ignored for one-value vars) until
     *   pop_var(count). Unknown slots are ignored (and must not be popped). */
    void(KILN_CALL* push_color)(uint32_t slot, uint32_t rgba);
    void(KILN_CALL* pop_color)(uint32_t count);
    void(KILN_CALL* push_var)(uint32_t var, float x, float y);
    void(KILN_CALL* pop_var)(uint32_t count);

    /* key_pressed: KILN_UI_KEY_* went down this frame (keyboard or gamepad; only while Kiln has the input).
     *   KILN_UI_KEYQ_REPEAT also answers auto-repeat. KILN_UI_KEYQ_CLAIM takes the key from ImGui's own navigation
     *   for this frame and the next (Tab then stops moving the focus; claim it every frame you use it).
     * key_mods: KILN_UI_MOD_* held now. */
    bool(KILN_CALL* key_pressed)(uint32_t key, uint32_t flags);
    uint32_t(KILN_CALL* key_mods)(void);

    /* set_scroll_here_y: scrolls the current window so the cursor sits at ratio (0 top, 0.5 middle, 1 bottom). */
    void(KILN_CALL* set_scroll_here_y)(float ratio);

    /* ================================================================================================================
     * v4 (appended; check header.size >= offsetof(KilnUiApi, pop_font) + sizeof(void*) before calling these).
     * ============================================================================================================== */

    /* menu_page: the main-menu page's state (KilnUiMenuPage; set out->size first). Any thread. KILN_ERR_UNAVAILABLE
     *   (with out->available = 0) when the feature is off on this build.
     * menu_page_drawn: the host drew the page in this overlay frame (render thread). The page leaves by itself when no
     *   host drew it for about half a second, so a player is never left on an empty page.
     * menu_page_leave: go back to the main menu (Escape, gamepad B, the page's Back control). Any thread. The game
     *   plays its back sound and moves the camera back itself.
     * menu_page_host: 1 = a host can draw the page (the MODS entry then opens it), 0 = not any more. */
    KilnResult(KILN_CALL* menu_page)(KilnUiMenuPage* out);
    void(KILN_CALL* menu_page_drawn)(void);
    void(KILN_CALL* menu_page_leave)(void);
    void(KILN_CALL* menu_page_host)(uint32_t on);

    /* push_font: text drawn until pop_font uses a KILN_UI_FONT_* font at the current size. False (and nothing to pop)
     *   when that font is not loaded (the game's font comes from its data; it may not be there yet). Inside a frame. */
    bool(KILN_CALL* push_font)(uint32_t font);
    void(KILN_CALL* pop_font)(void);

    /* ================================================================================================================
     * v5 (appended; check header.version >= 5 and header.size >= offsetof(KilnUiApi, subscribe_window_input) +
     * sizeof(void*) before calling it).
     * ============================================================================================================== */

    /* subscribe_window_input: fn(record) for the game window's own input records, for a native plugin that keeps a key
     *   state of its own (kiln-shroudforge feeds ShroudForge's shortcut state with it). On the window thread, inside
     *   Kiln's window procedure, before Kiln handles the message, and whether or not Kiln then keeps the message from
     *   the game (the Kiln menu, a Kiln screen or text field, the radial wheel, the menu key and key bindings): what
     *   fn does changes nothing the game or Kiln receive. Records (KilnUiWindowInput):
     *   - RAW_KEY: every keyboard record of WM_INPUT. WINDOW_KEY: every WM_KEYDOWN, WM_KEYUP, WM_SYSKEYDOWN and
     *     WM_SYSKEYUP. A key usually arrives both ways (the game registers raw input without RIDEV_NOLEGACY); both are
     *     delivered, in the order the window received them, which is not always press-by-press (the window may take
     *     WM_INPUT ahead of the window messages). Nothing is merged or left out.
     *   - MOUSE_BUTTON: from each WM_INPUT mouse record (one record per button flag, presses before releases) and from
     *     each button message; a click usually arrives both ways too.
     *   - FOCUS_LOST: on WM_KILLFOCUS or WM_ACTIVATEAPP with wparam 0, once per loss: the second of the two messages
     *     of one loss gives no record. The next loss gives one again after WM_SETFOCUS, WM_ACTIVATEAPP with wparam
     *     other than 0, or any key or button record.
     *   Only what reaches Kiln's procedure: a module that subclassed the window above Kiln and keeps a message from it
     *   keeps it from fn too. Characters, mouse motion and the wheel are not delivered. While nobody subscribed the
     *   window procedure pays one relaxed load per message. A fault in fn removes the subscription, with a log line.
     *   Remove with unsubscribe: when it returns KILN_OK, a call in flight has ended and fn is not called again (from
     *   inside another kiln.ui callback it may return KILN_ERR_WRONG_THREAD instead of waiting; call it again from
     *   elsewhere). Native subscribers unsubscribe before they unload. INVALID_ARGUMENT: no fn. */
    KilnResult(KILN_CALL* subscribe_window_input)(KilnOwner owner, KilnUiWindowInputFn fn, void* user, KilnHandle* out);
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
