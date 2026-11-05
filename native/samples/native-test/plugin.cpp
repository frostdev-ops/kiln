// SPDX-License-Identifier: MIT
// native-plugin: a kiln_plugin_main plugin for the profile "plugin" entry check. Logs "native-plugin: loaded PASS".
#include "kiln_api.h"

extern "C" __declspec(dllexport) KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner,
                                                                       KilnPluginInfo* info) {
    static const char kText[] = "native-plugin: loaded from the profile PASS";
    api->log(owner, KILN_LOG_INFO, KilnStr{kText, sizeof kText - 1});
    info->id = KilnStr{"native-plugin", 13};
    info->version = KilnStr{"1.0.0", 5};
    return KILN_OK;
}
