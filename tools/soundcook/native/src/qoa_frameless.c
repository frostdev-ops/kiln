/* SPDX-License-Identifier: MIT
 * FramelessQOA encoder/decoder (see qoa_frameless.h).
 *
 * Ported from the QOA reference implementation, qoa.h (https://github.com/phoboslab/qoa, commit bc05897, MIT:
 * Copyright (c) 2023 Dominic Szablewski; the tables, the LMS predictor, qoa_div and the brute-force scale factor
 * search with the weights penalty are his). Changed for Keen's frameless layout and for byte equality with
 * tools/kfc/kfc/sound.py (whose re-encodes of the game's own sounds reproduce the game's bytes, docs/re/audio.md):
 *   1. layout: no headers, no LMS state in the stream, the predictor is never reset, slices are little-endian u64,
 *      interleaved by channel per slice index;
 *   2. the scale factor search tries 0..15 in order and keeps the first strictly best rank (qoa.h starts at the
 *      previous slice's factor; the two differ only on exact ties);
 *   3. arithmetic is 64-bit where qoa.h's int could overflow (v * reciprocal, the weights penalty); the prediction
 *      sum wraps to 32 bits before the shift, as the game's decoder does.
 */
#include "qoa_frameless.h"

#include <string.h>

static const int kq_quant[17] = {7, 7, 7, 5, 5, 3, 3, 1, 0, 0, 2, 2, 4, 4, 6, 6, 6};
static const int64_t kq_reciprocal[16] = {65536, 9363, 3121, 1457, 781, 475, 311, 216, 156, 117, 90, 71, 57, 47, 39, 32};
static const int kq_dequant[16][8] = {
    {1, -1, 3, -3, 5, -5, 7, -7},
    {5, -5, 18, -18, 32, -32, 49, -49},
    {16, -16, 53, -53, 95, -95, 147, -147},
    {34, -34, 113, -113, 203, -203, 315, -315},
    {63, -63, 210, -210, 378, -378, 588, -588},
    {104, -104, 345, -345, 621, -621, 966, -966},
    {158, -158, 528, -528, 950, -950, 1477, -1477},
    {228, -228, 760, -760, 1368, -1368, 2128, -2128},
    {316, -316, 1053, -1053, 1895, -1895, 2947, -2947},
    {422, -422, 1405, -1405, 2529, -2529, 3934, -3934},
    {548, -548, 1828, -1828, 3290, -3290, 5117, -5117},
    {696, -696, 2320, -2320, 4176, -4176, 6496, -6496},
    {868, -868, 2893, -2893, 5207, -5207, 8099, -8099},
    {1064, -1064, 3548, -3548, 6386, -6386, 9933, -9933},
    {1286, -1286, 4288, -4288, 7718, -7718, 12005, -12005},
    {1536, -1536, 5120, -5120, 9216, -9216, 14336, -14336},
};

typedef struct {
    int64_t h[4];
    int64_t w[4];
} kq_lms;

static void kq_lms_init(kq_lms* l) {
    memset(l, 0, sizeof *l);
    l->w[2] = -(1 << 13);
    l->w[3] = 1 << 14;
}

static int64_t kq_predict(const kq_lms* l) {
    int64_t v = l->h[0] * l->w[0] + l->h[1] * l->w[1] + l->h[2] * l->w[2] + l->h[3] * l->w[3];
    int32_t wrapped = (int32_t)(uint32_t)(uint64_t)v; /* 32-bit wrap, as the game's decoder */
    return (int64_t)(wrapped >> 13);
}

static void kq_update(kq_lms* l, int64_t sample, int64_t residual) {
    int64_t d = residual >> 4;
    for (int i = 0; i < 4; i++) l->w[i] += l->h[i] < 0 ? -d : d;
    l->h[0] = l->h[1];
    l->h[1] = l->h[2];
    l->h[2] = l->h[3];
    l->h[3] = sample;
}

static int64_t kq_div(int64_t v, int sf) {
    int64_t n = (v * kq_reciprocal[sf] + (1 << 15)) >> 16;
    return n + ((v > 0) - (v < 0)) - ((n > 0) - (n < 0));
}

static int64_t kq_clamp16(int64_t v) { return v < -32768 ? -32768 : v > 32767 ? 32767 : v; }

