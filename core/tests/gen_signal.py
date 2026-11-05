#!/usr/bin/env python3
"""Make M17 test signals with gr-m17's coder, the way a real transmitter would send them.

  gen_signal.py symbols <kind> <out.sym> [options]   -> 1 sps symbols from m17_coder
  gen_signal.py iq <in.sym> <out.cf32> [impairments]  -> complex baseband at a chosen rate

Symbols -> zero-stuffed x10, RRC (alpha 0.5, 81 taps, unity DC gain, as gr-m17's
TX example) -> FM at 800 Hz per symbol unit (+-3 = +-2.4 kHz) at 48 kHz ->
resampled to the target rate, with optional carrier offset, symbol clock error and AWGN.

Needs GNU Radio + gr-m17 (for `symbols`) and numpy/scipy (for `iq`).
"""
import argparse
import os
import sys

import numpy as np


def cmd_symbols(a):
    import gnuradio
    gnuradio.__path__.append(os.environ["GR_M17_PKG"])
    import pmt
    from gnuradio import blocks, gr, m17

    def h(s):
        return bytes.fromhex(s).decode("latin-1")

    if a.kind == "sms":
        tb = gr.top_block()
        src = blocks.vector_source_b([0] * 16, True)
        coder = m17.m17_coder(a.src, a.dst, 2, 0, 0, 0, a.can, m17.build_meta(0, 0), "", "", False, False, "", 1, False)
        sink = blocks.vector_sink_f()
        tb.connect(src, coder, sink)
        tb.start()
        coder.to_basic_block()._post(pmt.intern("transmission_control"),
                                     pmt.cons(pmt.intern("SMS"), pmt.intern(a.text)))
        # the coder sends the packet once and then idles: stop when the output stops growing
        import time
        last, stable = -1, 0
        for _ in range(200):
            time.sleep(0.05)
            n = len(sink.data())
            stable = stable + 1 if (n == last and n > 0) else 0
            last = n
            if stable >= 6:
                break
        tb.stop()
        tb.wait()
        sym = np.array(sink.data(), dtype=np.float32)
        # trim the idle tail after the EoT
        nz = np.nonzero(sym)[0]
        sym = sym[: nz[-1] + 1] if len(nz) else sym
    else:
        payload = open(a.payload, "rb").read()
        if a.key:
            a.aes = True
        enc = 2 if a.aes else (1 if a.scr else 0)
        if a.aes:
            sub = (len(a.key) // 2 - 16) // 8 if a.key else 2      # AES-128/192/256
        elif a.scr:
            sub = len(a.scr) // 2 - 1                              # 8/16/24-bit LFSR
        else:
            sub = a.meta_type if a.kind == "voice" else 0
        if a.meta_type == 1:
            meta = m17.build_meta(0, 1, "", a.lat, a.lon, a.alt, 0.0, 0)
        elif a.meta_type == 2:
            meta = m17.build_meta(0, 2, "", cf1=a.cf1, cf2=a.cf2)
        else:
            meta = m17.build_meta(enc, 0, a.text if not (a.aes or a.scr) else "")
        dtype = 2 if a.kind == "voice" else 1
        key = (a.key or os.urandom(32).hex()) if a.aes else ""
        tb = gr.top_block()
        src = blocks.vector_source_b(list(payload), False)
        coder = m17.m17_coder(a.src, a.dst, dtype, enc, sub, sub, a.can, meta, h(key), h(a.priv or ""),
                              False, bool(a.priv), h(a.scr or ""), 1, True)
        sink = blocks.vector_sink_f()
        tb.connect(src, coder, sink)
        tb.run()
        sym = np.array(sink.data(), dtype=np.float32)
    sym.tofile(a.out)
    print(f"{len(sym)} symbols ({len(sym) / 4800:.2f} s)", file=sys.stderr)


def rrc_taps():
    # same design as libm17 rrc_taps_10: alpha 0.5, span 8 symbols, 10 sps
    sps, span, alpha = 10, 8, 0.5
    t = (np.arange(span * sps + 1) - span * sps / 2) / sps
    h = np.zeros_like(t)
    for i, x in enumerate(t):
        if abs(x) < 1e-9:
            h[i] = 1 - alpha + 4 * alpha / np.pi
        elif abs(abs(x) - 1 / (4 * alpha)) < 1e-9:
            h[i] = alpha / np.sqrt(2) * ((1 + 2 / np.pi) * np.sin(np.pi / (4 * alpha)) +
                                         (1 - 2 / np.pi) * np.cos(np.pi / (4 * alpha)))
        else:
            h[i] = (np.sin(np.pi * x * (1 - alpha)) + 4 * alpha * x * np.cos(np.pi * x * (1 + alpha))) / \
                   (np.pi * x * (1 - (4 * alpha * x) ** 2))
    return h / h.sum() * sps  # zero-stuffed input: DC gain 1


def cmd_iq(a):
    from fractions import Fraction
    from scipy.signal import lfilter, resample_poly

    rng = np.random.default_rng(a.seed)
    sym = np.fromfile(a.sym, dtype=np.float32)
    if a.lead > 0:  # unmodulated carrier before the transmission, as a radio keys up
        sym = np.concatenate([np.zeros(int(a.lead * 4800), np.float32), sym])
    up = np.zeros(len(sym) * 10, np.float64)
    up[::10] = sym
    bb = lfilter(rrc_taps(), 1.0, up)
    fs0 = 48000.0
    # symbol clock error: the transmitter runs (1 + ppm) fast
    if a.ppm:
        n = len(bb)
        t_new = np.arange(int(n / (1 + a.ppm * 1e-6))) * (1 + a.ppm * 1e-6)
        bb = np.interp(t_new, np.arange(n), bb)
    freq = bb * a.dev / 3.0 + a.offset
    # carrier present only while transmitting (plus the lead-in), noise everywhere
    phase = 2 * np.pi * np.cumsum(freq) / fs0
    iq = np.exp(1j * phase)
    pad = int(a.pad * fs0)
    iq = np.concatenate([np.zeros(pad, complex), iq, np.zeros(pad, complex)])
    frac = Fraction(a.rate / fs0).limit_denominator(1000)
    iq = resample_poly(iq, frac.numerator, frac.denominator)
    # AWGN for a given C/N in a 12.5 kHz channel
    if a.cn is not None:
        p_noise_total = 10 ** (-a.cn / 10) * (a.rate / 12500.0)
        iq = iq + np.sqrt(p_noise_total / 2) * (rng.standard_normal(len(iq)) + 1j * rng.standard_normal(len(iq)))
    else:
        iq = iq + 1e-4 * (rng.standard_normal(len(iq)) + 1j * rng.standard_normal(len(iq)))
    out = np.empty(2 * len(iq), np.float32)
    out[0::2] = iq.real * a.scale
    out[1::2] = iq.imag * a.scale
    out.tofile(a.out)
    print(f"{len(iq)} samples at {a.rate:.0f} Hz ({len(iq) / a.rate:.2f} s)", file=sys.stderr)


p = argparse.ArgumentParser()
sp = p.add_subparsers(dest="cmd", required=True)
s = sp.add_parser("symbols")
s.add_argument("kind", choices=["voice", "data", "sms"])
s.add_argument("out")
s.add_argument("--payload")
s.add_argument("--src", default="N0CALL")
s.add_argument("--dst", default="@ALL")
s.add_argument("--can", type=int, default=0)
s.add_argument("--text", default="")
s.add_argument("--meta-type", type=int, default=0)
s.add_argument("--lat", type=float, default=0.0)
s.add_argument("--lon", type=float, default=0.0)
s.add_argument("--alt", type=float, default=0.0)
s.add_argument("--cf1", default="")
s.add_argument("--cf2", default="")
s.add_argument("--aes", action="store_true")
s.add_argument("--key", default="", help="AES key, hex (32/48/64 digits)")
s.add_argument("--scr", default="", help="scrambler seed, hex (2/4/6 digits)")
s.add_argument("--priv")
i = sp.add_parser("iq")
i.add_argument("sym")
i.add_argument("out")
i.add_argument("--rate", type=float, default=48000.0)
i.add_argument("--dev", type=float, default=2400.0, help="outer symbol deviation, Hz")
i.add_argument("--offset", type=float, default=0.0, help="carrier offset, Hz")
i.add_argument("--ppm", type=float, default=0.0, help="symbol clock error")
i.add_argument("--cn", type=float, default=None, help="C/N in 12.5 kHz, dB (default: practically noiseless)")
i.add_argument("--lead", type=float, default=0.1, help="unmodulated carrier before the TX, s")
i.add_argument("--pad", type=float, default=0.2, help="noise-only time before and after, s")
i.add_argument("--scale", type=float, default=0.3)
i.add_argument("--seed", type=int, default=1)
a = p.parse_args()
{"symbols": cmd_symbols, "iq": cmd_iq}[a.cmd](a)
