// SPDX-License-Identifier: MIT
// The barber's and the character screen's hair and beard lists, on a client: FbUiBundle.characterCustomizationOptions.hairItems and .beardItems. A new item whose
// icon item or base item is one of the game's listed styles is listed too, with that style's entry (its preview, its knowledge query) naming the new item.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace kc {

struct BarberItem {
    uint8_t guid[16];  // the new item
    uint8_t base[16];  // the item it is a clone of
    uint8_t icon[16];  // the item whose picture it shows (zero: its base)
};

// Client: the lists become the game's own and an entry for each item that has a listed style to start from. Rebuilt from the game's lists when the set
// changes. Without the UI bundle (a dedicated server) it does nothing. Any thread that may touch the game's resources (the world's).
void barber_sync(const std::vector<BarberItem>& items);
std::string barber_report();

}  // namespace kc
