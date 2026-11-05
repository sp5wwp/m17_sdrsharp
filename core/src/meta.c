/*
 * libm17rx - LSF to m17rx_link_t, including META decoding
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#include "internal.h"

#include <string.h>

static int popcount4(unsigned v)
{
    int n = 0;
    for (; v; v &= v - 1)
        n++;
    return n;
}

static int ctz4(unsigned v)
{
    for (int i = 0; i < 4; i++)
        if (v & (1u << i))
            return i;
    return 0;
}

void m17_text_reset(m17_text_t *t)
{
    memset(t, 0, sizeof(*t));
}

/* One META Text Data block. Returns 1 if the assembled text changed. */
static int text_block(m17_text_t *t, const uint8_t meta[14])
{
    unsigned used = meta[0] >> 4, which = meta[0] & 0xF;
    int valid = (used == 0x1 || used == 0x3 || used == 0x7 || used == 0xF) &&
                which && !(which & (which - 1)) && (which & used);
    if (!valid)
        return 0;

    if (used != t->used) /* another message: start over */
    {
        t->used = used;
        t->have = 0;
    }
    int idx = ctz4(which);
    int changed = !(t->have & which) || memcmp(t->block[idx], &meta[1], 13) != 0;
    memcpy(t->block[idx], &meta[1], 13);
    t->have |= which;
    return changed;
}

/* Text assembled from the blocks received so far, in block order. */
static void text_render(const m17_text_t *t, char *out, size_t cap, int32_t *complete)
{
    size_t n = 0;
    int total = popcount4(t->used);
    for (int i = 0; i < total && n + 13 < cap; i++)
    {
        if (!(t->have & (1u << i)))
            continue;
        memcpy(out + n, t->block[i], 13);
        n += 13;
    }
    /* stop at a NUL, trim trailing spaces (the last block is space-padded) */
    out[n] = 0;
    n = strlen(out);
    while (n > 0 && (out[n - 1] == ' ' || out[n - 1] == 0))
        out[--n] = 0;
    *complete = (t->used != 0 && t->have == t->used);
}

int m17_link_from_lsf(m17rx_link_t *link, const lsf_t *lsf, int from_lich, m17_text_t *text)
{
    m17rx_link_t old = *link;
    m17rx_link_t l;
    memset(&l, 0, sizeof(l));

    l.valid = 1;
    l.from_lich = from_lich;
    decode_callsign_bytes(l.src, lsf->src);
    decode_callsign_bytes(l.dst, lsf->dst);
    l.type = ((uint16_t)lsf->type[0] << 8) | lsf->type[1];
    l.stream = l.type & 1;
    l.data_type = (l.type >> 1) & 3;
    l.enc_type = (l.type >> 3) & 3;
    l.enc_subtype = (l.type >> 5) & 3;
    l.can = (l.type >> 7) & 0xF;
    l.signed_stream = (l.type >> 11) & 1;
    memcpy(l.meta, lsf->meta, 14);

    int empty = 1;
    for (int i = 0; i < 14; i++)
        empty &= (lsf->meta[i] == 0);

    if (!l.stream)
    {
        /* packet mode: only the packet bit and CAN are defined in TYPE */
        l.data_type = l.enc_type = l.enc_subtype = l.signed_stream = 0;
        l.meta_kind = M17RX_META_NONE;
    }
    else if (l.enc_type != M17RX_ENC_NONE || empty)
    {
        l.meta_kind = M17RX_META_NONE; /* nonce or nothing */
    }
    else if (l.enc_subtype == 0)
    {
        l.meta_kind = M17RX_META_TEXT;
        /* a different station or a new transmission keeps its own text */
        if (strcmp(old.src, l.src) != 0)
            m17_text_reset(text);
        text_block(text, lsf->meta);
        text_render(text, l.meta_text, sizeof(l.meta_text), &l.meta_text_complete);
    }
    else if (l.enc_subtype == 1)
    {
        uint8_t src, station, validity;
        float lat, lon, alt, speed, radius;
        uint16_t bearing;
        l.meta_kind = M17RX_META_GNSS;
        if (get_LSF_meta_position(&src, &station, &lat, &lon, &validity, &alt, &bearing, &speed, &radius, lsf) == 0)
        {
            l.gnss_source = src;
            l.gnss_station = station;
            l.gnss_valid_pos = (validity >> 3) & 1;
            l.gnss_valid_alt = (validity >> 2) & 1;
            l.gnss_valid_speed = (validity >> 1) & 1;
            l.gnss_lat = lat;
            l.gnss_lon = lon;
            l.gnss_alt_m = alt;
            l.gnss_speed_kmh = speed;
            l.gnss_bearing_deg = bearing;
        }
    }
    else if (l.enc_subtype == 2)
    {
        l.meta_kind = M17RX_META_ECD;
        decode_callsign_bytes(l.ecd_cf1, &lsf->meta[0]);
        int has2 = 0;
        for (int i = 6; i < 12; i++)
            has2 |= lsf->meta[i] != 0;
        if (has2)
            decode_callsign_bytes(l.ecd_cf2, &lsf->meta[6]);
    }
    else
    {
        l.meta_kind = M17RX_META_RAW;
    }

    *link = l;

    /* changed in a way worth reporting? (rotating text blocks only count when the text grows) */
    if (!old.valid)
        return 1;
    if (strcmp(old.src, l.src) || strcmp(old.dst, l.dst) || old.type != l.type)
        return 1;
    if (l.meta_kind == M17RX_META_TEXT)
        return strcmp(old.meta_text, l.meta_text) != 0 || old.meta_text_complete != l.meta_text_complete;
    return memcmp(old.meta, l.meta, 14) != 0;
}