static void kq_put_le64(uint8_t* p, uint64_t v) {
    for (int i = 0; i < 8; i++) p[i] = (uint8_t)(v >> (8 * i));
}

static uint64_t kq_get_le64(const uint8_t* p) {
    uint64_t v = 0;
    for (int i = 7; i >= 0; i--) v = (v << 8) | p[i];
    return v;
}

size_t kqoa_size(uint32_t frames, uint32_t channels) {
    return ((size_t)frames + KQOA_SLICE_LEN - 1) / KQOA_SLICE_LEN * 8u * channels;
}

int kqoa_encode(const int16_t* pcm, uint32_t frames, uint32_t channels, uint8_t* out) {
    if (channels < 1 || channels > KQOA_MAX_CHANNELS) return -1;
    kq_lms lms[KQOA_MAX_CHANNELS];
    for (uint32_t c = 0; c < channels; c++) kq_lms_init(&lms[c]);
    const uint32_t slices = (uint32_t)(((size_t)frames + KQOA_SLICE_LEN - 1) / KQOA_SLICE_LEN);
    for (uint32_t si = 0; si < slices; si++) {
        const uint32_t start = si * KQOA_SLICE_LEN;
        const uint32_t len = frames - start < KQOA_SLICE_LEN ? frames - start : KQOA_SLICE_LEN;
        for (uint32_t c = 0; c < channels; c++) {
            int have_best = 0;
            int64_t best_err = 0;
            uint64_t best_slice = 0;
            kq_lms best_lms;
            for (int sf = 0; sf < 16; sf++) {
                kq_lms t = lms[c];
                int64_t err = 0;
                uint64_t slice = (uint64_t)sf;
                uint32_t i = 0;
                for (; i < len; i++) {
                    const int64_t x = pcm[(size_t)(start + i) * channels + c];
                    const int64_t p = kq_predict(&t);
                    int64_t s = kq_div(x - p, sf);
                    s = s < -8 ? -8 : s > 8 ? 8 : s;
                    const int q = kq_quant[s + 8];
                    const int64_t r = kq_dequant[sf][q];
                    const int64_t v = kq_clamp16(p + r);
                    const int64_t e = x - v;
                    err += e * e;
                    const int64_t penalty =
                        ((t.w[0] * t.w[0] + t.w[1] * t.w[1] + t.w[2] * t.w[2] + t.w[3] * t.w[3]) >> 18) - 0x8FF;
                    if (penalty > 0) err += penalty * penalty;
                    if (have_best && err >= best_err) break;
                    kq_update(&t, v, r);
                    slice = (slice << 3) | (uint64_t)q;
                }
                if (i < len) continue; /* worse than the best so far */
                slice <<= 3 * (KQOA_SLICE_LEN - len);
                if (!have_best || err < best_err) {
                    have_best = 1;
                    best_err = err;
                    best_slice = slice;
                    best_lms = t;
                }
            }
            lms[c] = best_lms;
            kq_put_le64(out + ((size_t)si * channels + c) * 8, best_slice);
        }
    }
    return 0;
}

int kqoa_decode(const uint8_t* data, size_t len, uint32_t frames, uint32_t channels, int16_t* out) {
    if (channels < 1 || channels > KQOA_MAX_CHANNELS) return -1;
    if (len < kqoa_size(frames, channels)) return -1;
    const uint32_t slices = (uint32_t)(((size_t)frames + KQOA_SLICE_LEN - 1) / KQOA_SLICE_LEN);
    for (uint32_t c = 0; c < channels; c++) {
        kq_lms l;
        kq_lms_init(&l);
        for (uint32_t si = 0; si < slices; si++) {
            const uint64_t s = kq_get_le64(data + ((size_t)si * channels + c) * 8);
            const int* dq = kq_dequant[s >> 60];
            for (uint32_t i = 0; i < KQOA_SLICE_LEN; i++) {
                const uint32_t f = si * KQOA_SLICE_LEN + i;
                if (f >= frames) break;
                const int64_t r = dq[(s >> (57 - 3 * i)) & 7];
                const int64_t v = kq_clamp16(kq_predict(&l) + r);
                kq_update(&l, v, r);
                out[(size_t)f * channels + c] = (int16_t)v;
            }
        }
    }
    return 0;
}
