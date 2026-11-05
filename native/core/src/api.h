// SPDX-License-Identifier: MIT
#pragma once
#include <string>
#include <vector>

#include "common.h"

namespace kiln {

const KilnApi* api();
void set_runtime_state(bool verified, bool hooks_enabled);
bool hooks_enabled();
void load_plugins();

// boot.json "plugins": every plugin DLL handled by load_plugins (services are not listed).
struct PluginRecord {
    std::string file, path, id, version;
    const char* status;  // loaded | failed | skipped
    std::string error;
};
const std::vector<PluginRecord>& plugin_records();
std::vector<std::string> published_services();  // "<name>@<version>" of services DLLs published at run time

}  // namespace kiln
