/* SPDX-License-Identifier: MIT
 * FramelessQOA: the QOA codec as Keen's engine stores it (docs/re/audio.md 1.2): no file header, no frame headers,
 * no stored LMS state; one little-endian u64 per 20 samples of one channel, slices interleaved by channel, the
 * predictor running over the whole sound from QOA's encoder start state.
 *
 * The encoder is a port of the QOA reference encoder (qoa.h, https://github.com/phoboslab/qoa, MIT,
 * Copyright (c) 2023 Dominic Szablewski) to that layout. It matches Kiln's Python reference
 * (tools/kfc/kfc/sound.py: encode_qoa) byte for byte; see qoa_frameless.c for the two deliberate differences from qoa.h.
 */
#ifndef KILN_QOA_FRAMELESS_H
#define KILN_QOA_FRAMELESS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define KQOA_SLICE_LEN 20
#define KQOA_MAX_CHANNELS 8

/* Bytes of a FramelessQOA stream: ceil(frames / 20) * 8 * channels. */
size_t kqoa_size(uint32_t frames, uint32_t channels);

/* Interleaved int16 PCM (frames x channels) -> FramelessQOA bytes. `out` must hold kqoa_size(frames, channels)
 * bytes. Unused residuals of a short last slice are zero. Returns 0, or -1 for a bad channel count. */
int kqoa_encode(const int16_t* pcm, uint32_t frames, uint32_t channels, uint8_t* out);

/* FramelessQOA bytes -> interleaved int16 PCM (frames x channels). `len` must be at least kqoa_size(). Returns 0,
 * or -1 when the input is too short or the channel count is bad. */
int kqoa_decode(const uint8_t* data, size_t len, uint32_t frames, uint32_t channels, int16_t* out);

#ifdef __cplusplus
}
#endif
#endif
