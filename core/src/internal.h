/*
 * libm17rx - internal declarations
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 */
#ifndef M17RX_INTERNAL_H
#define M17RX_INTERNAL_H

#include "m17rx.h"
#include "dsp.h"
#include "m17.h"
#include "codec2_mod.h"

typedef struct
{
    unsigned used, have;
    uint8_t  block[4][13];
} m17_text_t;

void m17_text_reset(m17_text_t *t);
/* Fill link from a CRC-valid LSF; returns 1 if it changed in a reportable way. */
int  m17_link_from_lsf(m17rx_link_t *link, const lsf_t *lsf, int from_lich, m17_text_t *text);

#endif
