// SPDX-License-Identifier: MIT
// The new internal slot must be appended; released Kiln UI calls must not move.
#include <cstddef>
#include <cstdint>
#include "services/kiln_ui.h"

static_assert(offsetof(KilnUiApi, set_texts) == 1008);
static_assert(offsetof(KilnUiApi, graphics_set) == 1016);
static_assert(offsetof(KilnUiApi, graphics_status) == 1024);
static_assert(offsetof(KilnUiApi, chat_completion) == 1032);
// subscribe_pointer_press (0.2.0-dev.30) is appended after it: the table is 1048 bytes
static_assert(offsetof(KilnUiApi, subscribe_pointer_press) == 1040);
static_assert(offsetof(KilnUiApi, chat_search) == 1048);
static_assert(sizeof(KilnUiApi) == 1056);
static_assert(offsetof(KilnUiChatSearch, query) == 64);
static_assert(offsetof(KilnUiChatSearch, lines) == 321);
static_assert(sizeof(KilnUiChatSearch) == 10572);
static_assert(offsetof(KilnUiChatCompletion, text) == 60);
static_assert(offsetof(KilnUiChatCompletion, replacement) == 317);
static_assert(sizeof(KilnUiChatCompletion) == 576);
int main() { return 0; }
