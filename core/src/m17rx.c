/*
 * libm17rx - M17 receiver core
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 *
 * Signal path:
 *   complex IQ (any rate) -> FM discriminator (Hz) -> resampler to 48 kHz
 *   -> RRC matched filter (unity DC gain) -> frame sync -> decode
 *
 * Frame sync works per frame: every 40 ms frame starts with a known 8-symbol
 * syncword. The receiver finds it by normalised correlation (insensitive to
 * deviation and carrier offset), refines its position to a fraction of a
 * sample, and samples the 184 payload symbols from there. Gain (deviation) and
 * DC (carrier offset) come from a least-squares fit, first to the syncword and,
 * once a frame has decoded, to all 192 symbols of the re-encoded frame. That
 * data-aided fit also gives EVM, MER and the eye statistics.
 */
#define M17RX_BUILD 1
#include "internal.h"
#include "crypto.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

static int dbg_on = -1;
#define DBG(...) do { if (dbg_on < 0) dbg_on = getenv("M17RX_DEBUG") != NULL; if (dbg_on) fprintf(stderr, __VA_ARGS__); } while (0)

#define VERSION_STR     "libm17rx 0.2.0"

#define BUF_CAP         (32768)
#define FRAME_SMP       (SYM_PER_FRA * M17RX_SPS)     /* 1920 */
#define SYNC_BACK       ((SYM_PER_SWD - 1) * M17RX_SPS) /* first sync symbol is 70 samples before the last */
#define PLD_AHEAD       (SYM_PER_PLD * M17RX_SPS)     /* last payload symbol is 1840 after the last sync symbol */
#define EYE_HALF        (M17RX_EYE_POINTS / 2)        /* 10 samples = 1 symbol */
#define NEED_AHEAD      (PLD_AHEAD + EYE_HALF + 4)
#define NEED_BACK       (SYNC_BACK + EYE_HALF + 4)
#define SEARCH_WIN      4
#define EYE_RING        768
#define AUDIO_CAP       (8000 * 8)
#define EVQ_CAP         32
#define RRC_DELAY       40

enum { SW_LSF, SW_STR, SW_PKT, SW_EOT, SW_N };

struct m17rx
{
    m17rx_config_t cfg;
    double fs_in;

    /* front end */
    int have_prev;
    float prev_i, prev_q;
    m17_resampler_t rs;
    int rs_ok;
    m17_fir_t rrc;
    float delay[RRC_DELAY];
    int delay_pos;
    float *tmp_hz, *tmp_48k;

    /* 48 kHz sample history: matched-filter output and the aligned raw discriminator */
    float *buf, *raw;
    int64_t base; /* absolute index of buf[0] */
    int len;

    /* frame sync */
    float pat[SW_N][8];   /* syncword symbols (+-3) */
    float patz[SW_N][8];  /* mean-removed */
    float patn[SW_N];     /* norm of patz */
    int64_t scan;         /* next index to test while searching */
    int locked;
    double last_pos;      /* position of the last syncword symbol of the last frame */
    int miss;
    int have_prev_pos;
    float a_trk, b_trk;   /* gain (Hz per symbol unit) and DC (Hz) */
    int trk_valid;
    float last_frac;

    /* decoding */
    viterbi_ctx_t vit;
    codec2_decoder_t c2;

    /* stream / packet state */
    int stream_active, start_reported, packet_active;
    uint8_t lich[30];
    int lich_map;
    m17_text_t text;
    m17rx_link_t link;
    int expected_fn;
    int frames, lost;
    double evm_sum;
    int evm_n;
    uint8_t pkt[M17RX_PKT_MAX];
    int pkt_len;

    /* keys */
    m17_aes_t aes;
    int aes_len;
    uint32_t scr_seed;
    int scr_bits;
    struct { char name[32]; uint8_t key[64]; } pub[M17RX_MAX_PUBKEYS];
    int npub;

    /* per-stream crypto state */
    uint32_t scr_state;
    int scr_next_fn;      /* FN the scrambler state belongs to, -1 unknown */
    uint8_t digest[16];
    int digest_ok;        /* digest covers every frame from FN 0 */
    uint8_t sig[64];
    int sig_map;          /* signature frames received, bit 0 = FN 7FFC */
    int sig_state;
    char sig_key[32];

    /* outputs */
    int16_t *audio;
    int a_head, a_len;
    m17rx_event_t *evq;
    int ev_head, ev_len;

    /* measurements */
    m17rx_status_t meas; /* only the measurement fields are used */
    int meas_init;
    float eye[EYE_RING][M17RX_EYE_POINTS];
    int eye_head, eye_count;
    float hist[M17RX_HIST_BINS];
    float evm_hist[M17RX_EVM_HISTORY];
    int evm_hist_n;
    float sync_q;
    int64_t inverted_until; /* sample index until which the inverted-signal hint is shown */
};

/* ------------------------------------------------------------------ */

M17RX_API int M17RX_CALL m17rx_api_version(void) { return M17RX_API_VERSION; }
M17RX_API const char *M17RX_CALL m17rx_version(void) { return VERSION_STR " (libm17 " LIBM17_VERSION ")"; }

M17RX_API int M17RX_CALL m17rx_struct_size(int which)
{
    switch (which)
    {
    case 0: return (int)sizeof(m17rx_link_t);
    case 1: return (int)sizeof(m17rx_status_t);
    case 2: return (int)sizeof(m17rx_event_t);
    case 3: return (int)sizeof(m17rx_config_t);
    default: return -1;
    }
}

M17RX_API void M17RX_CALL m17rx_default_config(m17rx_config_t *c)
{
    memset(c, 0, sizeof(*c));
    c->sync_threshold = 0.90f;
    c->track_threshold = 0.55f;
    c->invert = 0;
    c->viterbi_mute = 0.08f;
    c->eye_source = 0;
}

static void push_event(m17rx_t *rx, const m17rx_event_t *ev)
{
    if (rx->ev_len == EVQ_CAP) /* full: drop the oldest */
    {
        rx->ev_head = (rx->ev_head + 1) % EVQ_CAP;
        rx->ev_len--;
    }
    rx->evq[(rx->ev_head + rx->ev_len) % EVQ_CAP] = *ev;
    rx->ev_len++;
}

