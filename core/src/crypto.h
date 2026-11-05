/* M17 payload decryption and stream signatures (spec 2.0.x), as gr-m17 implements them.
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception */
#ifndef M17RX_CRYPTO_H
#define M17RX_CRYPTO_H

#include <stdint.h>

/* AES, encryption direction only (CTR mode needs nothing else). key_len 16, 24 or 32. */
typedef struct
{
    uint8_t rk[240];
    int rounds;
} m17_aes_t;

int  m17_aes_init(m17_aes_t *a, const uint8_t *key, int key_len); /* 0 on success */
void m17_aes_encrypt(const m17_aes_t *a, const uint8_t in[16], uint8_t out[16]);

/* One stream frame: counter = META nonce (14 bytes) || FN with the EOS bit cleared. In place. */
void m17_aes_ctr_frame(const m17_aes_t *a, const uint8_t nonce[14], uint16_t fn, uint8_t payload[16]);

/* Scrambler: Fibonacci LFSR of 8, 16 or 24 bits (subtype 0, 1, 2), 128 bits per frame, MSB first. */
uint32_t m17_lfsr_advance(uint32_t state, int subtype, uint32_t steps);
uint32_t m17_lfsr_frame(uint32_t state, int subtype, uint8_t out[16]); /* returns the next state */

/* Stream digest: XOR the payload in, then rotate left by one byte. */
void m17_digest_update(uint8_t digest[16], const uint8_t payload[16]);

/* Public keys for secp256r1: 64 bytes X||Y, 65 bytes 04||X||Y, or 33 bytes 02/03||X (compressed).
   Writes the 64-byte form; returns 0 if the key is a valid point on the curve. */
int m17_pubkey_parse(const uint8_t *in, int len, uint8_t out[64]);

/* 1 if sig (64 bytes r||s) is a valid signature of the 16-byte digest under pub (64 bytes). */
int m17_sig_verify(const uint8_t pub[64], const uint8_t digest[16], const uint8_t sig[64]);

#endif
