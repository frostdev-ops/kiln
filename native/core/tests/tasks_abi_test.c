/* SPDX-License-Identifier: MIT
 * kiln.tasks v1 (abi/services/kiln_tasks.h): every size, offset and permanent tag, compiled as C11 and as C++20 by
 * test-abi-headers.sh. A change here is an ABI break, never a test update. */
#include <stddef.h>

#include "services/kiln_tasks.h"

#ifdef __cplusplus
#define KILN_TEST_ASSERT static_assert
#else
#define KILN_TEST_ASSERT _Static_assert
#endif

KILN_TEST_ASSERT(KILN_TASKS_VERSION == 1u, "kiln.tasks v1");
KILN_TEST_ASSERT(sizeof(KilnTasksApi) == 56, "KilnTasksApi: header + 5 pointers + submit_in (appended)");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, header) == 0, "header");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, submit) == 8, "submit");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, cancel) == 16, "cancel");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, status) == 24, "status");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, release) == 32, "release");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, drain_owner) == 40, "drain_owner");
KILN_TEST_ASSERT(offsetof(KilnTasksApi, submit_in) == 48, "submit_in: the first appended member, past v1's 48 bytes");

KILN_TEST_ASSERT(sizeof(KilnTaskInfo) == 32, "KilnTaskInfo v1");
KILN_TEST_ASSERT(offsetof(KilnTaskInfo, size) == 0, "info.size");
KILN_TEST_ASSERT(offsetof(KilnTaskInfo, flags) == 4, "info.flags");
KILN_TEST_ASSERT(offsetof(KilnTaskInfo, name) == 8, "info.name");
KILN_TEST_ASSERT(offsetof(KilnTaskInfo, priority) == 24, "info.priority");
KILN_TEST_ASSERT(offsetof(KilnTaskInfo, reserved) == 28, "info.reserved");

KILN_TEST_ASSERT(sizeof(KilnTaskRun) == 40, "KilnTaskRun v1");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, size) == 0, "run.size");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, reserved) == 4, "run.reserved");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, task) == 8, "run.task");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, owner) == 16, "run.owner");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, cancelled) == 24, "run.cancelled");
KILN_TEST_ASSERT(offsetof(KilnTaskRun, internal) == 32, "run.internal");

KILN_TEST_ASSERT(KILN_TASK_NONE == 0 && KILN_TASK_QUEUED == 1 && KILN_TASK_RUNNING == 2 && KILN_TASK_COMPLETED == 3 &&
                     KILN_TASK_FAULTED == 4 && KILN_TASK_CANCELLED == 5,
                 "task state tags");
KILN_TEST_ASSERT(KILN_TASK_PRIORITY_NORMAL == 0 && KILN_TASK_PRIORITY_LOW == 1 && KILN_TASK_PRIORITY_HIGH == 2,
                 "priority tags (zero = Normal)");
KILN_TEST_ASSERT(KILN_TASK_LONG_RUNNING == 1u && KILN_TASK_AFTER_STEP == 2u, "flag bits");
KILN_TEST_ASSERT(KILN_TASK_REFUSAL_NONE == 0 && KILN_TASK_REFUSAL_QUEUE_FULL == 1 && KILN_TASK_REFUSAL_OWNER_SEALED == 2 &&
                     KILN_TASK_REFUSAL_OWNER_QUARANTINED == 3 && KILN_TASK_REFUSAL_CAPABILITY == 4 &&
                     KILN_TASK_REFUSAL_ARGUMENT == 5 && KILN_TASK_REFUSAL_SHUTDOWN == 6 &&
                     KILN_TASK_REFUSAL_NO_SERVER_WORLD == 7 && KILN_TASK_REFUSAL_NO_WORLD == 8,
                 "refusal tags (NO_WORLD appended)");

int kiln_tasks_abi_test_anchor;
