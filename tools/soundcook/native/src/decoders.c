/* SPDX-License-Identifier: MIT
 * The vendored decoders' implementations, in one translation unit (native/third_party/dr_libs, public domain/MIT-0).
 * stb_vorbis is compiled on its own (vorbis.c): it defines short macros that would leak into other code. */
#define DRWAV_API static
#define DRWAV_PRIVATE static
#define DR_WAV_IMPLEMENTATION
#define DR_WAV_NO_STDIO
#include "dr_libs/dr_wav.h"

#define DRFLAC_API static
#define DRFLAC_PRIVATE static
#define DR_FLAC_IMPLEMENTATION
#define DR_FLAC_NO_STDIO
#define DR_FLAC_NO_OGG
#include "dr_libs/dr_flac.h"

#define DRMP3_API static
#define DRMP3_PRIVATE static
#define DR_MP3_IMPLEMENTATION
#define DR_MP3_NO_STDIO
#include "dr_libs/dr_mp3.h"

#include "audio.h"

#include <stdlib.h>
#include <string.h>

static int fail(char* err, size_t n, const char* m) {
    if (err && n) {
        strncpy(err, m, n - 1);
        err[n - 1] = 0;
    }
    return -1;
}

static int alloc_out(kaudio* a, uint64_t frames, uint32_t ch, char* err, size_t n) {
    if (ch < 1 || ch > 8) return fail(err, n, "unsupported channel count (1..8)");
    if (frames == 0) return fail(err, n, "no audio frames");
    if (frames > (uint64_t)48000 * 60 * 60 * 3) return fail(err, n, "longer than 3 hours");
    a->data = (double*)malloc((size_t)frames * ch * sizeof(double));
    if (!a->data) return fail(err, n, "out of memory");
    a->frames = frames;
    a->channels = ch;
    return 0;
}

int kaudio_decode_wav(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n) {
    drwav w;
    if (!drwav_init_memory(&w, bytes, len, NULL)) return fail(err, n, "not a readable WAV file");
    int rc = -1;
    if (alloc_out(a, w.totalPCMFrameCount, w.channels, err, n) == 0) {
        a->rate = w.sampleRate;
        a->bits = w.bitsPerSample;
        const size_t total = (size_t)a->frames * a->channels;
        if (w.translatedFormatTag == DR_WAVE_FORMAT_PCM) {
            /* integers: read as s32 (left-justified, exact for 8..32 bits) */
            int32_t* tmp = (int32_t*)malloc(total * sizeof(int32_t));
            if (tmp) {
                const uint64_t got = drwav_read_pcm_frames_s32(&w, a->frames, tmp);
                for (size_t i = 0; i < (size_t)got * a->channels; i++) a->data[i] = (double)tmp[i] / 2147483648.0;
                a->frames = got;
                strcpy(a->format, "wav-pcm");
                a->exact16 = a->bits == 16;
                free(tmp);
                rc = got ? 0 : fail(err, n, "no audio frames");
            } else {
                rc = fail(err, n, "out of memory");
            }
        } else {
            /* IEEE float (32/64-bit), A-law, mu-law, ADPCM: through dr_wav's f32 conversion */
            float* tmp = (float*)malloc(total * sizeof(float));
            if (tmp) {
                const uint64_t got = drwav_read_pcm_frames_f32(&w, a->frames, tmp);
                for (size_t i = 0; i < (size_t)got * a->channels; i++) a->data[i] = (double)tmp[i];
                a->frames = got;
                strcpy(a->format, w.translatedFormatTag == DR_WAVE_FORMAT_IEEE_FLOAT ? "wav-float" : "wav-other");
                free(tmp);
                rc = got ? 0 : fail(err, n, "no audio frames");
            } else {
                rc = fail(err, n, "out of memory");
            }
        }
    }
    drwav_uninit(&w);
    return rc;
}

int kaudio_decode_flac(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n) {
    drflac* f = drflac_open_memory(bytes, len, NULL);
    if (!f) return fail(err, n, "not a readable FLAC file");
    int rc = -1;
    if (alloc_out(a, f->totalPCMFrameCount, f->channels, err, n) == 0) {
        a->rate = f->sampleRate;
        a->bits = f->bitsPerSample;
        int32_t* tmp = (int32_t*)malloc((size_t)a->frames * a->channels * sizeof(int32_t));
        if (tmp) {
            const uint64_t got = drflac_read_pcm_frames_s32(f, a->frames, tmp);
            for (size_t i = 0; i < (size_t)got * a->channels; i++) a->data[i] = (double)tmp[i] / 2147483648.0;
            a->frames = got;
            strcpy(a->format, "flac");
            a->exact16 = a->bits == 16;
            free(tmp);
            rc = got ? 0 : fail(err, n, "no audio frames");
        } else {
            rc = fail(err, n, "out of memory");
        }
    }
    drflac_close(f);
    return rc;
}

int kaudio_decode_mp3(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n) {
    drmp3 m;
    if (!drmp3_init_memory(&m, bytes, len, NULL)) return fail(err, n, "not a readable MP3 file");
    int rc = -1;
    const uint64_t frames = drmp3_get_pcm_frame_count(&m);
    if (alloc_out(a, frames, m.channels, err, n) == 0) {
        a->rate = m.sampleRate;
        a->bits = 0;
        float* tmp = (float*)malloc((size_t)a->frames * a->channels * sizeof(float));
        if (tmp) {
            const uint64_t got = drmp3_read_pcm_frames_f32(&m, a->frames, tmp);
            for (size_t i = 0; i < (size_t)got * a->channels; i++) a->data[i] = (double)tmp[i];
            a->frames = got;
            strcpy(a->format, "mp3");
            free(tmp);
            rc = got ? 0 : fail(err, n, "no audio frames");
        } else {
            rc = fail(err, n, "out of memory");
        }
    }
    drmp3_uninit(&m);
    return rc;
}
