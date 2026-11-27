/* SPDX-License-Identifier: MIT
 * kiln.registry v1 (abi/services/kiln_registry.h): every size, offset and permanent tag, compiled as C11 and as C++20 by
 * test-abi-headers.sh; with KILN_TEST_REGISTRY_HOST also the Kiln-internal kiln.internal.registry table
 * (native/services/content/include/kiln_registry_host.h). A change here is an ABI break, never a test update. */
#include <stddef.h>
#ifdef __cplusplus
#include <type_traits>
#endif

#include "services/kiln_registry.h"
#include "services/kiln_registry_live.h"
#ifdef KILN_TEST_REGISTRY_HOST
#include "kiln_registry_host.h"
#endif

#ifdef __cplusplus
#define KILN_TEST_ASSERT static_assert
#else
#define KILN_TEST_ASSERT _Static_assert
#endif

KILN_TEST_ASSERT(KILN_REGISTRY_VERSION == 1u, "kiln.registry v1");
KILN_TEST_ASSERT(sizeof(KilnRegistryApi) == 88, "KilnRegistryApi: header + 10 pointers (registry P2 live API uses a separate table)");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, header) == 0, "header");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, status) == 8, "status");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, schema_json) == 16, "schema_json");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, field_info) == 24, "field_info");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, subscribe_stage) == 32, "subscribe_stage");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, submit) == 40, "submit");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, read) == 48, "read");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, select) == 56, "select");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, report_json) == 64, "report_json");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, subscribe_ready) == 72, "subscribe_ready");
KILN_TEST_ASSERT(offsetof(KilnRegistryApi, unsubscribe) == 80, "unsubscribe");

KILN_TEST_ASSERT(sizeof(KilnRegistryStatus) == 40, "KilnRegistryStatus v1");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, size) == 0, "status.size");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, phase) == 4, "status.phase");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, generation) == 8, "status.generation");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, edits) == 16, "status.edits");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, refused) == 20, "status.refused");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, conflicts) == 24, "status.conflicts");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, world) == 28, "status.world");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, flags) == 32, "status.flags");
KILN_TEST_ASSERT(offsetof(KilnRegistryStatus, reserved) == 36, "status.reserved");

KILN_TEST_ASSERT(sizeof(KilnRegistryFieldInfo) == 192, "KilnRegistryFieldInfo v1");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, size) == 0, "field.size");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, applies) == 4, "field.applies");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, mapping) == 8, "field.mapping");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, type) == 12, "field.type");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, flags) == 16, "field.flags");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, reserved) == 20, "field.reserved");
KILN_TEST_ASSERT(offsetof(KilnRegistryFieldInfo, reason) == 24, "field.reason");
KILN_TEST_ASSERT(sizeof(((KilnRegistryFieldInfo*)0)->reason) == 168, "field.reason: 168 bytes");

KILN_TEST_ASSERT(sizeof(KilnRegistrySubmitResult) == 16, "KilnRegistrySubmitResult v1");
KILN_TEST_ASSERT(offsetof(KilnRegistrySubmitResult, accepted) == 4 && offsetof(KilnRegistrySubmitResult, refused) == 8 &&
                     offsetof(KilnRegistrySubmitResult, reserved) == 12,
                 "submit result fields");

KILN_TEST_ASSERT(sizeof(KilnRegistryStageInfo) == 24, "KilnRegistryStageInfo v1");
KILN_TEST_ASSERT(offsetof(KilnRegistryStageInfo, size) == 0 && offsetof(KilnRegistryStageInfo, stage) == 4 &&
                     offsetof(KilnRegistryStageInfo, mod) == 8,
                 "stage info fields");
KILN_TEST_ASSERT(sizeof(KilnRegistryReady) == 24, "KilnRegistryReady v1");
KILN_TEST_ASSERT(offsetof(KilnRegistryReady, size) == 0 && offsetof(KilnRegistryReady, phase) == 4 &&
                     offsetof(KilnRegistryReady, generation) == 8 && offsetof(KilnRegistryReady, world) == 16 &&
                     offsetof(KilnRegistryReady, reserved) == 20,
                 "ready event fields");

KILN_TEST_ASSERT(KILN_REGISTRY_APPLIES_NOW == 1 && KILN_REGISTRY_APPLIES_WORLDLOAD == 2 && KILN_REGISTRY_APPLIES_RESTART == 3,
                 "applies tags (the schema's \"applies\" and C#'s RegistryApplies: the same numbers)");
KILN_TEST_ASSERT(KILN_REGISTRY_PHASE_NONE == 0 && KILN_REGISTRY_PHASE_ADD == 1 && KILN_REGISTRY_PHASE_ALTER == 2 &&
                     KILN_REGISTRY_PHASE_FINAL == 3 && KILN_REGISTRY_PHASE_START == 4 && KILN_REGISTRY_PHASE_WORLDLOAD == 5,
                 "phase tags");
