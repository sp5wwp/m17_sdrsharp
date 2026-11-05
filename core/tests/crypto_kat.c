/* Known-answer tests for crypto.c: FIPS-197 AES vectors, CTR against tinier-aes (gr-m17),
   the scrambler against gr-m17's LFSR code, and an ECDSA round trip.
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception */
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "crypto.h"
#include "uECC.h"
#ifdef WITH_TINIER
#include "aes.h"
#endif

static int fails;
static void check(const char *what, int ok) { printf("%s  %s\n", ok ? "PASS" : "FAIL", what); fails += !ok; }
static void hex(const char *s, uint8_t *o) { for (size_t i = 0; i < strlen(s) / 2; i++) sscanf(s + 2 * i, "%2hhx", &o[i]); }

/* gr-m17's scrambler, verbatim logic (m17_decoder_impl.cc) */
static uint32_t gr_frame(uint32_t lfsr, int sub, uint8_t out[16])
{
    uint8_t bits[128];
    for (int i = 0; i < 128; i++)
    {
        uint32_t bit;
        if (sub == 0) bit = (lfsr >> 7) ^ (lfsr >> 5) ^ (lfsr >> 4) ^ (lfsr >> 3);
        else if (sub == 1) bit = (lfsr >> 15) ^ (lfsr >> 14) ^ (lfsr >> 12) ^ (lfsr >> 3);
        else bit = (lfsr >> 23) ^ (lfsr >> 22) ^ (lfsr >> 21) ^ (lfsr >> 16);
        bit &= 1;
        lfsr = ((lfsr << 1) | bit) & 0xFFFFFF;
        bits[i] = (uint8_t)bit;
    }
    for (int i = 0; i < 16; i++) { out[i] = 0; for (int k = 0; k < 8; k++) out[i] = (uint8_t)((out[i] << 1) | bits[8 * i + k]); }
    if (sub == 0) lfsr &= 0xFF; else if (sub == 1) lfsr &= 0xFFFF;
    return lfsr;
}

static int rng(uint8_t *d, unsigned n) { for (unsigned i = 0; i < n; i++) d[i] = (uint8_t)rand(); return 1; }

int main(void)
{
    const char *key[3] = {"000102030405060708090a0b0c0d0e0f", "000102030405060708090a0b0c0d0e0f1011121314151617",
                          "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"};
    const char *ct[3] = {"69c4e0d86a7b0430d8cdb78070b4c55a", "dda97ca4864cdfe06eaf70a0ec0d7191", "8ea2b7ca516745bfeafc49904b496089"};
    for (int k = 0; k < 3; k++)
    {
        uint8_t kb[32], pt[16], c[16], out[16];
        hex(key[k], kb);
        hex("00112233445566778899aabbccddeeff", pt);
        hex(ct[k], c);
        m17_aes_t a;
        m17_aes_init(&a, kb, 16 + 8 * k);
        m17_aes_encrypt(&a, pt, out);
        char n[64];
        snprintf(n, sizeof n, "AES-%d FIPS-197 C.%d", 128 + 64 * k, k + 1);
        check(n, memcmp(out, c, 16) == 0);
    }
#ifdef WITH_TINIER
    srand(1);
    int same = 1;
    for (int t = 0; t < 3000; t++)
    {
        int sub = t % 3;
        uint8_t kb[32], nonce[16], p1[16], p2[16];
        rng(kb, 32); rng(nonce, 14); rng(p1, 16);
        memcpy(p2, p1, 16);
        uint16_t fn = (uint16_t)rand();
        m17_aes_t a;
        m17_aes_init(&a, kb, 16 + 8 * sub);
        m17_aes_ctr_frame(&a, nonce, fn, p1);
        nonce[14] = (fn >> 8) & 0x7F; nonce[15] = fn & 0xFF;
        aes_ctr_bytewise_payload_crypt(nonce, kb, p2, sub);
        same &= memcmp(p1, p2, 16) == 0;
    }
    check("AES-CTR frames equal tinier-aes (gr-m17), 3000 random", same);
#endif
    for (int sub = 0; sub < 3; sub++)
    {
        uint32_t seed = 0xA5C3E1 & ((1u << (8 * (sub + 1))) - 1), s1 = seed, s2 = seed;
        int same = 1;
        for (int f = 0; f < 500; f++)
        {
            uint8_t a[16], b[16];
            s1 = m17_lfsr_frame(s1, sub, a);
            s2 = gr_frame(s2, sub, b);
            same &= memcmp(a, b, 16) == 0;
        }
        /* jump: state for FN 300 computed directly */
        uint8_t a[16], b[16];
        uint32_t j = m17_lfsr_advance(seed, sub, 128 * 300), w = seed;
        for (int f = 0; f < 300; f++) w = gr_frame(w, sub, b);
        m17_lfsr_frame(j, sub, a);
        gr_frame(w, sub, b);
        same &= memcmp(a, b, 16) == 0;
        char n[64];
        snprintf(n, sizeof n, "scrambler %d-bit equals gr-m17 (500 frames, jump to FN 300)", 8 * (sub + 1));
        check(n, same);
    }
    uECC_set_rng(rng);
    const struct uECC_Curve_t *c = uECC_secp256r1();
    uint8_t priv[32], pub[64], pub2[64], comp[33], digest[16], sig[64];
    memset(priv, 0x11, 32);
    uECC_compute_public_key(priv, pub, c);
    rng(digest, 16);
    uECC_sign(priv, digest, 16, sig, c);
    check("ECDSA P-256: valid signature verifies", m17_sig_verify(pub, digest, sig));
    digest[3] ^= 1;
    check("ECDSA P-256: changed digest fails", !m17_sig_verify(pub, digest, sig));
    uECC_compress(pub, comp, c);
    check("public key: compressed form parses to the same key", m17_pubkey_parse(comp, 33, pub2) == 0 && !memcmp(pub, pub2, 64));
    uint8_t bad[64];
    memcpy(bad, pub, 64); bad[63] ^= 1;
    check("public key: point off the curve rejected", m17_pubkey_parse(bad, 64, pub2) != 0);
    char ph[129];
    for (int i = 0; i < 64; i++) sprintf(ph + 2 * i, "%02x", pub[i]);
    printf("pubkey(priv 11*32) %s\n", ph);
    return fails != 0;
}
