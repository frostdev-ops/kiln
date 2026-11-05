/* SPDX-License-Identifier: MIT
 * kiln-soundcook: Kiln's native sound cooker helper (tools/soundcook, docs/kiln/modding/sounds.md).
 *
 *   kiln-soundcook version
 *   kiln-soundcook probe IN                                  source format, rate, channels, frames (JSON)
 *   kiln-soundcook cook IN [--channels mono|stereo|keep] [--gain DB | --normalize DBFS] [--start S] [--end S]
 *                     [--seed N] [--quality 0..10] [--pcm OUT.pcm] [--qoa OUT.qoa]
 *                                                            decode -> trim -> channels -> resample to 48 kHz ->
 *                                                            gain -> int16 (TPDF dither) -> FramelessQOA (JSON stats)
 *   kiln-soundcook encode IN.pcm OUT.qoa --channels N        s16le interleaved PCM -> FramelessQOA
 *   kiln-soundcook decode IN.qoa OUT.pcm --frames N --channels N
 *   kiln-soundcook selftest
 *
 * Sources: WAV (PCM 8/16/24/32, float, A/mu-law, ADPCM), FLAC, Ogg Vorbis, MP3, detected by content. Output is
 * always 48,000 Hz (the game's only rate). The processing after decoding is in doubles, in a fixed order, with a
 * deterministic dither (splitmix64 seeded by --seed), so tools/soundcook's Python fallback (WAV at 48 kHz) gives the
 * same bytes; resampling uses the speexdsp resampler in float32 without SIMD (same output on every platform).
 * No game data is read or written here.
 */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "audio.h"
#include "qoa_frameless.h"

#define OUTSIDE_SPEEX 1
#define RANDOM_PREFIX kiln_speex
#include "speexdsp/speex_resampler.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#define KSC_VERSION 1
#define RATE 48000

/* ---------------------------------------------------------------------------------------------- files */
static FILE* open_utf8(const char* path, const char* mode) {
#ifdef _WIN32
    wchar_t wp[4096], wm[8];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wp, 4096)) return NULL;
    if (!MultiByteToWideChar(CP_UTF8, 0, mode, -1, wm, 8)) return NULL;
    return _wfopen(wp, wm);
#else
    return fopen(path, mode);
#endif
}

static uint8_t* read_all(const char* path, size_t* len) {
    FILE* f = open_utf8(path, "rb");
    if (!f) return NULL;
    size_t cap = 1 << 20, n = 0;
    uint8_t* b = (uint8_t*)malloc(cap);
    while (b) {
        if (n == cap) {
            cap *= 2;
            uint8_t* nb = (uint8_t*)realloc(b, cap);
            if (!nb) {
                free(b);
                b = NULL;
                break;
            }
            b = nb;
        }
        const size_t r = fread(b + n, 1, cap - n, f);
        if (r == 0) break;
        n += r;
    }
    fclose(f);
    *len = n;
    return b;
}

static int write_all(const char* path, const void* data, size_t len) {
    FILE* f = open_utf8(path, "wb");
    if (!f) return -1;
    const size_t w = len ? fwrite(data, 1, len, f) : 0;
    const int rc = fclose(f);
    return (w == len && rc == 0) ? 0 : -1;
}

/* ---------------------------------------------------------------------------------------------- json out */
static void json_str(FILE* o, const char* s) {
    fputc('"', o);
    for (; *s; s++) {
        const unsigned char c = (unsigned char)*s;
        if (c == '"' || c == '\\') fprintf(o, "\\%c", c);
        else if (c < 0x20) fprintf(o, "\\u%04x", c);
        else fputc(c, o);
    }
    fputc('"', o);
}

static void json_num(FILE* o, double v) {
    if (isfinite(v)) fprintf(o, "%.17g", v);
    else fputs("null", o);
}

static int die(const char* msg) {
    fputs("{\"ok\":false,\"error\":", stdout);
    json_str(stdout, msg);
    fputs("}\n", stdout);
    return 1;
}

