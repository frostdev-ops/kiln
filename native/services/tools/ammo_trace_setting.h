// SPDX-License-Identifier: MIT
#pragma once
// The switch of kiln.ui.ammo's pick trace (ammo_trace.h): kiln.json "ui": {"ammoTrace": true} (top level, not in a side
// section, as "ui": {"overlay"}), read once at the game's start by tools.cpp. The environment variable KILN_AMMO_TRACE,
// where it is set to 1 or 0, wins (as KILN_CHAT_HISTORY_TRACE does for the chat history). Host test: tests/ammo_trace.cpp.
#include <string_view>

#include "nlohmann/json.hpp"

namespace kiln::tools {
// env: 1 or 0 when KILN_AMMO_TRACE says so, -1 when it is unset or another value. kiln_json: the file's text, "" when
// there is none. Comments are allowed, as the core reads kiln.json; anything but a boolean true is off.
inline bool ammo_trace_setting(int env,std::string_view kiln_json) {
    if(env==0||env==1)return env==1;
    try {
        const auto doc=nlohmann::json::parse(kiln_json.begin(),kiln_json.end(),nullptr,false,true);
        if(!doc.is_object())return false;
        const auto ui=doc.find("ui");
        if(ui==doc.end()||!ui->is_object())return false;
        const auto v=ui->find("ammoTrace");
        return v!=ui->end()&&v->is_boolean()&&v->get<bool>();
    } catch(...) {return false;}
}
} // namespace kiln::tools
