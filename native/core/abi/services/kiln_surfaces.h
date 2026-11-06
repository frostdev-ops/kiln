/* SPDX-License-Identifier: MIT
 * kiln.surfaces v1: building-material surfaces. The client's three surface tables (BuildingMaterialBlendingResource,
 * GiVoxelBuildingMaterialResource, BuildingMaterialParametersResource) get a slot for every new material kiln.worldtables
 * placed, and a game material a MaterialTweakBlock retextures gets new maps, cooked from the mods' PNGs. Written once at boot,
 * as a kiln.worldtables table writer, before the renderer reads the tables. A dedicated server has no such tables: there it
 * does nothing. Guide: docs/kiln/services/surfaces.md.
 *
 * Fail soft: a game build whose layouts differ, or a table that doesn't read, leaves the game's tables as they are and the
 * new materials draw the shader's fallback; kiln.log says why.
 */
#ifndef KILN_SURFACES_H
#define KILN_SURFACES_H
#include "../kiln_api.h"
#ifdef __cplusplus
extern "C" {
#endif

#define KILN_SURFACES_SERVICE "kiln.surfaces"
#define KILN_SURFACES_VERSION 1u

/* state() */
enum {
    KILN_SURFACES_OFF = 0,       /* turned off, no kiln.worldtables, or this game build's layouts differ */
    KILN_SURFACES_WAITING = 1,   /* the tables haven't been written yet */
    KILN_SURFACES_NO_TABLES = 2, /* this side has no surface tables (a dedicated server) */
    KILN_SURFACES_UNTOUCHED = 3, /* nothing to write: the game's tables are served as they are */
    KILN_SURFACES_WRITTEN = 4,   /* the written tables are served */
};

typedef struct KilnSurfacesApi {
    KilnServiceHeader header; /* { sizeof(KilnSurfacesApi), KILN_SURFACES_VERSION } */
    int32_t(KILN_CALL* state)(void);
    /* JSON: state, each surface with its slot and status, errors, warnings, the streamed-texture count. */
    KilnResult(KILN_CALL* status_json)(char* json, size_t capacity, size_t* out_len);
} KilnSurfacesApi;

#ifdef __cplusplus
}
#endif
#endif
