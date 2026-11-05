/*
 * m17rx_cli - run libm17rx over a recording, for tests.
 *
 *   m17rx_cli <rate_hz> <iq.cf32 | fm.f32> [--fm] [--audio out.s16] [--json]
 *
 * Input: interleaved float32 I/Q (default) or float32 FM discriminator output in Hz (--fm).
 * Prints one JSON line per event, then a final status line.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#include "m17rx.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void json_str(const char *s)
{
    putchar('"');
    for (; *s; s++)
    {
        unsigned char c = (unsigned char)*s;
        if (c == '"' || c == '\\')
            printf("\\%c", c);
        else if (c < 0x20)
            printf("\\u%04x", c);
        else
            putchar(c);
    }
    putchar('"');
}

static void print_link(const m17rx_link_t *l)
{
    printf("{\"valid\":%d,\"from_lich\":%d,\"src\":", l->valid, l->from_lich);
    json_str(l->src);
    printf(",\"dst\":");
    json_str(l->dst);
    printf(",\"type\":%u,\"stream\":%d,\"data_type\":%d,\"enc\":%d,\"enc_sub\":%d,\"can\":%d,\"signed\":%d,\"meta_kind\":%d,\"text\":",
           l->type, l->stream, l->data_type, l->enc_type, l->enc_subtype, l->can, l->signed_stream, l->meta_kind);
    json_str(l->meta_text);
    printf(",\"text_complete\":%d,\"ecd1\":", l->meta_text_complete);
    json_str(l->ecd_cf1);
    printf(",\"ecd2\":");
    json_str(l->ecd_cf2);
    printf(",\"lat\":%.5f,\"lon\":%.5f,\"alt\":%.1f,\"pos_valid\":%d,\"decrypt\":%d}", l->gnss_lat, l->gnss_lon, l->gnss_alt_m, l->gnss_valid_pos, l->decrypt);
}

int main(int argc, char **argv)
{
    if (argc < 3)
    {
        fprintf(stderr, "usage: %s rate_hz input [--fm] [--audio out.s16]\n", argv[0]);
        return 2;
    }
    double rate = atof(argv[1]);
    const char *in = argv[2], *audio_path = NULL;
    int fm = 0, invert = 0;
    uint8_t aes[32];
    int aes_len = 0, scr_bits = 0, npub = 0;
    uint32_t scr = 0;
    const char *pub_arg[16];
    for (int i = 3; i < argc; i++)
    {
        if (!strcmp(argv[i], "--aes") && i + 1 < argc) /* hex */
        {
            const char *h = argv[++i];
            aes_len = (int)strlen(h) / 2;
            for (int k = 0; k < aes_len && k < 32; k++)
                sscanf(h + 2 * k, "%2hhx", &aes[k]);
        }
        else if (!strcmp(argv[i], "--scr") && i + 1 < argc) /* hex, 2/4/6 digits */
        {
            scr_bits = 4 * (int)strlen(argv[i + 1]);
            scr = (uint32_t)strtoul(argv[++i], NULL, 16);
        }
        else if (!strcmp(argv[i], "--pub") && i + 1 < argc && npub < 16) /* name=hex */
            pub_arg[npub++] = argv[++i];
        else
        if (!strcmp(argv[i], "--fm"))
            fm = 1;
        else if (!strcmp(argv[i], "--invert"))
            invert = 1;
        else if (!strcmp(argv[i], "--audio") && i + 1 < argc)
            audio_path = argv[++i];
    }

    FILE *f = fopen(in, "rb");
    if (!f)
    {
        perror(in);
        return 1;
    }
    FILE *fa = audio_path ? fopen(audio_path, "wb") : NULL;

    m17rx_t *rx = m17rx_create();
    if (aes_len && m17rx_set_aes_key(rx, aes, aes_len) != 0)
        fprintf(stderr, "bad AES key\n");
    if (scr_bits && m17rx_set_scrambler_seed(rx, scr, scr_bits) != 0)
        fprintf(stderr, "bad scrambler seed\n");
    for (int k = 0; k < npub; k++)
    {
        char name[32] = {0};
        const char *eq = strchr(pub_arg[k], '=');
        if (!eq)
            continue;
        snprintf(name, sizeof name, "%.*s", (int)(eq - pub_arg[k]), pub_arg[k]);
        uint8_t key[65];
        int kl = (int)strlen(eq + 1) / 2;
        for (int j = 0; j < kl && j < 65; j++)
            sscanf(eq + 1 + 2 * j, "%2hhx", &key[j]);
        if (m17rx_add_public_key(rx, name, key, kl) != 0)
            fprintf(stderr, "bad public key %s\n", name);
    }
    if (m17rx_set_input_rate(rx, rate) != 0)
    {
        fprintf(stderr, "bad rate\n");
        return 1;
    }
    m17rx_config_t cfg;
    m17rx_default_config(&cfg);
    cfg.invert = invert;
    m17rx_set_config(rx, &cfg);

    const int chunk = 3000; /* odd-sized blocks, like a host would deliver */
    float *buf = malloc(sizeof(float) * 2 * chunk);
    int16_t pcm[8192];
    m17rx_event_t *ev = malloc(sizeof(m17rx_event_t));
    long audio_total = 0;
    size_t got;
    while ((got = fread(buf, sizeof(float) * (fm ? 1 : 2), chunk, f)) > 0)
    {
        if (fm)
            m17rx_process_fm(rx, buf, (int)got);
        else
            m17rx_process_iq(rx, buf, (int)got);

        int na;
        while ((na = m17rx_read_audio(rx, pcm, 8192)) > 0)
        {
            audio_total += na;
            if (fa)
                fwrite(pcm, sizeof(int16_t), (size_t)na, fa);
        }
        while (m17rx_next_event(rx, ev))
        {
            static const char *kinds[] = {"none", "stream_start", "link_update", "stream_end", "packet", "packet_bad"};
            printf("{\"event\":\"%s\"", kinds[ev->kind]);
            if (ev->kind == M17RX_EV_STREAM_END)
            {
                printf(",\"reason\":%d,\"frames\":%d,\"lost\":%d,\"duration\":%.2f,\"evm\":%.2f,\"sig\":%d,\"sig_key\":",
                       ev->end_reason, ev->frames, ev->frames_lost, ev->duration_s, ev->evm_rms_pct, ev->sig_state);
                json_str(ev->sig_key);
            }
            if (ev->kind == M17RX_EV_PACKET || ev->kind == M17RX_EV_PACKET_BAD)
            {
                printf(",\"protocol\":%d,\"length\":%d,\"sms\":", ev->protocol, ev->length);
                json_str(ev->sms);
            }
            printf(",\"link\":");
            print_link(&ev->link);
            printf("}\n");
        }
    }

    m17rx_status_t st;
    m17rx_get_status(rx, &st);
    printf("{\"status\":{\"meas_valid\":%d,\"evm\":%.3f,\"evm_peak\":%.3f,\"mer\":%.2f,\"ber_pre\":%.5f,\"eye\":%.1f,"
           "\"polarity_hint\":%d,\"dev\":%.1f,\"ratio\":%.4f,\"offset\":%.1f,\"ppm\":%.1f,\"evm_ref\":%d,"
           "\"link_valid\":%d,\"locked\":%d,\"state\":%d,\"levels\":[%.3f,%.3f,%.3f,%.3f],\"sigma\":[%.3f,%.3f,%.3f,%.3f],\"audio_samples\":%ld}}\n",
           st.meas_valid, st.evm_rms_pct, st.evm_peak_pct, st.mer_db, st.ber_pre_fec, st.eye_opening_pct,
           st.polarity_hint, st.deviation_hz, st.inner_outer_ratio, st.freq_offset_hz, st.clock_ppm, st.evm_reference, st.link.valid, st.locked, st.state,
           st.level_mean[0], st.level_mean[1], st.level_mean[2], st.level_mean[3],
           st.level_sigma[0], st.level_sigma[1], st.level_sigma[2], st.level_sigma[3], audio_total);

    float eye[64 * M17RX_EYE_POINTS];
    int ntr = m17rx_get_eye(rx, eye, 64);
    float hist[M17RX_HIST_BINS];
    m17rx_get_histogram(rx, hist);
    float eh[M17RX_EVM_HISTORY];
    int nh = m17rx_get_evm_history(rx, eh);
    printf("{\"views\":{\"eye_traces\":%d,\"evm_history\":%d}}\n", ntr, nh);

    m17rx_destroy(rx);
    free(buf);
    free(ev);
    fclose(f);
    if (fa)
        fclose(fa);
    return 0;
}