static void push_audio(m17rx_t *rx, const int16_t *pcm, int n)
{
    for (int i = 0; i < n; i++)
    {
        if (rx->a_len == AUDIO_CAP)
        {
            rx->a_head = (rx->a_head + 1) % AUDIO_CAP;
            rx->a_len--;
        }
        rx->audio[(rx->a_head + rx->a_len) % AUDIO_CAP] = pcm[i];
        rx->a_len++;
    }
}

static void push_silence(m17rx_t *rx, int n)
{
    int16_t z[SAMPLES_PER_FRAME] = {0};
    while (n > 0)
    {
        int k = n < SAMPLES_PER_FRAME ? n : SAMPLES_PER_FRAME;
        push_audio(rx, z, k);
        n -= k;
    }
}

static void make_patterns(m17rx_t *rx)
{
    const uint16_t sw[SW_N] = {SYNC_LSF, SYNC_STR, SYNC_PKT, EOT_MRKR};
    for (int t = 0; t < SW_N; t++)
    {
        float mean = 0, nn = 0;
        for (int k = 0; k < 8; k++)
        {
            rx->pat[t][k] = symbol_map[(sw[t] >> (14 - 2 * k)) & 3];
            mean += rx->pat[t][k];
        }
        mean /= 8;
        for (int k = 0; k < 8; k++)
        {
            rx->patz[t][k] = rx->pat[t][k] - mean;
            nn += rx->patz[t][k] * rx->patz[t][k];
        }
        rx->patn[t] = sqrtf(nn);
    }
}

static void reset_stream_state(m17rx_t *rx)
{
    rx->stream_active = rx->start_reported = rx->packet_active = 0;
    rx->lich_map = 0;
    rx->expected_fn = -1;
    rx->frames = rx->lost = 0;
    rx->evm_sum = 0;
    rx->evm_n = 0;
    rx->pkt_len = 0;
    rx->scr_next_fn = -1;
    memset(rx->digest, 0, sizeof(rx->digest));
    rx->digest_ok = 0;
    rx->sig_map = 0;
    rx->sig_state = M17RX_SIG_NONE;
    rx->sig_key[0] = 0;
}

/* ------------------------------------------------------------------ keys */

static int decrypt_state(const m17rx_t *rx, const m17rx_link_t *l)
{
    if (!l->valid || !l->stream || l->enc_type == M17RX_ENC_NONE)
        return M17RX_DEC_CLEAR;
    if (l->enc_subtype > 2 || l->enc_type == M17RX_ENC_RESERVED)
        return M17RX_DEC_UNSUPPORTED;
    if (l->enc_type == M17RX_ENC_AES)
        return rx->aes_len == 16 + 8 * l->enc_subtype ? M17RX_DEC_OK : M17RX_DEC_NO_KEY;
    return rx->scr_bits == 8 * (l->enc_subtype + 1) ? M17RX_DEC_OK : M17RX_DEC_NO_KEY;
}

M17RX_API int M17RX_CALL m17rx_set_aes_key(m17rx_t *rx, const uint8_t *key, int len)
{
    if (len == 0 || !key)
        rx->aes_len = 0;
    else if (m17_aes_init(&rx->aes, key, len) == 0)
        rx->aes_len = len;
    else
        return -1;
    rx->link.decrypt = decrypt_state(rx, &rx->link);
    return 0;
}

M17RX_API int M17RX_CALL m17rx_set_scrambler_seed(m17rx_t *rx, uint32_t seed, int bits)
{
    if (bits != 0 && bits != 8 && bits != 16 && bits != 24)
        return -1;
    rx->scr_bits = bits;
    rx->scr_seed = bits ? seed & ((1u << bits) - 1) : 0;
    rx->scr_next_fn = -1;
    rx->link.decrypt = decrypt_state(rx, &rx->link);
    return 0;
}

M17RX_API void M17RX_CALL m17rx_clear_public_keys(m17rx_t *rx) { rx->npub = 0; }

M17RX_API int M17RX_CALL m17rx_add_public_key(m17rx_t *rx, const char *name, const uint8_t *key, int len)
{
    if (rx->npub >= M17RX_MAX_PUBKEYS || !key)
        return -1;
    uint8_t k[64];
    if (m17_pubkey_parse(key, len, k) != 0)
        return -1;
    memcpy(rx->pub[rx->npub].key, k, 64);
    snprintf(rx->pub[rx->npub].name, sizeof(rx->pub[0].name), "%s", name ? name : "");
    rx->npub++;
    return 0;
}

static int same_call(const char *a, const char *b)
{
    for (; *a && *b; a++, b++)
        if ((*a | 0x20) != (*b | 0x20))
            return 0;
    return *a == *b;
}

/* all four signature frames are in: check them with the public key stored under the sender's
   callsign (case-insensitive). Keys under other names are never tried. */
static void verify_signature(m17rx_t *rx)
{
    if (rx->sig_map != 0xF)
        rx->sig_state = M17RX_SIG_MISSING;
    else if (!rx->digest_ok)
        rx->sig_state = M17RX_SIG_INCOMPLETE;
    else
    {
        rx->sig_state = M17RX_SIG_NO_KEY;
        for (int i = 0; i < rx->npub; i++)
        {
            if (!same_call(rx->pub[i].name, rx->link.src))
                continue;
            if (m17_sig_verify(rx->pub[i].key, rx->digest, rx->sig))
            {
                rx->sig_state = M17RX_SIG_VALID;
                snprintf(rx->sig_key, sizeof(rx->sig_key), "%s", rx->pub[i].name);
                break;
            }
            rx->sig_state = M17RX_SIG_INVALID;
        }
    }
    DBG("signature: state %d key '%s'\n", rx->sig_state, rx->sig_key);
}

M17RX_API void M17RX_CALL m17rx_reset(m17rx_t *rx)
{
    rx->have_prev = 0;
    if (rx->rs_ok)
        m17_resampler_reset(&rx->rs);
    m17_fir_reset(&rx->rrc);
    memset(rx->delay, 0, sizeof(rx->delay));
    rx->delay_pos = 0;
    rx->base = 0;
    rx->len = 0;
    rx->scan = NEED_BACK;
    rx->locked = 0;
    rx->miss = 0;
    rx->have_prev_pos = 0;
    rx->trk_valid = 0;
    viterbi_reset_ctx(&rx->vit);
    codec2_decoder_init(&rx->c2);
    reset_stream_state(rx);
    memset(&rx->link, 0, sizeof(rx->link));
    m17_text_reset(&rx->text);
    rx->a_head = rx->a_len = 0;
    rx->ev_head = rx->ev_len = 0;
    memset(&rx->meas, 0, sizeof(rx->meas));
    rx->meas_init = 0;
    rx->eye_head = rx->eye_count = 0;
    memset(rx->hist, 0, sizeof(rx->hist));
    rx->evm_hist_n = 0;
    rx->sync_q = 0;
    rx->inverted_until = 0;
}

