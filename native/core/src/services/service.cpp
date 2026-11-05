// SPDX-License-Identifier: MIT
#include "service.h"

#include <vector>

#include "../log.h"

namespace kiln::svc {
namespace {
std::vector<Service*>& all() {
    static std::vector<Service*> v;  // function-local: safe against static-init order
    return v;
}
}  // namespace

Registrar::Registrar(Service* s) { all().push_back(s); }

void init_all() {
    for (Service* s : all()) {
        bool ok = false;
        try {
            ok = s->init ? s->init() : true;
        } catch (const std::exception& e) {
            KLOG_E("service %s: init threw: %s", s->name, e.what());
        }
        s->available = ok;
        KLOG_I("service %s v%u: %s", s->name, s->version, ok ? "available" : "unavailable");
    }
}

const void* find(std::string_view name, uint32_t min_version) {
    for (Service* s : all())
        if (s->available && name == s->name && s->version >= min_version) return s->table;
    return nullptr;
}

bool available(std::string_view name) {
    for (Service* s : all())
        if (name == s->name) return s->available;
    return false;
}

void each(void (*fn)(const Service&, void*), void* user) {
    for (Service* s : all()) fn(*s, user);
}

}  // namespace kiln::svc
