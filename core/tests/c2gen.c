/*
 * c2gen - make test Codec2 3200 frames and their reference decode.
 *
 *   c2gen <seconds> <out.c2> <ref.s16>
 *
 * The "speech" is a voiced harmonic signal with a gliding pitch and syllable-like
 * envelope, with short pauses, so the frames exercise real Codec2 parameters.
 * ref.s16 is what a freshly initialised Codec2 decoder makes of out.c2.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#include "codec2_mod.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    if (argc != 4)
    {
        fprintf(stderr, "usage: %s seconds out.c2 ref.s16\n", argv[0]);
        return 2;
    }
    int frames = (int)(atof(argv[1]) * 50.0);
    frames += frames & 1; /* whole M17 frames: 2 Codec2 frames each */
    FILE *fc = fopen(argv[2], "wb"), *fr = fopen(argv[3], "wb");
    if (!fc || !fr)
        return 1;

    codec2_encoder_t enc;
    codec2_decoder_t dec;
    codec2_encoder_init(&enc);
    codec2_decoder_init(&dec);
    int16_t pcm[SAMPLES_PER_FRAME], out[SAMPLES_PER_FRAME];
    uint8_t bits[8];
    double phase = 0, t = 0;

    for (int f = 0; f < frames; f++)
    {
        for (int i = 0; i < SAMPLES_PER_FRAME; i++, t += 1.0 / 8000)
        {
            double syl = fmod(t, 0.35) / 0.35;                       /* 350 ms syllables */
            double env = (fmod(t, 2.0) > 1.6) ? 0 : sin(M_PI * syl); /* pause every 2 s */
            double f0 = 120 + 40 * sin(2 * M_PI * 0.7 * t);
            phase += 2 * M_PI * f0 / 8000;
            double x = 0;
            for (int h = 1; h <= 15; h++)
            {
                double fh = f0 * h;
                double formant = exp(-pow((fh - 700) / 300, 2)) + 0.6 * exp(-pow((fh - 1200) / 400, 2)) + 0.05;
                x += formant * sin(h * phase) / h;
            }
            pcm[i] = (int16_t)(9000 * env * x);
        }
        codec2_encode(&enc, bits, pcm);
        codec2_decode(&dec, out, bits);
        fwrite(bits, 1, 8, fc);
        fwrite(out, sizeof(int16_t), SAMPLES_PER_FRAME, fr);
    }
    fclose(fc);
    fclose(fr);
    return 0;
}