M17RX_API m17rx_t *M17RX_CALL m17rx_create(void)
{
    m17rx_t *rx = (m17rx_t *)calloc(1, sizeof(m17rx_t));
    if (!rx)
        return NULL;
    m17rx_default_config(&rx->cfg);

    float sum = 0;
    for (int i = 0; i < 81; i++)
        sum += rrc_taps_10[i];
    rx->buf = (float *)calloc(BUF_CAP, sizeof(float));
    rx->raw = (float *)calloc(BUF_CAP, sizeof(float));
    rx->tmp_hz = (float *)calloc(4096, sizeof(float));
    rx->tmp_48k = (float *)calloc(8192, sizeof(float));
    rx->audio = (int16_t *)calloc(AUDIO_CAP, sizeof(int16_t));
    rx->evq = (m17rx_event_t *)calloc(EVQ_CAP, sizeof(m17rx_event_t));
    if (!rx->buf || !rx->raw || !rx->tmp_hz || !rx->tmp_48k || !rx->audio || !rx->evq ||
        m17_fir_init(&rx->rrc, rrc_taps_10, 81, 1.0f / sum) != 0)
    {
        m17rx_destroy(rx);
        return NULL;
    }
    make_patterns(rx);
    m17rx_set_input_rate(rx, M17RX_FS);
    m17rx_reset(rx);
    return rx;
}

M17RX_API void M17RX_CALL m17rx_destroy(m17rx_t *rx)
{
    if (!rx)
        return;
    if (rx->rs_ok)
        m17_resampler_free(&rx->rs);
    m17_fir_free(&rx->rrc);
    free(rx->buf);
    free(rx->raw);
    free(rx->tmp_hz);
    free(rx->tmp_48k);
    free(rx->audio);
    free(rx->evq);
    free(rx);
}

M17RX_API void M17RX_CALL m17rx_set_config(m17rx_t *rx, const m17rx_config_t *cfg)
{
    rx->cfg = *cfg;
    if (rx->cfg.sync_threshold < 0.5f || rx->cfg.sync_threshold > 0.99f)
        rx->cfg.sync_threshold = 0.90f;
    if (rx->cfg.track_threshold < 0.2f || rx->cfg.track_threshold > rx->cfg.sync_threshold)
        rx->cfg.track_threshold = 0.55f;
    if (rx->cfg.viterbi_mute <= 0.0f)
        rx->cfg.viterbi_mute = 0.08f;
}

M17RX_API int M17RX_CALL m17rx_set_input_rate(m17rx_t *rx, double fs)
{
    if (fs < 8000.0 || fs > 2.4e6)
        return -1;
    if (rx->rs_ok && fabs(fs - rx->fs_in) < 1e-6)
        return 0;
    if (rx->rs_ok)
        m17_resampler_free(&rx->rs);
    rx->rs_ok = (m17_resampler_init(&rx->rs, fs, M17RX_FS) == 0);
    rx->fs_in = fs;
    rx->have_prev = 0;
    return rx->rs_ok ? 0 : -1;
}

/* ------------------------------------------------------------------ */
/* Sample history                                                      */
/* ------------------------------------------------------------------ */

static inline float at(const m17rx_t *rx, const float *b, double pos)
{
    double rel = pos - (double)rx->base;
    int i = (int)floor(rel);
    float mu = (float)(rel - i);
    return m17_interp_cubic(b, i, mu);
}

/* Normalised correlation of the syncword ending at integer index n with pattern t. */
static float corr(const m17rx_t *rx, int64_t n, int t)
{
    const float *x = &rx->buf[(n - rx->base) - SYNC_BACK];
    float mean = 0;
    for (int k = 0; k < 8; k++)
        mean += x[k * M17RX_SPS];
    mean /= 8;
    float num = 0, den = 0;
    for (int k = 0; k < 8; k++)
    {
        float d = x[k * M17RX_SPS] - mean;
        num += d * rx->patz[t][k];
        den += d * d;
    }
    if (den <= 1e-12f)
        return 0;
    return num / (sqrtf(den) * rx->patn[t]);
}

static void drop_old(m17rx_t *rx, int incoming)
{
    int64_t keep = rx->locked ? (int64_t)floor(rx->last_pos) - NEED_BACK : rx->scan - NEED_BACK - SEARCH_WIN - 2;
    if (keep < rx->base)
        keep = rx->base;
    int drop = (int)(keep - rx->base);
    if (rx->len + incoming - drop > BUF_CAP) /* never happens with sane block sizes */
        drop = rx->len + incoming - BUF_CAP;
    if (drop > rx->len)
        drop = rx->len;
    if (drop <= 0)
        return;
    memmove(rx->buf, rx->buf + drop, sizeof(float) * (size_t)(rx->len - drop));
    memmove(rx->raw, rx->raw + drop, sizeof(float) * (size_t)(rx->len - drop));
    rx->len -= drop;
    rx->base += drop;
    if (rx->scan < rx->base + NEED_BACK)
        rx->scan = rx->base + NEED_BACK;
}

/* ------------------------------------------------------------------ */
/* Measurements                                                        */
/* ------------------------------------------------------------------ */

static int level_index(float ideal)
{
    return ideal < -2 ? 0 : ideal < 0 ? 1 : ideal < 2 ? 2 : 3;
}

static float nearest_level(float y)
{
    return y < -2 ? -3.0f : y < 0 ? -1.0f : y < 2 ? 1.0f : 3.0f;
}

/* Gray-coded dibit of a level, as in libm17's symbol_map {+1,+3,-1,-3} for 00,01,10,11. */
static int level_dibit(float v)
{
    return v > 2 ? 1 : v > 0 ? 0 : v > -2 ? 2 : 3;
}

static void smooth(float *dst, float v, int init, float alpha)
{
    *dst = init ? v : *dst + alpha * (v - *dst);
}

