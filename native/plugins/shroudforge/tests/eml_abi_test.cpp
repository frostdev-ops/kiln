// SPDX-License-Identifier: MIT
// This is the layout a v1 reader compiled against, without the appended v2 slot.
#include "../../../core/abi/services/kiln_eml.h"
#include <cstddef>
struct EmlV1Prefix {
    KilnServiceHeader header;
    KilnResult(KILN_CALL* add_mod)(KilnStr, KilnStr);
    uint32_t(KILN_CALL* phase)(void);
    KilnResult(KILN_CALL* state_json)(char*, size_t, size_t*);
};
static_assert(offsetof(EmlV1Prefix, header) == offsetof(KilnEmlApi, header));
static_assert(offsetof(EmlV1Prefix, add_mod) == offsetof(KilnEmlApi, add_mod));
static_assert(offsetof(EmlV1Prefix, phase) == offsetof(KilnEmlApi, phase));
static_assert(offsetof(EmlV1Prefix, state_json) == offsetof(KilnEmlApi, state_json));
static_assert(sizeof(EmlV1Prefix) == offsetof(KilnEmlApi, stand_aside));
static_assert(sizeof(void*) != 8 || sizeof(EmlV1Prefix) == 32);
static_assert(sizeof(void*) != 8 || sizeof(KilnEmlApi) == 40);
static_assert(KILN_EML_VERSION == 2);
int main() {}
