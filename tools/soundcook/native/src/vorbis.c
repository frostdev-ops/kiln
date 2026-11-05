/* SPDX-License-Identifier: MIT
 * Ogg Vorbis sources through stb_vorbis (native/third_party/stb_vorbis, public domain/MIT), its own translation
 * unit because stb_vorbis.c defines short macros. */
#define STB_VORBIS_NO_STDIO
#define STB_VORBIS_NO_PUSHDATA_API
#include "stb_vorbis/stb_vorbis.c"

#include "audio.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int kaudio_decode_ogg(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n) {
    int e = 0;
    stb_vorbis* v = stb_vorbis_open_memory(bytes, (int)len, &e, NULL);
    if (!v) {
        if (err && n) snprintf(err, n, "not a readable Ogg Vorbis file (stb_vorbis error %d)", e);
        return -1;
    }
    const stb_vorbis_info info = stb_vorbis_get_info(v);
    const unsigned total = stb_vorbis_stream_length_in_samples(v);
    int rc = -1;
    if (info.channels < 1 || info.channels > 8 || total == 0) {
        if (err && n) snprintf(err, n, "Ogg Vorbis: %d channels, %u frames", info.channels, total);
    } else {
        /* the length from the last page's granule position; read until the stream ends, growing if needed */
        size_t cap = (size_t)total + 4096, got = 0;
        float* buf = (float*)malloc(cap * info.channels * sizeof(float));
        while (buf) {
            if (got + 4096 > cap) {
                cap *= 2;
                float* nb = (float*)realloc(buf, cap * info.channels * sizeof(float));
                if (!nb) {
                    free(buf);
                    buf = NULL;
                    break;
                }
                buf = nb;
            }
            const int r = stb_vorbis_get_samples_float_interleaved(v, info.channels, buf + got * info.channels,
                                                                   (int)(4096 * info.channels));
            if (r <= 0) break;
            got += (size_t)r;
        }
        if (!buf) {
            if (err && n) snprintf(err, n, "out of memory");
        } else if (!got) {
            if (err && n) snprintf(err, n, "no audio frames");
            free(buf);
        } else {
            a->data = (double*)malloc(got * info.channels * sizeof(double));
            if (a->data) {
                for (size_t i = 0; i < got * info.channels; i++) a->data[i] = (double)buf[i];
                a->frames = got;
                a->channels = (uint32_t)info.channels;
                a->rate = info.sample_rate;
                a->bits = 0;
                strcpy(a->format, "ogg-vorbis");
                rc = 0;
            } else if (err && n) {
                snprintf(err, n, "out of memory");
            }
            free(buf);
        }
    }
    stb_vorbis_close(v);
    return rc;
}
