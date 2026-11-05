/*
 * libm17rx - DSP building blocks
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#include "dsp.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

/* Modified Bessel function of the first kind, order 0 (for the Kaiser window). */
static double bessel_i0(double x)
{
    double sum = 1.0, term = 1.0, q = x * x / 4.0;
    for (int k = 1; k < 40; k++)
    {
        term *= q / ((double)k * k);
        sum += term;
        if (term < 1e-12 * sum)
            break;
    }
    return sum;
}

int m17_resampler_init(m17_resampler_t *r, double fs_in, double fs_out)
{
    memset(r, 0, sizeof(*r));
    if (fs_in <= 0 || fs_out <= 0)
        return -1;

    r->ratio = fs_in / fs_out;

    /* Cutoff at 45% of the lower of the two rates, in cycles per input sample. */
    double fc = 0.45 * (fs_in < fs_out ? fs_in : fs_out) / fs_in;
    /* About 8 cycles of the cutoff frequency on each side of the centre. */
    r->half = (int)ceil(4.0 / fc);
    if (r->half < 4)
        r->half = 4;
    r->phases = 256;

    const int len = 2 * r->half;
    const double beta = 8.0; /* Kaiser window, ~80 dB stopband */
    const double i0b = bessel_i0(beta);

    r->table = (float *)malloc(sizeof(float) * (size_t)r->phases * len);
    if (!r->table)
        return -1;

    for (int p = 0; p < r->phases; p++)
    {
        double mu = (double)p / r->phases; /* output lies mu after tap index half-1 */
        double sum = 0;
        float *row = &r->table[(size_t)p * len];
        for (int k = 0; k < len; k++)
        {
            double d = (k - (r->half - 1)) - mu; /* distance from the output point */
            double s = (fabs(d) < 1e-12) ? 2.0 * fc : sin(2.0 * M_PI * fc * d) / (M_PI * d);
            double w = d / r->half;
            double win = (fabs(w) <= 1.0) ? bessel_i0(beta * sqrt(1.0 - w * w)) / i0b : 0.0;
            row[k] = (float)(s * win);
            sum += row[k];
        }
        for (int k = 0; k < len; k++) /* unity DC gain for every phase */
            row[k] = (float)(row[k] / sum);
    }

    r->in_cap = 4096 + 4 * len;
    r->in = (float *)calloc((size_t)r->in_cap, sizeof(float));
    if (!r->in)
        return -1;
    m17_resampler_reset(r);
    return 0;
}

void m17_resampler_reset(m17_resampler_t *r)
{
    if (!r->in)
        return;
    /* Prime with zeros so the first output has a full history. */
    r->in_len = 2 * r->half;
    memset(r->in, 0, sizeof(float) * (size_t)r->in_len);
    r->t = r->half - 1;
}

void m17_resampler_free(m17_resampler_t *r)
{
    free(r->table);
    free(r->in);
    memset(r, 0, sizeof(*r));
}

int m17_resampler_run(m17_resampler_t *r, const float *x, int n, float *y, int max)
{
    int out = 0;
    int consumed = 0;
    const int len = 2 * r->half;

    for (;;)
    {
        /* Produce while the kernel fits inside the pending input. */
        while (out < max)
        {
            int i = (int)floor(r->t);
            if (i + r->half + 1 > r->in_len)
                break;
            double mu = r->t - i;
            int p = (int)(mu * r->phases + 0.5);
            int base = i - (r->half - 1);
            if (p >= r->phases) /* rounded up to the next sample */
            {
                p = 0;
                base++;
            }
            const float *row = &r->table[(size_t)p * len];
            const float *xin = &r->in[base];
            float acc = 0.0f;
            for (int k = 0; k < len; k++)
                acc += row[k] * xin[k];
            y[out++] = acc;
            r->t += r->ratio;
        }

        /* Drop input that no future output needs. */
        int keep_from = (int)floor(r->t) - (r->half - 1) - 1;
        if (keep_from > 0)
        {
            if (keep_from > r->in_len)
                keep_from = r->in_len;
            memmove(r->in, r->in + keep_from, sizeof(float) * (size_t)(r->in_len - keep_from));
            r->in_len -= keep_from;
            r->t -= keep_from;
        }

        if (consumed >= n || out >= max)
            break;

        /* Append more input. */
        int room = r->in_cap - r->in_len;
        int take = n - consumed;
        if (take > room)
            take = room;
        if (take <= 0)
            break;
        memcpy(r->in + r->in_len, x + consumed, sizeof(float) * (size_t)take);
        r->in_len += take;
        consumed += take;
    }
    return out;
}

int m17_fir_init(m17_fir_t *f, const float *taps, int ntaps, float scale)
{
    memset(f, 0, sizeof(*f));
    f->ntaps = ntaps;
    f->taps = (float *)malloc(sizeof(float) * (size_t)ntaps);
    f->hist = (float *)calloc((size_t)(2 * ntaps), sizeof(float));
    if (!f->taps || !f->hist)
        return -1;
    for (int i = 0; i < ntaps; i++)
        f->taps[i] = taps[i] * scale;
    return 0;
}

void m17_fir_free(m17_fir_t *f)
{
    free(f->taps);
    free(f->hist);
    memset(f, 0, sizeof(*f));
}

void m17_fir_reset(m17_fir_t *f)
{
    if (f->hist)
        memset(f->hist, 0, sizeof(float) * (size_t)(2 * f->ntaps));
    f->pos = 0;
}

float m17_fir_step(m17_fir_t *f, float x)
{
    f->hist[f->pos] = x;
    f->hist[f->pos + f->ntaps] = x;
    f->pos = (f->pos + 1) % f->ntaps;
    /* hist[pos .. pos+ntaps-1] is oldest .. newest */
    const float *h = &f->hist[f->pos];
    float acc = 0.0f;
    for (int k = 0; k < f->ntaps; k++)
        acc += f->taps[f->ntaps - 1 - k] * h[k];
    return acc;
}
