/* M17 payload decryption and stream signatures (spec 2.0.x), as gr-m17 implements them.
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception */
#include "crypto.h"

#include <string.h>

#include "uECC.h"

/* ------------------------------------------------------------------ AES (FIPS-197), encrypt only */

static const uint8_t sbox[256] = {
    0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
    0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
    0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
    0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
    0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
    0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
    0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
    0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
    0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
    0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
    0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
    0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
    0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
    0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
    0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
    0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16};

static uint8_t xtime(uint8_t x) { return (uint8_t)((x << 1) ^ ((x >> 7) * 0x1b)); }

int m17_aes_init(m17_aes_t *a, const uint8_t *key, int key_len)
{
    if (key_len != 16 && key_len != 24 && key_len != 32)
        return -1;
    const int nk = key_len / 4;
    a->rounds = nk + 6;
    const int words = 4 * (a->rounds + 1);
    memcpy(a->rk, key, (size_t)key_len);
    uint8_t rcon = 1;
    for (int i = nk; i < words; i++)
    {
        uint8_t t[4];
        memcpy(t, &a->rk[4 * (i - 1)], 4);
        if (i % nk == 0)
        {
            uint8_t u = t[0];
            t[0] = (uint8_t)(sbox[t[1]] ^ rcon);
            t[1] = sbox[t[2]];
            t[2] = sbox[t[3]];
            t[3] = sbox[u];
            rcon = xtime(rcon);
        }
        else if (nk > 6 && i % nk == 4)
        {
            for (int k = 0; k < 4; k++)
                t[k] = sbox[t[k]];
        }
        for (int k = 0; k < 4; k++)
            a->rk[4 * i + k] = (uint8_t)(a->rk[4 * (i - nk) + k] ^ t[k]);
    }
    return 0;
}

void m17_aes_encrypt(const m17_aes_t *a, const uint8_t in[16], uint8_t out[16])
{
    uint8_t s[16];
    for (int i = 0; i < 16; i++)
        s[i] = (uint8_t)(in[i] ^ a->rk[i]);
    for (int r = 1; r <= a->rounds; r++)
    {
        uint8_t t[16];
        /* SubBytes + ShiftRows (state is column-major: s[4*col + row]) */
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                t[4 * c + row] = sbox[s[4 * ((c + row) & 3) + row]];
        if (r != a->rounds)
        {
            /* MixColumns */
            for (int c = 0; c < 4; c++)
            {
                uint8_t *p = &t[4 * c];
                uint8_t a0 = p[0], a1 = p[1], a2 = p[2], a3 = p[3], all = (uint8_t)(a0 ^ a1 ^ a2 ^ a3);
                p[0] ^= (uint8_t)(all ^ xtime((uint8_t)(a0 ^ a1)));
                p[1] ^= (uint8_t)(all ^ xtime((uint8_t)(a1 ^ a2)));
                p[2] ^= (uint8_t)(all ^ xtime((uint8_t)(a2 ^ a3)));
                p[3] ^= (uint8_t)(all ^ xtime((uint8_t)(a3 ^ a0)));
            }
        }
        for (int i = 0; i < 16; i++)
            s[i] = (uint8_t)(t[i] ^ a->rk[16 * r + i]);
    }
    memcpy(out, s, 16);
}

void m17_aes_ctr_frame(const m17_aes_t *a, const uint8_t nonce[14], uint16_t fn, uint8_t payload[16])
{
    uint8_t ctr[16], ks[16];
    memcpy(ctr, nonce, 14);
    ctr[14] = (uint8_t)((fn >> 8) & 0x7F);
    ctr[15] = (uint8_t)(fn & 0xFF);
    m17_aes_encrypt(a, ctr, ks);
    for (int i = 0; i < 16; i++)
        payload[i] ^= ks[i];
}

/* ------------------------------------------------------------------ scrambler */

static inline uint32_t lfsr_bit(uint32_t s, int subtype)
{
    switch (subtype)
    {
    case 0: return ((s >> 7) ^ (s >> 5) ^ (s >> 4) ^ (s >> 3)) & 1;
    case 1: return ((s >> 15) ^ (s >> 14) ^ (s >> 12) ^ (s >> 3)) & 1;
    default: return ((s >> 23) ^ (s >> 22) ^ (s >> 21) ^ (s >> 16)) & 1;
    }
}

static inline uint32_t lfsr_mask(int subtype) { return subtype == 0 ? 0xFF : subtype == 1 ? 0xFFFF : 0xFFFFFF; }

uint32_t m17_lfsr_advance(uint32_t s, int subtype, uint32_t steps)
{
    const uint32_t m = lfsr_mask(subtype);
    s &= m;
    while (steps--)
        s = ((s << 1) | lfsr_bit(s, subtype)) & m;
    return s;
}

uint32_t m17_lfsr_frame(uint32_t s, int subtype, uint8_t out[16])
{
    const uint32_t m = lfsr_mask(subtype);
    s &= m;
    memset(out, 0, 16);
    for (int i = 0; i < 128; i++)
    {
        uint32_t b = lfsr_bit(s, subtype);
        s = ((s << 1) | b) & m;
        out[i >> 3] |= (uint8_t)(b << (7 - (i & 7)));
    }
    return s;
}

/* ------------------------------------------------------------------ signatures */

void m17_digest_update(uint8_t d[16], const uint8_t p[16])
{
    for (int i = 0; i < 16; i++)
        d[i] ^= p[i];
    uint8_t t = d[0];
    memmove(d, d + 1, 15);
    d[15] = t;
}

int m17_pubkey_parse(const uint8_t *in, int len, uint8_t out[64])
{
    const struct uECC_Curve_t *c = uECC_secp256r1();
    if (len == 64)
        memcpy(out, in, 64);
    else if (len == 65 && in[0] == 0x04)
        memcpy(out, in + 1, 64);
    else if (len == 33 && (in[0] == 0x02 || in[0] == 0x03))
        uECC_decompress(in, out, c);
    else
        return -1;
    return uECC_valid_public_key(out, c) ? 0 : -1;
}

int m17_sig_verify(const uint8_t pub[64], const uint8_t digest[16], const uint8_t sig[64])
{
    return uECC_verify(pub, digest, 16, sig, uECC_secp256r1()) ? 1 : 0;
}
