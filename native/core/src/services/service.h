// SPDX-License-Identifier: MIT
// Service registry. Each service lives in src/services/<name>.cpp (+ optional <name>_*.cpp), registers itself with
// KILN_SERVICE at static-init time, and publishes a C table declared in abi/services/kiln_<name>.h.
// CMake picks up every src/services/*.cpp automatically: adding a service never touches shared build files.
//
//   static const KilnFooApi kTable = {sizeof(KilnFooApi), 1, &foo_do, ...};
//   static bool init() { /* resolve symbols, install hooks, subscribe ticks; return false = unavailable */ }
//   KILN_SERVICE(foo, "kiln.foo", 1, &kTable, &init);
//
// init() runs once during boot, on the main thread before the game's entry point, only when hooks are enabled
// (verified build or policy any), after sigdb resolution and the tick hook, before native plugins and .NET.
#pragma once
#include <string_view>

#include "../common.h"

namespace kiln::svc {

struct Service {
    const char* name;
    uint32_t version;
    const void* table;          // starts with KilnServiceHeader
    bool (*init)();             // may be null
    bool available = false;     // set by init_all
};

struct Registrar {
    explicit Registrar(Service* s);
};

#define KILN_SERVICE(id, name, version, table, init) \
    static ::kiln::svc::Service kiln_service_##id{name, version, table, init}; \
    static ::kiln::svc::Registrar kiln_service_registrar_##id{&kiln_service_##id}

void init_all();                                                   // boot
const void* find(std::string_view name, uint32_t min_version);     // KilnApi.service
bool available(std::string_view name);
// Every registered in-core service, in registration order (boot.json "services").
void each(void (*fn)(const Service& s, void* user), void* user);

}  // namespace kiln::svc
