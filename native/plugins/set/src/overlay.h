// SPDX-License-Identifier: MIT
// SET-specific tools, independent of the managed UI's engine pages.
#pragma once
#include <filesystem>
#include <string>
#include "services/kiln_ui.h"

namespace setui {
struct Api {
    KilnServiceHeader header;
    KilnResult(KILN_CALL* open)(KilnStr mod); // empty: all mods; any thread
};
const Api* service();
void configure(std::filesystem::path logs, std::filesystem::path control, void (*sink)(const char*) = nullptr);
void bind(const KilnUiApi* ui, KilnOwner owner);
void draw();                         // render thread; never enters Lua or reads entity memory
void stop();                        // no further tools or captured input
bool isOpen();
void line(const char* text);         // bounded console history, any thread
std::string logText(bool previous); // redacted payload; no clipboard/file writes
}