/* s: raw sampled symbols (Hz), ideal: reference levels, n = 192. */
static void measure_frame(m17rx_t *rx, const float *s, const float *ideal, int data_aided,
                          float a, float b, float vber)
{
    const int n = SYM_PER_FRA;
    m17rx_status_t *m = &rx->meas;
    int init = !rx->meas_init;

    double se = 0, si = 0, peak = 0;
    double lsum[4] = {0}, lsq[4] = {0};
    int lcnt[4] = {0}, biterr = 0;
    for (int k = 0; k < n; k++)
    {
        float y = (s[k] - b) / a;
        float e = y - ideal[k];
        se += (double)e * e;
        si += (double)ideal[k] * ideal[k];
        if (fabsf(e) > peak)
            peak = fabsf(e);
        int li = level_index(ideal[k]);
        lsum[li] += y;
        lsq[li] += (double)y * y;
        lcnt[li]++;
        int d = level_dibit(nearest_level(y)) ^ level_dibit(ideal[k]);
        biterr += (d & 1) + ((d >> 1) & 1);
    }
    float ref = (float)sqrt(si / n);
    float evm = (float)(sqrt(se / n) / ref * 100.0);
    float evm_peak = (float)(peak / ref * 100.0);
    float mer = (float)(10.0 * log10(si / (se > 1e-12 ? se : 1e-12)));

    float mu[4], sg[4];
    for (int i = 0; i < 4; i++)
    {
        static const float nominal[4] = {-3, -1, 1, 3};
        if (lcnt[i])
        {
            mu[i] = (float)(lsum[i] / lcnt[i]);
            double var = lsq[i] / lcnt[i] - (double)mu[i] * mu[i];
            sg[i] = (float)sqrt(var > 0 ? var : 0);
        }
        else
        {
            mu[i] = init ? nominal[i] : m->level_mean[i];
            sg[i] = init ? 0 : m->level_sigma[i];
        }
    }
    /* statistical eye: gap between adjacent levels at 3 sigma, 2.0 = fully open */
    float open = 100.0f;
    for (int i = 0; i < 3; i++)
    {
        float gap = (mu[i + 1] - 3 * sg[i + 1]) - (mu[i] + 3 * sg[i]);
        float pct = gap / 2.0f * 100.0f;
        if (pct < open)
            open = pct;
    }
    if (open < 0)
        open = 0;
    float ratio = (mu[3] - mu[0]) > 1e-6f ? (mu[2] - mu[1]) / (mu[3] - mu[0]) : 0;

    const float al = 0.25f;
    smooth(&m->evm_rms_pct, evm, init, al);
    m->evm_peak_pct = evm_peak;
    smooth(&m->mer_db, mer, init, al);
    smooth(&m->ber_pre_fec, (float)biterr / (2.0f * n), init, al);
    m->viterbi_ber = vber;
    smooth(&m->eye_opening_pct, open, init, al);
    smooth(&m->deviation_hz, 3.0f * a, init, al);
    smooth(&m->inner_outer_ratio, ratio, init, al);
    smooth(&m->freq_offset_hz, rx->cfg.invert ? -b : b, init, al);
    for (int i = 0; i < 4; i++)
    {
        smooth(&m->level_mean[i], mu[i], init, al);
        smooth(&m->level_sigma[i], sg[i], init, al);
    }
    m->evm_reference = data_aided ? 0 : 1;
    m->meas_valid = 1;
    rx->meas_init = 1;

    if (rx->evm_hist_n == M17RX_EVM_HISTORY)
    {
        memmove(rx->evm_hist, rx->evm_hist + 1, sizeof(float) * (M17RX_EVM_HISTORY - 1));
        rx->evm_hist_n--;
    }
    rx->evm_hist[rx->evm_hist_n++] = evm;
    if (rx->stream_active || rx->packet_active)
    {
        rx->evm_sum += evm;
        rx->evm_n++;
    }

    /* histogram, slowly decaying */
    for (int i = 0; i < M17RX_HIST_BINS; i++)
        rx->hist[i] *= 0.95f;
    for (int k = 0; k < n; k++)
    {
        float y = (s[k] - b) / a;
        int bin = (int)((y + 4.5f) / 9.0f * M17RX_HIST_BINS);
        if (bin >= 0 && bin < M17RX_HIST_BINS)
            rx->hist[bin] += 1.0f;
    }
}

static void store_eye(m17rx_t *rx, double first_pos, float a, float b)
{
    const float *src = rx->cfg.eye_source ? rx->raw : rx->buf;
    for (int k = 0; k < SYM_PER_FRA; k++)
    {
        double c = first_pos + (double)k * M17RX_SPS;
        float *tr = rx->eye[rx->eye_head];
        for (int p = -EYE_HALF; p <= EYE_HALF; p++)
            tr[p + EYE_HALF] = (at(rx, src, c + p) - b) / a;
        rx->eye_head = (rx->eye_head + 1) % EYE_RING;
        if (rx->eye_count < EYE_RING)
            rx->eye_count++;
    }
}

/* Least-squares fit s = a * p + b. */
static void ls_fit(const float *s, const float *p, int n, float *a, float *b)
{
    double ms = 0, mp = 0, cov = 0, var = 0;
    for (int k = 0; k < n; k++)
    {
        ms += s[k];
        mp += p[k];
    }
    ms /= n;
    mp /= n;
    for (int k = 0; k < n; k++)
    {
        cov += (s[k] - ms) * (p[k] - mp);
        var += (p[k] - mp) * (p[k] - mp);
    }
    *a = var > 0 ? (float)(cov / var) : 0;
    *b = (float)(ms - *a * mp);
}

/* ------------------------------------------------------------------ */
/* Stream and packet handling                                          */
/* ------------------------------------------------------------------ */

