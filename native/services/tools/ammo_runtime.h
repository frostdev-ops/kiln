// SPDX-License-Identifier: MIT
#pragma once
#include "ammo_list.h"
#include "ammo_trace.h"
#include <memory>
namespace { namespace ammo {
struct Publication;
struct Frame {
    uint32_t selected[2]{};
    std::shared_ptr<const Publication> publication[2];
    bool rebuild[2]{};
    kiln::tools::AmmoCursor entry[2]{}; // the pick trace's cursor at the build's entry (only while it is on)
    Frame(kiln::tools::SelectorGroup*,void*,uint8_t);
    ~Frame();
};
void after(Frame&,kiln::tools::SelectorGroup*,void*);
void retire(KilnOwner);
void stop();
bool start();
bool available();
bool failed();
extern const KilnUiAmmoApi table;
} }
