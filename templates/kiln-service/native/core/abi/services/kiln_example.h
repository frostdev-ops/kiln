/* SPDX-License-Identifier: MIT
 * kiln.example v1: the template every Kiln service follows. Counts server-world steps. */
#ifndef KILN_EXAMPLE_H
#define KILN_EXAMPLE_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_EXAMPLE_SERVICE "kiln.example"
#define KILN_EXAMPLE_VERSION 1u

typedef struct KilnExampleApi {
    KilnServiceHeader header;               /* { sizeof(KilnExampleApi), KILN_EXAMPLE_VERSION } */
    uint64_t(KILN_CALL* steps)(void);       /* server-world steps seen since boot; any thread */
} KilnExampleApi;

#ifdef __cplusplus
}
#endif
#endif