static void end_stream(m17rx_t *rx, int reason)
{
    if (rx->stream_active && rx->start_reported)
    {
        m17rx_event_t ev;
        memset(&ev, 0, sizeof(ev));
        ev.kind = M17RX_EV_STREAM_END;
        ev.end_reason = reason;
        ev.frames = rx->frames;
        ev.frames_lost = rx->lost;
        ev.duration_s = rx->frames * 0.04f;
        ev.evm_rms_pct = rx->evm_n ? (float)(rx->evm_sum / rx->evm_n) : 0;
        ev.link = rx->link;
        if (rx->link.valid && rx->link.signed_stream)
            ev.sig_state = rx->sig_state != M17RX_SIG_NONE ? rx->sig_state : M17RX_SIG_MISSING;
        memcpy(ev.sig_key, rx->sig_key, sizeof(ev.sig_key));
        push_event(rx, &ev);
    }
    if (rx->packet_active && rx->pkt_len > 0)
    {
        m17rx_event_t ev;
        memset(&ev, 0, sizeof(ev));
        ev.kind = M17RX_EV_PACKET_BAD;
        ev.link = rx->link;
        ev.length = rx->pkt_len;
        memcpy(ev.data, rx->pkt, (size_t)rx->pkt_len);
        push_event(rx, &ev);
    }
    reset_stream_state(rx);
    memset(&rx->link, 0, sizeof(rx->link)); /* a later false lock must not show the old station */
    m17_text_reset(&rx->text);
}

static void begin_transmission(m17rx_t *rx)
{
    if (rx->stream_active || rx->packet_active)
        end_stream(rx, M17RX_END_EOT);
    reset_stream_state(rx);
    memset(&rx->link, 0, sizeof(rx->link));
    m17_text_reset(&rx->text);
    codec2_decoder_init(&rx->c2);
}

static void on_lsf(m17rx_t *rx, const lsf_t *lsf, int from_lich)
{
    int changed = m17_link_from_lsf(&rx->link, lsf, from_lich, &rx->text);
    rx->link.decrypt = decrypt_state(rx, &rx->link);
    if (rx->link.stream)
    {
        rx->stream_active = 1;
        if (!rx->start_reported)
        {
            m17rx_event_t ev;
            memset(&ev, 0, sizeof(ev));
            ev.kind = M17RX_EV_STREAM_START;
            ev.link = rx->link;
            push_event(rx, &ev);
            rx->start_reported = 1;
        }
        else if (changed)
        {
            m17rx_event_t ev;
            memset(&ev, 0, sizeof(ev));
            ev.kind = M17RX_EV_LINK_UPDATE;
            ev.link = rx->link;
            push_event(rx, &ev);
        }
    }
    else
    {
        rx->packet_active = 1;
        rx->pkt_len = 0;
    }
}

static void handle_str(m17rx_t *rx, const uint8_t data[16], const uint8_t lich[5], uint8_t cnt,
                       uint16_t fn, float vber)
{
    if (!rx->stream_active)
    {
        if (rx->packet_active)
            end_stream(rx, M17RX_END_EOT);
        rx->stream_active = 1; /* late join: the link follows from LICH */
    }

    /* LICH: six chunks rebuild the LSF */
    if (cnt <= 5)
    {
        if (cnt == 0)
            rx->lich_map = 0;
        memcpy(&rx->lich[cnt * 5], lich, 5);
        rx->lich_map |= 1 << cnt;
        if (rx->lich_map == 0x3F)
        {
            lsf_t l;
            memcpy(&l, rx->lich, sizeof(l));
            if (CRC_M17((const uint8_t *)&l, sizeof(l)) == 0)
                on_lsf(rx, &l, !rx->link.valid);
            /* the map is kept (shown as 6/6) until the next superframe starts with chunk 0 */
        }
    }

    const int f = fn & 0x7FFF;
    const int sig = rx->link.valid && rx->link.signed_stream && f >= 0x7FFC;

    if (!sig)
    {
        if (rx->expected_fn >= 0 && f != rx->expected_fn)
        {
            int gap = (f - rx->expected_fn) & 0x7FFF;
            rx->digest_ok = 0; /* a missing frame: the digest can't be rebuilt */
            if (gap < 0x100)
            {
                rx->lost += gap;
                push_silence(rx, (gap < 5 ? gap : 5) * 2 * SAMPLES_PER_FRAME);
            }
        }
        rx->expected_fn = (f + 1) & 0x7FFF;
        rx->frames++;
    }

    const m17rx_link_t *l = &rx->link;
    uint8_t pl[16];
    memcpy(pl, data, 16);

    if (sig)
    {
        /* the signature is sent in the clear, 16 bytes per frame, FN 7FFC..7FFF */
        memcpy(&rx->sig[(f - 0x7FFC) * 16], data, 16);
        rx->sig_map |= 1 << (f - 0x7FFC);
        if (f == 0x7FFF)
            verify_signature(rx);
    }
    else
    {
        /* digest over the payload as transmitted (before decryption), every frame from FN 0 */
        if (f == 0)
        {
            memset(rx->digest, 0, sizeof(rx->digest));
            rx->digest_ok = 1;
        }
        m17_digest_update(rx->digest, data);

        if (l->valid && l->decrypt == M17RX_DEC_OK)
        {
            if (l->enc_type == M17RX_ENC_AES)
                m17_aes_ctr_frame(&rx->aes, l->meta, fn, pl);
            else
            {
                /* the keystream restarts from the seed at FN 0; after a gap or a late join it is
                   recomputed from the FN (128 LFSR steps per frame) */
                if (f == 0)
                    rx->scr_state = rx->scr_seed;
                else if (f != rx->scr_next_fn)
                    rx->scr_state = m17_lfsr_advance(rx->scr_seed, l->enc_subtype, 128u * (uint32_t)f);
                uint8_t ks[16];
                rx->scr_state = m17_lfsr_frame(rx->scr_state, l->enc_subtype, ks);
                rx->scr_next_fn = (f + 1) & 0x7FFF;
                for (int i = 0; i < 16; i++)
                    pl[i] ^= ks[i];
            }
        }
    }

    if (!l->valid || l->data_type == M17RX_DT_VOICE || l->data_type == M17RX_DT_VOICE_DATA)
    {
        int clear = l->enc_type == M17RX_ENC_NONE || l->decrypt == M17RX_DEC_OK;
        int play = l->valid && l->data_type == M17RX_DT_VOICE && clear && !sig && vber <= rx->cfg.viterbi_mute;
        if (play)
        {
            int16_t pcm[SAMPLES_PER_FRAME];
            codec2_decode(&rx->c2, pcm, &pl[0]);
            push_audio(rx, pcm, SAMPLES_PER_FRAME);
            codec2_decode(&rx->c2, pcm, &pl[8]);
            push_audio(rx, pcm, SAMPLES_PER_FRAME);
        }
        else
        {
            push_silence(rx, 2 * SAMPLES_PER_FRAME);
        }
    }

    if (fn & 0x8000)
        end_stream(rx, M17RX_END_EOS);
}

