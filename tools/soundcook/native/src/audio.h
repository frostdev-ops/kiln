/* SPDX-License-Identifier: MIT
 * kiln-soundcook's decoded audio: interleaved doubles in [-1, 1) at the source rate. */
#ifndef KILN_SOUNDCOOK_AUDIO_H
#define KILN_SOUNDCOOK_AUDIO_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t rate;
    uint32_t channels;
    uint64_t frames;
    int bits;      /* source bits per sample for integer formats, 0 for lossy/float */
    int exact16;   /* 16-bit integer source: x * 32768 gives the source samples back exactly */
    char format[16];
    double* data;  /* frames * channels, interleaved */
} kaudio;

int kaudio_decode_wav(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n);
int kaudio_decode_flac(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n);
int kaudio_decode_mp3(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n);
int kaudio_decode_ogg(const uint8_t* bytes, size_t len, kaudio* a, char* err, size_t n);

#ifdef __cplusplus
}
#endif
#endif