KILN_TEST_ASSERT(KILN_REGISTRY_VIEW_BASE == 0 && KILN_REGISTRY_VIEW_STAGE == 1 && KILN_REGISTRY_VIEW_RESOLVED == 2, "view tags");
KILN_TEST_ASSERT(KILN_REGISTRY_MAPPED == 0 && KILN_REGISTRY_UNMAPPED == 1 && KILN_REGISTRY_UNPROVEN == 2 && KILN_REGISTRY_REMOVED == 3,
                 "mapping tags");
KILN_TEST_ASSERT(KILN_REGISTRY_TYPE_INT == 1 && KILN_REGISTRY_TYPE_FLOAT == 2 && KILN_REGISTRY_TYPE_BOOL == 3 &&
                     KILN_REGISTRY_TYPE_ENUM == 4 && KILN_REGISTRY_TYPE_ID == 5 && KILN_REGISTRY_TYPE_LIST == 6,
                 "field type tags");
KILN_TEST_ASSERT(KILN_REGISTRY_FIELD_READ_ONLY == 1u, "field flag bits");
KILN_TEST_ASSERT(KILN_REGISTRY_STATUS_FAILED == 1u, "status flag bits");
KILN_TEST_ASSERT(KILN_REGISTRY_MAX_IR_BYTES == 16u * 1024u * 1024u, "the largest IR document");

/* every member's signature (a change is an ABI break) */
typedef uint32_t KilnTestU32;
#ifdef __cplusplus
#define KILN_TEST_SIG(member, ...) \
    KILN_TEST_ASSERT(std::is_same<decltype(KilnRegistryApi::member), __VA_ARGS__>::value, #member "'s signature")
#else
#define KILN_TEST_SIG(member, ...) \
    KILN_TEST_ASSERT(_Generic(((KilnRegistryApi*)0)->member, __VA_ARGS__: 1, default: 0), #member "'s signature")
#endif
KILN_TEST_SIG(status, KilnResult(KILN_CALL*)(KilnRegistryStatus*));
KILN_TEST_SIG(schema_json, KilnResult(KILN_CALL*)(char*, size_t, size_t*));
KILN_TEST_SIG(field_info, KilnResult(KILN_CALL*)(KilnStr, KilnRegistryFieldInfo*));
KILN_TEST_SIG(subscribe_stage, KilnResult(KILN_CALL*)(KilnOwner, KilnRegistryStageFn, void*, KilnHandle*));
KILN_TEST_SIG(submit, KilnResult(KILN_CALL*)(KilnOwner, KilnStr, KilnRegistrySubmitResult*, char*, size_t, size_t*));
KILN_TEST_SIG(read, KilnResult(KILN_CALL*)(KilnTestU32, KilnStr, KilnStr, KilnStr, char*, size_t, size_t*));
KILN_TEST_SIG(select, KilnResult(KILN_CALL*)(KilnTestU32, KilnStr, KilnStr, char*, size_t, size_t*));
KILN_TEST_SIG(report_json, KilnResult(KILN_CALL*)(char*, size_t, size_t*));
KILN_TEST_SIG(subscribe_ready, KilnResult(KILN_CALL*)(KilnOwner, KilnRegistryReadyFn, void*, KilnHandle*));
KILN_TEST_SIG(unsubscribe, KilnResult(KILN_CALL*)(KilnHandle));

#ifdef KILN_TEST_REGISTRY_HOST
KILN_TEST_ASSERT(KILN_REGISTRY_HOST_VERSION == 3u, "kiln.internal.registry v3");
KILN_TEST_ASSERT(sizeof(KilnRegistryHostApi) == 80 && offsetof(KilnRegistryHostApi, set_order) == 8 &&
                     offsetof(KilnRegistryHostApi, set_mode) == 16 && offsetof(KilnRegistryHostApi, read_pass) == 24,
                 "kiln.internal.registry v2's members in their places (read_pass, review4 NS5)");
KILN_TEST_ASSERT(offsetof(KilnRegistryHostApi, epoch) == 32 && offsetof(KilnRegistryHostApi, read_pass_in) == 40 &&
                     offsetof(KilnRegistryHostApi, prepare) == 48 && offsetof(KilnRegistryHostApi, install) == 56 &&
                     offsetof(KilnRegistryHostApi, discard) == 64 && offsetof(KilnRegistryHostApi, build_report) == 72,
                 "kiln.internal.registry v3 layout (Registry.Reauthor off the world tick, dev.32)");
KILN_TEST_ASSERT(sizeof(KilnRegistryPassCall) == 32 && offsetof(KilnRegistryPassCall, point) == 8 && offsetof(KilnRegistryPassCall, ir) == 16 &&
                     sizeof(KilnRegistryTurnItem) == 16 && offsetof(KilnRegistryTurnItem, handle) == 8 && KILN_REGISTRY_INSTALL_ANYWAY == 1u,
                 "kiln.internal.registry v3 records");
KILN_TEST_ASSERT(KILN_REGISTRY_MODE_RESOURCES_READY == 0 && KILN_REGISTRY_MODE_ONBOOT == 1, "stage modes");
#endif

int kiln_registry_abi_test_anchor;