static void handle_pkt(m17rx_t *rx, const uint8_t data[25], uint8_t eof, uint8_t pfn)
{
    if (rx->stream_active)
        end_stream(rx, M17RX_END_EOT);
    rx->packet_active = 1;

    m17rx_event_t ev;
    if (!eof)
    {
        if (pfn != rx->pkt_len / 25 || rx->pkt_len + 25 > M17RX_PKT_MAX)
            rx->pkt_len = 0; /* out of sequence: start again */
        if (pfn == rx->pkt_len / 25)
        {
            memcpy(&rx->pkt[rx->pkt_len], data, 25);
            rx->pkt_len += 25;
        }
        return;
    }

    memset(&ev, 0, sizeof(ev));
    ev.link = rx->link;
    if (pfn < 1 || pfn > 25 || rx->pkt_len + pfn > M17RX_PKT_MAX)
    {
        ev.kind = M17RX_EV_PACKET_BAD;
    }
    else
    {
        memcpy(&rx->pkt[rx->pkt_len], data, pfn);
        int total = rx->pkt_len + pfn;
        if (total >= 3 && CRC_M17(rx->pkt, (uint16_t)total) == 0)
        {
            ev.kind = M17RX_EV_PACKET;
            ev.length = total - 2;
            ev.protocol = rx->pkt[0];
            memcpy(ev.data, rx->pkt, (size_t)ev.length);
            if (ev.protocol == 0x05)
            {
                int tl = 0;
                while (1 + tl < ev.length && rx->pkt[1 + tl] != 0)
                    tl++;
                memcpy(ev.sms, &rx->pkt[1], (size_t)tl);
                ev.sms[tl] = 0;
            }
        }
        else
        {
            ev.kind = M17RX_EV_PACKET_BAD;
            ev.length = total;
            memcpy(ev.data, rx->pkt, (size_t)total);
        }
    }
    push_event(rx, &ev);
    rx->packet_active = 0;
    rx->pkt_len = 0;
}

/* ------------------------------------------------------------------ */
/* One frame                                                           */
/* ------------------------------------------------------------------ */

/* Decode the frame whose last syncword symbol is at pos (absolute, fractional).
   Returns 1 if the frame decoded well enough to trust. */
static int do_frame(m17rx_t *rx, double pos, int type, int acquiring)
{
    float s[SYM_PER_FRA], y[SYM_PER_FRA], ideal[SYM_PER_FRA];
    const double first = pos - SYNC_BACK;
    for (int k = 0; k < SYM_PER_FRA; k++)
        s[k] = at(rx, rx->buf, first + (double)k * M17RX_SPS);

    float a_s, b_s;
    ls_fit(s, rx->pat[type], 8, &a_s, &b_s);
    /* outer-symbol deviation must be plausible: 0.6 .. 6 kHz (nominal 2.4 kHz) */
    if (3.0f * a_s < 600.0f || 3.0f * a_s > 6000.0f)
    {
        if (!acquiring) /* keep the frame timing moving */
        {
            rx->last_pos = pos;
            rx->have_prev_pos = 1;
        }
        return 0;
    }

    float a = (rx->trk_valid && !acquiring) ? rx->a_trk : a_s;
    float b = (rx->trk_valid && !acquiring) ? rx->b_trk : b_s;
    for (int k = 0; k < SYM_PER_FRA; k++)
        y[k] = (s[k] - b) / a;

    int ok = 0, have_ideal = 0;
    float vber = 1.0f;

    if (type == SW_LSF)
    {
        lsf_t l;
        uint32_t e = decode_LSF_ctx(&rx->vit, &l, &y[SYM_PER_SWD]);
        vber = (float)e / 65535.0f / (2.0f * SYM_PER_PLD);
        ok = CRC_M17((const uint8_t *)&l, sizeof(l)) == 0;
        if (ok)
        {
            gen_frame(ideal, NULL, FRAME_LSF, &l, 0, 0);
            have_ideal = 1;
            begin_transmission(rx);
            on_lsf(rx, &l, 0);
        }
    }
    else if (type == SW_STR)
    {
        uint8_t data[16], lich[5], cnt = 0xFF;
        uint16_t fn = 0;
        uint32_t e = decode_str_frame_ctx(&rx->vit, data, lich, &fn, &cnt, &y[SYM_PER_SWD]);
        vber = (float)e / 65535.0f / (2.0f * SYM_PER_PLD);
        /* While tracking, a frame whose LICH chunk failed its Golay check still has a good
           payload if the Viterbi decode is clean: keep its voice, skip only the LICH chunk. */
        ok = (cnt <= 5 || !acquiring) && vber < (acquiring ? 0.06f : 0.12f);
        if (ok)
        {
            if (cnt <= 5)
            {
                lsf_t fake;
                memset(&fake, 0, sizeof(fake));
                memcpy((uint8_t *)&fake + cnt * 5, lich, 5);
                have_ideal = gen_frame(ideal, data, FRAME_STR, &fake, cnt, fn) == 0;
            }
            handle_str(rx, data, lich, cnt, fn, vber);
        }
    }
    else if (type == SW_PKT)
    {
        uint8_t data[26], eof = 0, pfn = 0;
        uint32_t e = decode_pkt_frame_ctx(&rx->vit, data, &eof, &pfn, &y[SYM_PER_SWD]);
        vber = (float)e / 65535.0f / (2.0f * SYM_PER_PLD);
        ok = vber < 0.06f;
        if (ok)
        {
            data[25] = (uint8_t)((eof << 7) | ((pfn & 0x1F) << 2));
            gen_frame(ideal, data, FRAME_PKT, NULL, 0, 0);
            have_ideal = 1;
            handle_pkt(rx, data, eof, pfn);
        }
    }
    else /* SW_EOT */
    {
        uint32_t c = 0;
        gen_eot(ideal, &c);
        have_ideal = 1;
        ok = 1;
    }

    if (!ok && acquiring)
        return 0;

    /* data-aided gain and DC from the whole frame */
    float a_m = a, b_m = b;
    if (have_ideal)
    {
        float a_da, b_da;
        ls_fit(s, ideal, SYM_PER_FRA, &a_da, &b_da);
        if (a_da > 1.0f)
        {
            a_m = a_da;
            b_m = b_da;
            if (!rx->trk_valid || acquiring)
            {
                rx->a_trk = a_da;
                rx->b_trk = b_da;
                rx->trk_valid = 1;
            }
            else
            {
                rx->a_trk += 0.3f * (a_da - rx->a_trk);
                rx->b_trk += 0.3f * (b_da - rx->b_trk);
            }
        }
    }
    else
    {
        for (int k = 0; k < SYM_PER_FRA; k++)
            ideal[k] = nearest_level(y[k]);
    }

    /* EoT frames are not measured: the carrier usually drops inside the RRC span of their last symbols */
    if (type != SW_EOT)
    {
        measure_frame(rx, s, ideal, have_ideal, a_m, b_m, vber);
        store_eye(rx, first, a_m, b_m);
    }

    /* timing bookkeeping */
    if (rx->have_prev_pos && !acquiring)
    {
        double predicted = rx->last_pos + FRAME_SMP;
        double delta = pos - predicted;
        smooth(&rx->meas.clock_ppm, (float)(-(pos - rx->last_pos - FRAME_SMP) / FRAME_SMP * 1e6), 0, 0.1f);
        rx->meas.timing_frac_sym = (float)(delta / M17RX_SPS);
    }
    rx->last_pos = pos;
    rx->have_prev_pos = 1;

    if (type == SW_EOT)
    {
        end_stream(rx, M17RX_END_EOT);
        rx->locked = 0;
        rx->trk_valid = 0;
        rx->have_prev_pos = 0;
        rx->scan = (int64_t)floor(pos) + 1;
    }
    return ok;
}

