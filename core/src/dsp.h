/*
 * libm17rx - DSP building blocks
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#ifndef M17RX_DSP_H
#define M17RX_DSP_H

#include <stdint.h>

#define M17RX_FS        48000.0   /* internal sample rate */
#define M17RX_SPS       10        /* samples per symbol at 48 kHz, 4800 Bd */

/* Arbitrary-ratio band-limited resampler (real samples), polyphase windowed sinc. */
typedef struct
{
    double  ratio;      /* input samples per output sample */
    int     half;       /* kernel half-width, input samples */
    int     phases;
    float  *table;      /* [phases][2*half] */
    float  *in;         /* pending input */
    int     in_len, in_cap;
    double  t;          /* position of the next output, in input samples from in[0] */
} m17_resampler_t;

int  m17_resampler_init(m17_resampler_t *r, double fs_in, double fs_out);
void m17_resampler_free(m17_resampler_t *r);
void m17_resampler_reset(m17_resampler_t *r);
/* Push n input samples; writes up to max outputs, returns how many. Call again with
   n = 0 to drain if the output buffer was full. */
int  m17_resampler_run(m17_resampler_t *r, const float *x, int n, float *y, int max);

/* FIR filter with a circular history (used for the RRC matched filter). */
typedef struct
{
    int    ntaps;
    float *taps;
    float *hist;        /* 2*ntaps, mirrored for a contiguous dot product */
    int    pos;
} m17_fir_t;

int   m17_fir_init(m17_fir_t *f, const float *taps, int ntaps, float scale);
void  m17_fir_free(m17_fir_t *f);
void  m17_fir_reset(m17_fir_t *f);
float m17_fir_step(m17_fir_t *f, float x);

/* Cubic (Catmull-Rom) interpolation of x at fractional position i + mu, 0 <= mu < 1.
   Needs x[i-1] .. x[i+2]. */
static inline float m17_interp_cubic(const float *x, int i, float mu)
{
    float xm1 = x[i - 1], x0 = x[i], x1 = x[i + 1], x2 = x[i + 2];
    float a = -0.5f * xm1 + 1.5f * x0 - 1.5f * x1 + 0.5f * x2;
    float b = xm1 - 2.5f * x0 + 2.0f * x1 - 0.5f * x2;
    float c = -0.5f * xm1 + 0.5f * x1;
    return ((a * mu + b) * mu + c) * mu + x0;
}

#endif