/* ---------------------------------------------------------------------------------------------- decode */
static const char* sniff(const uint8_t* b, size_t n) {
    if (n >= 12 && (!memcmp(b, "RIFF", 4) || !memcmp(b, "RF64", 4) || !memcmp(b, "riff", 4)) && !memcmp(b + 8, "WAVE", 4)) return "wav";
    if (n >= 4 && !memcmp(b, "fLaC", 4)) return "flac";
    if (n >= 4 && !memcmp(b, "OggS", 4)) return "ogg";
    if (n >= 3 && !memcmp(b, "ID3", 3)) return "mp3";
    if (n >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return "mp3";
    return NULL;
}

static int load(const char* path, kaudio* a, char* err, size_t en) {
    memset(a, 0, sizeof *a);
    size_t len = 0;
    uint8_t* bytes = read_all(path, &len);
    if (!bytes) {
        snprintf(err, en, "cannot read %s", path);
        return -1;
    }
    const char* kind = sniff(bytes, len);
    int rc;
    if (!kind) {
        snprintf(err, en, "%s: unknown format (WAV, FLAC, Ogg Vorbis and MP3 are read)", path);
        rc = -1;
    } else if (!strcmp(kind, "wav")) rc = kaudio_decode_wav(bytes, len, a, err, en);
    else if (!strcmp(kind, "flac")) rc = kaudio_decode_flac(bytes, len, a, err, en);
    else if (!strcmp(kind, "ogg")) rc = kaudio_decode_ogg(bytes, len, a, err, en);
    else rc = kaudio_decode_mp3(bytes, len, a, err, en);
    free(bytes);
    if (rc == 0 && (a->rate < 1000 || a->rate > 768000)) {
        snprintf(err, en, "%s: sample rate %u Hz", path, a->rate);
        rc = -1;
    }
    return rc;
}

/* ---------------------------------------------------------------------------------------------- dsp */
typedef struct {
    uint64_t s;
} prng;

static uint64_t prng_next(prng* p) {
    uint64_t z = (p->s += 0x9E3779B97F4A7C15ull);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}

static double prng_unit(prng* p) { return (double)(prng_next(p) >> 11) * (1.0 / 9007199254740992.0); }

/* Active RMS (docs/re/audio.md 1.3): 50 ms blocks at 48 kHz whose RMS is above -50 dBFS. */
static void measure(const double* x, uint64_t frames, uint32_t ch, double gain, double* peak, double* rms_active) {
    const uint64_t block = 2400;
    const double floor_ms = pow(10.0, -50.0 / 10.0); /* mean square of -50 dBFS */
    double pk = 0, act_sum = 0;
    uint64_t act_n = 0;
    for (uint64_t b0 = 0; b0 < frames; b0 += block) {
        const uint64_t b1 = b0 + block < frames ? b0 + block : frames;
        double s = 0;
        for (uint64_t i = b0 * ch; i < b1 * ch; i++) {
            const double v = x[i] * gain;
            const double av = fabs(v);
            if (av > pk) pk = av;
            s += v * v;
        }
        const uint64_t n = (b1 - b0) * ch;
        if (s / (double)n > floor_ms) {
            act_sum += s;
            act_n += n;
        }
    }
    *peak = pk;
    *rms_active = act_n ? sqrt(act_sum / (double)act_n) : 0.0;
}

static double dbfs(double v) { return v > 0 ? 20.0 * log10(v) : -INFINITY; }

static int resample(kaudio* a, int quality, char* err, size_t en) {
    int e = 0;
    SpeexResamplerState* st = kiln_speex_resampler_init(a->channels, a->rate, RATE, quality, &e);
    if (!st) {
        snprintf(err, en, "resampler: %s", kiln_speex_resampler_strerror(e));
        return -1;
    }
    kiln_speex_resampler_skip_zeros(st);
    const uint32_t ch = a->channels;
    const uint64_t want = (a->frames * RATE + a->rate / 2) / a->rate;
    const uint64_t latency = (uint64_t)kiln_speex_resampler_get_input_latency(st);
    float* in = (float*)malloc((size_t)(a->frames + latency + 64) * ch * sizeof(float));
    float* out = (float*)malloc((size_t)(want + 64) * ch * sizeof(float));
    double* res = (double*)malloc((size_t)want * ch * sizeof(double));
    if (!in || !out || !res) {
        free(in);
        free(out);
        free(res);
        kiln_speex_resampler_destroy(st);
        snprintf(err, en, "out of memory");
        return -1;
    }
    for (uint64_t i = 0; i < a->frames * ch; i++) in[i] = (float)a->data[i];
    memset(in + a->frames * ch, 0, (size_t)(latency + 64) * ch * sizeof(float)); /* flush the filter */
    uint64_t in_pos = 0, out_pos = 0;
    const uint64_t in_total = a->frames + latency + 64;
    while (out_pos < want && in_pos < in_total) {
        spx_uint32_t il = (spx_uint32_t)((in_total - in_pos) > 65536 ? 65536 : (in_total - in_pos));
        spx_uint32_t ol = (spx_uint32_t)((want + 64 - out_pos) > 65536 ? 65536 : (want + 64 - out_pos));
        kiln_speex_resampler_process_interleaved_float(st, in + in_pos * ch, &il, out + out_pos * ch, &ol);
        in_pos += il;
        out_pos += ol;
        if (il == 0 && ol == 0) break;
    }
    kiln_speex_resampler_destroy(st);
    if (out_pos < want) memset(out + out_pos * ch, 0, (size_t)(want - out_pos) * ch * sizeof(float));
    for (uint64_t i = 0; i < want * ch; i++) res[i] = (double)out[i];
    free(in);
    free(out);
    free(a->data);
    a->data = res;
    a->frames = want;
    a->rate = RATE;
    return 0;
}

/* ---------------------------------------------------------------------------------------------- commands */
typedef struct {
    const char* in;
    const char* pcm_out;
    const char* qoa_out;
    const char* channels;
    double gain_db;
    int has_gain;
    double normalize;
    int has_normalize;
    double start, end;
    uint64_t seed;
    int quality;
} cook_opts;

static int cmd_cook(const cook_opts* o) {
    char err[512] = {0};
    kaudio a;
    if (load(o->in, &a, err, sizeof err)) return die(err);
    const uint32_t src_rate = a.rate, src_ch = a.channels;
    const uint64_t src_frames = a.frames;
    char src_format[16];
    memcpy(src_format, a.format, sizeof src_format);
    const int src_bits = a.bits;

    /* trim (seconds at the source rate) */
    if (o->start > 0 || o->end > 0) {
        uint64_t s0 = (uint64_t)floor(o->start * a.rate + 0.5);
        uint64_t s1 = o->end > 0 ? (uint64_t)floor(o->end * a.rate + 0.5) : a.frames;
        if (s1 > a.frames) s1 = a.frames;
        if (s0 >= s1) return die("start/end leave no audio");
        memmove(a.data, a.data + s0 * a.channels, (size_t)(s1 - s0) * a.channels * sizeof(double));
        a.frames = s1 - s0;
    }
    /* channels */
    int mixed = 0;
    uint32_t target = a.channels;
    if (!strcmp(o->channels, "mono")) target = 1;
    else if (!strcmp(o->channels, "stereo")) target = 2;
    else if (strcmp(o->channels, "keep")) return die("--channels: mono, stereo or keep");
    if (target == 1 && a.channels > 1) {
        for (uint64_t f = 0; f < a.frames; f++) {
            double s = 0;
            for (uint32_t c = 0; c < a.channels; c++) s += a.data[f * a.channels + c];
            a.data[f] = s / a.channels;
        }
        a.channels = 1;
        mixed = 1;
    } else if (target == 2 && a.channels == 1) {
        double* d = (double*)malloc((size_t)a.frames * 2 * sizeof(double));
        if (!d) return die("out of memory");
        for (uint64_t f = 0; f < a.frames; f++) d[2 * f] = d[2 * f + 1] = a.data[f];
        free(a.data);
        a.data = d;
        a.channels = 2;
    } else if (target == 2 && a.channels > 2) {
        return die("stereo from more than 2 channels is not supported: use mono or keep");
    }
    if (a.channels == 3 || a.channels == 5 || a.channels == 7) return die("the game plays 1, 2, 4, 6 or 8 channels");
    /* resample */
    const int resampled = a.rate != RATE;
    if (resampled && resample(&a, o->quality, err, sizeof err)) return die(err);
    /* gain / normalize */
    double g = 1.0, peak, rms;
    int limited = 0;
    if (o->has_gain && o->has_normalize) return die("--gain and --normalize exclude each other");
    if (o->has_gain && o->gain_db != 0) g = pow(10.0, o->gain_db / 20.0);
    if (o->has_normalize) {
        measure(a.data, a.frames, a.channels, 1.0, &peak, &rms);
        if (rms <= 0) return die("--normalize: the sound is silent (no 50 ms block above -50 dBFS)");
        g = pow(10.0, (o->normalize - dbfs(rms)) / 20.0);
        const double ceiling = pow(10.0, -1.0 / 20.0);
        if (peak * g > ceiling) {
            g = ceiling / peak;
            limited = 1;
        }
    }
    measure(a.data, a.frames, a.channels, g, &peak, &rms);
    /* to int16: exact for untouched 16-bit sources, else TPDF dither (+-1 LSB) and rounding */
    const int exact = a.exact16 && !resampled && !mixed && g == 1.0;
    const size_t n = (size_t)a.frames * a.channels;
    int16_t* pcm = (int16_t*)malloc(n * sizeof(int16_t));
    if (!pcm) return die("out of memory");
    uint64_t clipped = 0;
    prng pr = {o->seed};
    for (size_t i = 0; i < n; i++) {
        double v;
        if (exact) {
            v = a.data[i] * 32768.0;
        } else {
            const double u1 = prng_unit(&pr), u2 = prng_unit(&pr);
            v = floor(a.data[i] * g * 32768.0 + (u1 - u2) + 0.5);
        }
        if (v > 32767.0) {
            v = 32767.0;
            clipped++;
        } else if (v < -32768.0) {
            v = -32768.0;
            clipped++;
        }
        pcm[i] = (int16_t)v;
    }
    const size_t qn = kqoa_size((uint32_t)a.frames, a.channels);
    uint8_t* q = (uint8_t*)malloc(qn ? qn : 1);
    if (!q) return die("out of memory");
    if (kqoa_encode(pcm, (uint32_t)a.frames, a.channels, q)) return die("encode failed");
    if (o->pcm_out && write_all(o->pcm_out, pcm, n * sizeof(int16_t))) return die("cannot write --pcm");
    if (o->qoa_out && write_all(o->qoa_out, q, qn)) return die("cannot write --qoa");

    FILE* out = stdout;
    fprintf(out, "{\"ok\":true,\"source\":{\"format\":");
    json_str(out, src_format);
    fprintf(out, ",\"rate\":%u,\"channels\":%u,\"frames\":%llu,\"bits\":%d},", src_rate, src_ch,
            (unsigned long long)src_frames, src_bits);
    fprintf(out, "\"rate\":%d,\"channels\":%u,\"frames\":%llu,\"resampled\":%s,\"mixed\":%s,\"exact\":%s,", RATE,
            a.channels, (unsigned long long)a.frames, resampled ? "true" : "false", mixed ? "true" : "false",
            exact ? "true" : "false");
    fprintf(out, "\"gain_db\":");
    json_num(out, dbfs(g));
    fprintf(out, ",\"normalize_limited\":%s,\"peak_dbfs\":", limited ? "true" : "false");
    json_num(out, dbfs(peak));
    fprintf(out, ",\"rms_active_dbfs\":");
    json_num(out, dbfs(rms));
    fprintf(out, ",\"clipped\":%llu,\"qoa_bytes\":%llu}\n", (unsigned long long)clipped, (unsigned long long)qn);
    free(pcm);
    free(q);
    free(a.data);
    return 0;
}

static int cmd_probe(const char* in) {
    char err[512] = {0};
    kaudio a;
    if (load(in, &a, err, sizeof err)) return die(err);
    fprintf(stdout, "{\"ok\":true,\"format\":");
    json_str(stdout, a.format);
    fprintf(stdout, ",\"rate\":%u,\"channels\":%u,\"frames\":%llu,\"bits\":%d,\"seconds\":", a.rate, a.channels,
            (unsigned long long)a.frames, a.bits);
    json_num(stdout, (double)a.frames / a.rate);
    fputs("}\n", stdout);
    free(a.data);
    return 0;
}

static int cmd_encode(const char* in, const char* out, uint32_t ch) {
    size_t len = 0;
    uint8_t* b = read_all(in, &len);
    if (!b) return die("cannot read the PCM file");
    if (ch < 1 || ch > 8 || len % (2u * ch)) return die("PCM size is not a whole number of frames");
    const uint32_t frames = (uint32_t)(len / 2 / ch);
    int16_t* pcm = (int16_t*)malloc(len ? len : 2);
    for (size_t i = 0; i < len / 2; i++) pcm[i] = (int16_t)(uint16_t)(b[2 * i] | (b[2 * i + 1] << 8));
    const size_t qn = kqoa_size(frames, ch);
    uint8_t* q = (uint8_t*)malloc(qn ? qn : 1);
    kqoa_encode(pcm, frames, ch, q);
    const int rc = write_all(out, q, qn);
    free(b);
    free(pcm);
    free(q);
    if (rc) return die("cannot write the QOA file");
    fprintf(stdout, "{\"ok\":true,\"frames\":%u,\"channels\":%u,\"qoa_bytes\":%llu}\n", frames, ch, (unsigned long long)qn);
    return 0;
}

static int cmd_decode(const char* in, const char* out, uint32_t frames, uint32_t ch) {
    size_t len = 0;
    uint8_t* b = read_all(in, &len);
    if (!b) return die("cannot read the QOA file");
    int16_t* pcm = (int16_t*)malloc((size_t)frames * ch * 2 + 2);
    if (kqoa_decode(b, len, frames, ch, pcm)) return die("QOA data too short for --frames/--channels");
    uint8_t* le = (uint8_t*)malloc((size_t)frames * ch * 2 + 2);
    for (size_t i = 0; i < (size_t)frames * ch; i++) {
        le[2 * i] = (uint8_t)((uint16_t)pcm[i] & 0xFF);
        le[2 * i + 1] = (uint8_t)((uint16_t)pcm[i] >> 8);
    }
    const int rc = write_all(out, le, (size_t)frames * ch * 2);
    free(b);
    free(pcm);
    free(le);
    if (rc) return die("cannot write the PCM file");
    fprintf(stdout, "{\"ok\":true,\"frames\":%u,\"channels\":%u}\n", frames, ch);
    return 0;
}

static int cmd_selftest(void) {
    /* one all-zero slice decodes to the ramp 1, 3, 6, 10, ... (tools/kfc/tests/test_sound.py test_known_slice) */
    uint8_t z[8] = {0};
    int16_t o[20];
    kqoa_decode(z, 8, 20, 1, o);
    if (o[0] != 1 || o[1] != 3 || o[2] != 6 || o[3] != 10) return die("selftest: known slice");
    /* a 440 Hz tone round trips above 30 dB SNR */
    enum { N = 4800 };
    static int16_t pcm[N], back[N];
    for (int i = 0; i < N; i++) pcm[i] = (int16_t)lrint(8000.0 * sin(2 * 3.14159265358979323846 * 440.0 * i / RATE));
    static uint8_t q[(N + 19) / 20 * 8];
    kqoa_encode(pcm, N, 1, q);
    kqoa_decode(q, sizeof q, N, 1, back);
    double s = 0, e = 0;
    for (int i = 0; i < N; i++) {
        s += (double)pcm[i] * pcm[i];
        e += (double)(pcm[i] - back[i]) * (pcm[i] - back[i]);
    }
    const double snr = 10 * log10(s / (e > 0 ? e : 1));
    if (snr < 30) return die("selftest: tone SNR below 30 dB");
    fprintf(stdout, "{\"ok\":true,\"snr_db\":%.2f}\n", snr);
    return 0;
}

static void usage(void) {
    fputs("usage: kiln-soundcook version | probe IN | cook IN [--channels mono|stereo|keep] [--gain DB | --normalize DBFS]\n"
          "       [--start S] [--end S] [--seed N] [--quality 0..10] [--pcm OUT] [--qoa OUT]\n"
          "       | encode IN.pcm OUT.qoa --channels N | decode IN.qoa OUT.pcm --frames N --channels N | selftest\n",
          stderr);
}

int main(int argc, char** argv) {
    if (argc < 2) {
        usage();
        return 2;
    }
    const char* cmd = argv[1];
    if (!strcmp(cmd, "version")) {
        printf("{\"ok\":true,\"tool\":\"kiln-soundcook\",\"version\":%d,\"codec\":\"FramelessQOA\",\"rate\":%d,"
               "\"libs\":{\"dr_wav\":\"0.14.6\",\"dr_flac\":\"0.13.4\",\"dr_mp3\":\"0.7.4\",\"stb_vorbis\":\"1.22\","
               "\"speexdsp_resampler\":\"7a15878\"}}\n",
               KSC_VERSION, RATE);
        return 0;
    }
    if (!strcmp(cmd, "selftest")) return cmd_selftest();
    if (!strcmp(cmd, "probe") && argc == 3) return cmd_probe(argv[2]);
    if (!strcmp(cmd, "encode") && argc >= 4) {
        uint32_t ch = 1;
        for (int i = 4; i + 1 < argc; i++)
            if (!strcmp(argv[i], "--channels")) ch = (uint32_t)strtoul(argv[++i], NULL, 10);
        return cmd_encode(argv[2], argv[3], ch);
    }
    if (!strcmp(cmd, "decode") && argc >= 4) {
        uint32_t ch = 1, frames = 0;
        for (int i = 4; i + 1 < argc; i++) {
            if (!strcmp(argv[i], "--channels")) ch = (uint32_t)strtoul(argv[++i], NULL, 10);
            else if (!strcmp(argv[i], "--frames")) frames = (uint32_t)strtoul(argv[++i], NULL, 10);
        }
        return cmd_decode(argv[2], argv[3], frames, ch);
    }
    if (!strcmp(cmd, "cook") && argc >= 3) {
        cook_opts o = {0};
        o.in = argv[2];
        o.channels = "keep";
        o.quality = 10;
        for (int i = 3; i < argc; i++) {
            const char* k = argv[i];
            const char* v = i + 1 < argc ? argv[i + 1] : NULL;
            if (!v) {
                usage();
                return 2;
            }
            i++;
            if (!strcmp(k, "--channels")) o.channels = v;
            else if (!strcmp(k, "--gain")) o.gain_db = strtod(v, NULL), o.has_gain = 1;
            else if (!strcmp(k, "--normalize")) o.normalize = strtod(v, NULL), o.has_normalize = 1;
            else if (!strcmp(k, "--start")) o.start = strtod(v, NULL);
            else if (!strcmp(k, "--end")) o.end = strtod(v, NULL);
            else if (!strcmp(k, "--seed")) o.seed = strtoull(v, NULL, 10);
            else if (!strcmp(k, "--quality")) o.quality = atoi(v);
            else if (!strcmp(k, "--pcm")) o.pcm_out = v;
            else if (!strcmp(k, "--qoa")) o.qoa_out = v;
            else {
                usage();
                return 2;
            }
        }
        if (o.quality < 0 || o.quality > 10) return die("--quality: 0..10");
        return cmd_cook(&o);
    }
    usage();
    return 2;
}