static void lose_lock(m17rx_t *rx)
{
    end_stream(rx, M17RX_END_LOST);
    rx->locked = 0;
    rx->trk_valid = 0;
    rx->have_prev_pos = 0;
    rx->miss = 0;
    rx->scan = (int64_t)floor(rx->last_pos) + SEARCH_WIN + 2;
}

/* A stream syncword is the negated LSF syncword: is this an LSF with the wrong polarity? */
static int inverted_lsf(m17rx_t *rx, double pos)
{
    float s[SYM_PER_FRA], y[SYM_PER_FRA], a, b;
    const double first = pos - SYNC_BACK;
    for (int k = 0; k < SYM_PER_FRA; k++)
        s[k] = at(rx, rx->buf, first + (double)k * M17RX_SPS);
    ls_fit(s, rx->pat[SW_LSF], 8, &a, &b); /* negative gain if inverted */
    if (-3.0f * a < 600.0f || -3.0f * a > 6000.0f)
        return 0;
    for (int k = 0; k < SYM_PER_FRA; k++)
        y[k] = (s[k] - b) / a;
    lsf_t l;
    decode_LSF_ctx(&rx->vit, &l, &y[SYM_PER_SWD]);
    return CRC_M17((const uint8_t *)&l, sizeof(l)) == 0;
}

static double refine(const m17rx_t *rx, int64_t n, int t, float r0)
{
    float rm = corr(rx, n - 1, t), rp = corr(rx, n + 1, t);
    float den = rm - 2 * r0 + rp;
    double f = (fabsf(den) > 1e-9f) ? 0.5 * (rm - rp) / den : 0;
    if (f > 0.5)
        f = 0.5;
    if (f < -0.5)
        f = -0.5;
    return (double)n + f;
}

static void run(m17rx_t *rx)
{
    const int64_t end = rx->base + rx->len;

    for (;;)
    {
        if (!rx->locked)
        {
            int64_t n = rx->scan;
            if (n < rx->base + NEED_BACK)
                n = rx->base + NEED_BACK;
            /* room for this frame and the next syncword (used to confirm stream/packet frames) */
            if (n + FRAME_SMP + 2 * SEARCH_WIN + 2 + NEED_AHEAD >= end)
            {
                rx->scan = n;
                return;
            }
            int best_t = -1;
            float best_r = 0;
            for (int t = SW_LSF; t <= SW_PKT; t++)
            {
                float r = corr(rx, n, t);
                if (r > best_r)
                {
                    best_r = r;
                    best_t = t;
                }
            }
            rx->scan = n + 1;
            if (best_t < 0 || best_r < rx->cfg.sync_threshold)
                continue;
            int peak = 1;
            for (int d = -SEARCH_WIN - 1; d <= SEARCH_WIN + 1 && peak; d++)
                if (d && corr(rx, n + d, best_t) > best_r)
                    peak = 0;
            if (!peak)
                continue;
            if (best_t == SW_STR && inverted_lsf(rx, refine(rx, n, best_t, best_r)))
            {
                rx->inverted_until = n + 2 * 24000; /* show the hint for about 2 s */
                DBG("inverted LSF at n=%lld\n", (long long)n);
                continue;
            }
            /* the next frame's syncword must follow exactly 40 ms later: noise passes this test
               almost never, and an LSF's CRC alone would accept noise about once per 65536 tries */
            {
                float rn = 0;
                for (int64_t m = n + FRAME_SMP - 1; m <= n + FRAME_SMP + 1; m++)
                    for (int t = SW_STR; t < SW_N; t++)
                    {
                        float r = corr(rx, m, t);
                        if (r > rn)
                            rn = r;
                    }
                if (rn < rx->cfg.sync_threshold)
                    continue;
            }
            double pos = refine(rx, n, best_t, best_r);
            DBG("acq try n=%lld t=%d r=%.3f\n", (long long)n, best_t, best_r);
            if (do_frame(rx, pos, best_t, 1))
            {
                rx->locked = 1;
                rx->miss = 0;
                rx->sync_q = best_r;
            }
        }
        else
        {
            double pred = rx->last_pos + FRAME_SMP;
            int64_t n0 = (int64_t)floor(pred + 0.5);
            if (n0 + SEARCH_WIN + 1 + NEED_AHEAD >= end)
                return;
            int best_t = -1;
            int64_t best_n = n0;
            float best_r = -1;
            for (int64_t n = n0 - SEARCH_WIN; n <= n0 + SEARCH_WIN; n++)
                for (int t = 0; t < SW_N; t++)
                {
                    float r = corr(rx, n, t);
                    if (r > best_r)
                    {
                        best_r = r;
                        best_t = t;
                        best_n = n;
                    }
                }
            rx->sync_q = best_r > 0 ? best_r : 0;
            DBG("trk n0=%lld best n=%lld t=%d r=%.3f miss=%d\n", (long long)n0, (long long)best_n, best_t, best_r, rx->miss);
            if (best_r >= rx->cfg.track_threshold)
            {
                rx->miss = 0;
                do_frame(rx, refine(rx, best_n, best_t, best_r), best_t, 0);
            }
            else
            {
                /* damaged syncword: keep the timing and try the frame anyway */
                if (++rx->miss >= 3)
                {
                    lose_lock(rx);
                    continue;
                }
                do_frame(rx, pred, rx->packet_active ? SW_PKT : SW_STR, 0);
                if (rx->locked && rx->last_pos < pred - 0.5) /* timing must always advance */
                {
                    rx->last_pos = pred;
                    rx->have_prev_pos = 1;
                }
            }
        }
    }
}

/* ------------------------------------------------------------------ */
/* Input                                                               */
/* ------------------------------------------------------------------ */

static void feed_48k(m17rx_t *rx, const float *x, int n)
{
    while (n > 0)
    {
        int k = n > 4096 ? 4096 : n;
        drop_old(rx, k);
        for (int i = 0; i < k; i++)
        {
            float v = rx->cfg.invert ? -x[i] : x[i];
            float m = m17_fir_step(&rx->rrc, v);
            float r = rx->delay[rx->delay_pos]; /* raw delayed by the RRC group delay */
            rx->delay[rx->delay_pos] = v;
            rx->delay_pos = (rx->delay_pos + 1) % RRC_DELAY;
            rx->buf[rx->len] = m;
            rx->raw[rx->len] = r;
            rx->len++;
        }
        x += k;
        n -= k;
        run(rx);
    }
}

static void feed_hz(m17rx_t *rx, const float *hz, int n)
{
    while (n > 0)
    {
        int k = n > 4096 ? 4096 : n;
        int produced = m17_resampler_run(&rx->rs, hz, k, rx->tmp_48k, 8192);
        feed_48k(rx, rx->tmp_48k, produced);
        /* drain if the output buffer limited the run */
        while (produced == 8192)
        {
            produced = m17_resampler_run(&rx->rs, NULL, 0, rx->tmp_48k, 8192);
            feed_48k(rx, rx->tmp_48k, produced);
        }
        hz += k;
        n -= k;
    }
}

M17RX_API int M17RX_CALL m17rx_process_fm(m17rx_t *rx, const float *hz, int n)
{
    if (!rx || !rx->rs_ok || n <= 0)
        return 0;
    feed_hz(rx, hz, n);
    return rx->ev_len;
}

M17RX_API int M17RX_CALL m17rx_process_iq(m17rx_t *rx, const float *iq, int n)
{
    if (!rx || !rx->rs_ok || n <= 0)
        return 0;
    const float k = (float)(rx->fs_in / (2.0 * 3.14159265358979323846));
    while (n > 0)
    {
        int m = n > 4096 ? 4096 : n;
        for (int i = 0; i < m; i++)
        {
            float I = iq[2 * i], Q = iq[2 * i + 1];
            if (!rx->have_prev)
            {
                rx->prev_i = I;
                rx->prev_q = Q;
                rx->have_prev = 1;
            }
            /* angle of z[n] * conj(z[n-1]) */
            float re = I * rx->prev_i + Q * rx->prev_q;
            float im = Q * rx->prev_i - I * rx->prev_q;
            rx->tmp_hz[i] = (re == 0 && im == 0) ? 0 : atan2f(im, re) * k;
            rx->prev_i = I;
            rx->prev_q = Q;
        }
        feed_hz(rx, rx->tmp_hz, m);
        iq += 2 * m;
        n -= m;
    }
    return rx->ev_len;
}

/* ------------------------------------------------------------------ */
/* Outputs                                                             */
/* ------------------------------------------------------------------ */

M17RX_API int M17RX_CALL m17rx_read_audio(m17rx_t *rx, int16_t *pcm, int max)
{
    int n = 0;
    while (n < max && rx->a_len > 0)
    {
        pcm[n++] = rx->audio[rx->a_head];
        rx->a_head = (rx->a_head + 1) % AUDIO_CAP;
        rx->a_len--;
    }
    return n;
}

M17RX_API int M17RX_CALL m17rx_next_event(m17rx_t *rx, m17rx_event_t *ev)
{
    if (rx->ev_len == 0)
        return 0;
    *ev = rx->evq[rx->ev_head];
    rx->ev_head = (rx->ev_head + 1) % EVQ_CAP;
    rx->ev_len--;
    return 1;
}

M17RX_API void M17RX_CALL m17rx_get_status(m17rx_t *rx, m17rx_status_t *st)
{
    *st = rx->meas;
    st->state = rx->packet_active ? M17RX_STATE_PACKET : rx->stream_active ? M17RX_STATE_STREAM : M17RX_STATE_SEARCH;
    st->locked = rx->locked;
    st->last_fn = rx->expected_fn >= 0 ? ((rx->expected_fn - 1) & 0x7FFF) : -1;
    st->frames = rx->frames;
    st->frames_lost = rx->lost;
    st->duration_s = rx->frames * 0.04f;
    st->lich_chunks = rx->lich_map;
    st->sync_quality = rx->sync_q;
    st->polarity_hint = (rx->base + rx->len) < rx->inverted_until;
    st->input_rate = (float)rx->fs_in;
    st->audio_queued = (uint32_t)rx->a_len;
    st->link = rx->link;
}

M17RX_API int M17RX_CALL m17rx_get_eye(m17rx_t *rx, float *out, int max_traces)
{
    int n = rx->eye_count < max_traces ? rx->eye_count : max_traces;
    int start = (rx->eye_head - n + EYE_RING) % EYE_RING;
    for (int i = 0; i < n; i++)
        memcpy(&out[i * M17RX_EYE_POINTS], rx->eye[(start + i) % EYE_RING], sizeof(float) * M17RX_EYE_POINTS);
    return n;
}

M17RX_API void M17RX_CALL m17rx_get_histogram(m17rx_t *rx, float out[M17RX_HIST_BINS])
{
    memcpy(out, rx->hist, sizeof(rx->hist));
}

M17RX_API int M17RX_CALL m17rx_get_evm_history(m17rx_t *rx, float out[M17RX_EVM_HISTORY])
{
    memcpy(out, rx->evm_hist, sizeof(float) * (size_t)rx->evm_hist_n);
    return rx->evm_hist_n;
}
