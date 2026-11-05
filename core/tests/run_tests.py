#!/usr/bin/env python3
"""libm17rx test suite: gr-m17-coded signals through realistic impairments.

Needs: GNU Radio + gr-m17 (GR_M17_PKG pointing at its installed gnuradio/ dir),
numpy, scipy, the built m17rx_cli and c2gen.

  run_tests.py <build dir> [work dir]
"""
import json
import os
import subprocess
import sys

import numpy as np

BUILD = os.path.abspath(sys.argv[1])
WORK = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else "/tmp/m17rx_tests")
HERE = os.path.dirname(os.path.abspath(__file__))
GEN = [sys.executable, os.path.join(HERE, "gen_signal.py")]
os.makedirs(WORK, exist_ok=True)

results = []


def sh(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode:
        raise RuntimeError(f"{cmd[0]} failed: {r.stderr[-500:]}")
    return r.stdout


def symbols(name, kind, **kw):
    out = os.path.join(WORK, name + ".sym")
    if not os.path.exists(out):
        args = GEN + ["symbols", kind, out]
        for k, v in kw.items():
            flag = "--" + k.replace("_", "-")
            if v is True:
                args.append(flag)
            else:
                args += [flag, str(v)]
        sh(args)
    return out


def iq(sym, name, **kw):
    out = os.path.join(WORK, name + ".cf32")
    args = GEN + ["iq", sym, out]
    for k, v in kw.items():
        args += ["--" + k, str(v)]
    sh(args)
    return out


def rx(path, rate, *extra):
    audio = path + ".s16"
    out = sh([os.path.join(BUILD, "m17rx_cli"), str(rate), path, "--audio", audio, *extra])
    lines = [json.loads(l) for l in out.splitlines() if l.strip()]
    events = [l for l in lines if "event" in l]
    status = next(l["status"] for l in lines if "status" in l)
    pcm = np.fromfile(audio, np.int16) if os.path.exists(audio) else np.zeros(0, np.int16)
    return events, status, pcm


def check(name, cond, detail=""):
    results.append((name, bool(cond), detail))
    print(f"{'PASS' if cond else 'FAIL'}  {name}  {detail}")


def kinds(events):
    return [e["event"] for e in events]


# ---------------------------------------------------------------- material
c2 = os.path.join(WORK, "voice.c2")
ref = os.path.join(WORK, "voice_ref.s16")
if not os.path.exists(c2):
    sh([os.path.join(BUILD, "c2gen"), "6", c2, ref])
REF = np.fromfile(ref, np.int16)

s_text = symbols("text", "voice", payload=c2, src="SP5WWP", dst="@ALL", text="Hello from the M17 test bench")
s_gnss = symbols("gnss", "voice", payload=c2, src="N0CALL", meta_type=1, lat=52.2297, lon=21.0122, alt=112)
s_ecd = symbols("ecd", "voice", payload=c2, src="M17-XYZ", meta_type=2, cf1="SP5WWP", cf2="M17-ABC")
s_aes = symbols("aes", "voice", payload=c2, src="N0CALL", aes=True)
s_sig = symbols("signed", "voice", payload=c2, src="N0CALL", priv="11" * 32)
s_data = symbols("data", "data", payload=c2, src="N0CALL", can=5)
K128, K192, K256 = "2b7e151628aed2a6abf7158809cf4f3c", "8e73b0f7da0e6452c810f32b809079e562f8ead2522c6b7b", \
    "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"
s_aes128 = symbols("aes128", "voice", payload=c2, src="N0CALL", key=K128)
s_aes192 = symbols("aes192", "voice", payload=c2, src="N0CALL", key=K192)
s_aes256 = symbols("aes256", "voice", payload=c2, src="N0CALL", key=K256)
s_scr = {b: symbols(f"scr{b}", "voice", payload=c2, src="N0CALL", scr=seed)
         for b, seed in ((8, "a5"), (16, "c3e1"), (24, "5a3c96"))}
s_sig_aes = symbols("signed_aes", "voice", payload=c2, src="N0CALL", priv="11" * 32, key=K256)
s_sig_scr = symbols("signed_scr", "voice", payload=c2, src="N0CALL", priv="11" * 32, scr="c3e1")
PUB11 = "0217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794"
PUB22 = "d65a93977caa3d1b081852ff57a79e465f1660577304baead505dd3a48589cf350185e895372df6221ea3a137557e473fddb6755f05bd507c3c533fce9c91285"
s_sms = symbols("sms", "sms", src="SP5WWP", dst="N0CALL", text="Test SMS from gr-m17, zażółć gęślą jaźń")


def audio_ok(pcm):
    return len(pcm) >= len(REF) and np.array_equal(pcm[: len(REF)], REF)


# ---------------------------------------------------------------- 1. sample rates
for rate in (12500, 16000, 24000, 25000, 32000, 37500, 48000, 62500, 96000, 125000, 250000, 1024000):
    ev, st, pcm = rx(iq(s_text, f"rate{rate}", rate=rate), rate)
    end = [e for e in ev if e["event"] == "stream_end"]
    check(f"rate {rate} Hz", audio_ok(pcm) and end and end[0]["lost"] == 0 and end[0]["reason"] == 1,
          f"EVM {st['evm']:.2f}%  dev {st['dev']:.0f} Hz  events {kinds(ev)}")

# ---------------------------------------------------------------- 2. impairments (32 kHz)
R = 32000
for label, kw, exp in [
    ("offset +1500 Hz", dict(offset=1500), dict(offset=1500)),
    ("offset -2500 Hz", dict(offset=-2500), dict(offset=-2500)),
    ("deviation 1800 Hz", dict(dev=1800), dict(dev=1800)),
    ("deviation 3000 Hz", dict(dev=3000), dict(dev=3000)),
    ("clock +100 ppm", dict(ppm=100), dict(ppm=100)),
    ("clock -100 ppm", dict(ppm=-100), dict(ppm=-100)),
]:
    ev, st, pcm = rx(iq(s_text, "imp", rate=R, **kw), R)
    k, v = next(iter(exp.items()))
    measured = st[k]
    tol = {"offset": 15, "dev": 30, "ppm": 15}[k]
    check(label, audio_ok(pcm) and abs(measured - v) < tol, f"measured {k} {measured:.1f}  EVM {st['evm']:.2f}%")

# inverted polarity: wrong setting finds nothing, right setting decodes
p = iq(s_text, "inv", rate=R, dev=-2400)
ev1, _, _ = rx(p, R)
ev2, st2, pcm2 = rx(p, R, "--invert")
check("inverted signal, invert off: no false decode", len(ev1) == 0, f"events {kinds(ev1)}")
check("inverted signal, invert on: decodes", audio_ok(pcm2), f"EVM {st2['evm']:.2f}%")

# ---------------------------------------------------------------- 3. noise
prev = 0
for cn in (25, 20, 16, 13, 11, 9):
    ev, st, pcm = rx(iq(s_text, f"cn{cn}", rate=R, cn=cn, seed=cn), R)
    end = [e for e in ev if e["event"] == "stream_end"]
    frames = end[0]["frames"] if end else 0
    lost = end[0]["lost"] if end else -1
    started = "stream_start" in kinds(ev)
    ok = started and frames > 140 if cn >= 13 else started
    check(f"C/N {cn} dB", ok and st["evm"] >= prev * 0.8,
          f"frames {frames} lost {lost}  EVM {st['evm']:.1f}%  MER {st['mer']:.1f} dB  pre-FEC BER {st['ber_pre']:.4f}  "
          f"bit-exact audio {audio_ok(pcm)}")
    prev = st["evm"]

ev, _, _ = rx(iq(s_text, "noise_only", rate=R, cn=-30, seed=7), R)
# a pure-noise file: C/N -30 dB buries the signal entirely
check("noise only: no events", len(ev) == 0, f"events {kinds(ev)}")

# ---------------------------------------------------------------- 4. late join (LSF missed)
full = np.fromfile(iq(s_text, "late", rate=R), np.float32)
cut = full[2 * int(R * (0.2 + 0.1 + 0.5)):]  # skip pad, lead-in, preamble, LSF and ~10 frames
cut.tofile(os.path.join(WORK, "late_cut.cf32"))
ev, st, pcm = rx(os.path.join(WORK, "late_cut.cf32"), R)
starts = [e for e in ev if e["event"] == "stream_start"]
check("late join: link from LICH", starts and starts[0]["link"]["from_lich"] == 1 and starts[0]["link"]["src"] == "SP5WWP",
      f"events {kinds(ev)}")

# ---------------------------------------------------------------- 5. META kinds
ev, st, _ = rx(iq(s_gnss, "gnss", rate=R), R)
l = ev[0]["link"]
check("META GNSS", l["meta_kind"] == 2 and abs(l["lat"] - 52.2297) < 1e-3 and abs(l["lon"] - 21.0122) < 1e-3
      and abs(l["alt"] - 112) < 1 and l["pos_valid"] == 1, f"{l['lat']:.5f}, {l['lon']:.5f}, {l['alt']} m")
ev, st, _ = rx(iq(s_ecd, "ecd", rate=R), R)
l = ev[0]["link"]
check("META Extended Callsign", l["meta_kind"] == 3 and l["ecd1"] == "SP5WWP" and l["ecd2"] == "M17-ABC",
      f"{l['ecd1']} / {l['ecd2']}")

# ---------------------------------------------------------------- 6. AES (no key in this version): muted
ev, st, pcm = rx(iq(s_aes, "aes", rate=R), R)
l = ev[0]["link"]
check("AES stream: flagged and muted", l["enc"] == 2 and l["enc_sub"] == 2 and len(pcm) > 0 and not np.any(pcm),
      f"enc {l['enc']}/{l['enc_sub']}, {len(pcm)} samples, max |pcm| {int(np.max(np.abs(pcm))) if len(pcm) else 0}")

# ---------------------------------------------------------------- 7. signed: signature frames are not played or counted
ev, st, pcm = rx(iq(s_sig, "signed", rate=R), R)
end = [e for e in ev if e["event"] == "stream_end"]
check("signed stream", ev[0]["link"]["signed"] == 1 and audio_ok(pcm) and end and end[0]["frames"] == 151
      and end[0]["lost"] == 0 and not np.any(pcm[len(REF) + 320:]),
      f"frames {end[0]['frames'] if end else '-'}, audio {len(pcm)} samples")

# ---------------------------------------------------------------- 7a. decryption
def end_of(ev):
    e = [x for x in ev if x["event"] == "stream_end"]
    return e[0] if e else None


for bits, sym, key in ((128, s_aes128, K128), (192, s_aes192, K192), (256, s_aes256, K256)):
    ev, st, pcm = rx(iq(sym, f"aes{bits}", rate=R), R, "--aes", key)
    l = ev[0]["link"]
    check(f"AES-{bits}: decrypted, audio bit-exact", l["enc"] == 2 and l["decrypt"] == 1 and audio_ok(pcm),
          f"enc {l['enc']}/{l['enc_sub']} decrypt {l['decrypt']}")
ev, st, pcm = rx(iq(s_aes256, "aes256", rate=R), R, "--aes", K128)
check("AES-256 with a 128-bit key: no key, muted", ev[0]["link"]["decrypt"] == 2 and not np.any(pcm))
ev, st, pcm = rx(iq(s_aes256, "aes256", rate=R), R)
check("AES-256 without a key: no key, muted", ev[0]["link"]["decrypt"] == 2 and not np.any(pcm))
for bits, seed in ((8, "a5"), (16, "c3e1"), (24, "5a3c96")):
    ev, st, pcm = rx(iq(s_scr[bits], f"scr{bits}", rate=R), R, "--scr", seed)
    l = ev[0]["link"]
    check(f"scrambler {bits}-bit: descrambled, audio bit-exact", l["enc"] == 1 and l["decrypt"] == 1 and audio_ok(pcm),
          f"enc {l['enc']}/{l['enc_sub']} decrypt {l['decrypt']}")


def edit_sym(src, name, start_frame=None, drop_frame=None):
    """Symbol-level edits: stream frame k starts at 384 + 192 k (preamble, LSF before it)."""
    out = os.path.join(WORK, name + ".sym")
    x = np.fromfile(src, np.float32)
    if drop_frame is not None:
        a = 384 + 192 * drop_frame
        x = np.concatenate([x[:a], x[a + 192:]])
    if start_frame is not None:
        x = x[384 + 192 * start_frame:]
    x.tofile(out)
    return out


# late join into a scrambled stream: the keystream is recomputed from the FN; the audio must equal
# a late join into the same transmission in the clear (same frames, same Codec2 state)
ev_c, _, pcm_c = rx(iq(edit_sym(s_sig, "late_clear", start_frame=37), "late_clear", rate=R), R)
ev_s, _, pcm_s = rx(iq(edit_sym(s_sig_scr, "late_scr", start_frame=37), "late_scr", rate=R), R, "--scr", "c3e1")
check("scrambler, late join at FN 37: same audio as the clear stream", len(pcm_s) > 8000 and np.any(pcm_s)
      and len(pcm_s) == len(pcm_c) and np.array_equal(pcm_s, pcm_c), f"{len(pcm_s)} vs {len(pcm_c)} samples")

# ---------------------------------------------------------------- 7b. signatures
sig_cases = [
    ("right key under the source callsign", s_sig, "signed", ["--pub", "N0CALL=" + PUB11], 1, "N0CALL"),
    ("right key under another name: not tried", s_sig, "signed", ["--pub", "SP5WWP=" + PUB22, "--pub", "club key=" + PUB11], 3, ""),
    ("callsign matched case-insensitively", s_sig, "signed", ["--pub", "n0call=" + PUB11], 1, "n0call"),
    ("wrong key", s_sig, "signed", ["--pub", "N0CALL=" + PUB22], 2, ""),
    ("no keys", s_sig, "signed", [], 3, ""),
    ("sender's key wrong, right key under another name", s_sig, "signed", ["--pub", "N0CALL=" + PUB22, "--pub", "other=" + PUB11], 2, ""),
    ("AES-256 + signed", s_sig_aes, "signed_aes", ["--aes", K256, "--pub", "N0CALL=" + PUB11], 1, "N0CALL"),
    ("scrambler + signed", s_sig_scr, "signed_scr", ["--scr", "c3e1", "--pub", "N0CALL=" + PUB11], 1, "N0CALL"),
]
for what, sym, name, extra, want, key in sig_cases:
    ev, st, pcm = rx(iq(sym, name, rate=R), R, *extra)
    e = end_of(ev)
    voice = audio_ok(pcm) if "--aes" in extra or "--scr" in extra or name == "signed" else True
    check(f"signature, {what}", e and e["sig"] == want and e["sig_key"] == key and voice,
          f"sig {e['sig'] if e else '-'} key '{e['sig_key'] if e else ''}'")
ev, st, pcm = rx(iq(edit_sym(s_sig, "sig_drop", drop_frame=60), "sig_drop", rate=R), R, "--pub", "N0CALL=" + PUB11)
e = end_of(ev)
check("signature, one frame lost: incomplete, not invalid", e and e["sig"] == 4 and e["lost"] == 1,
      f"sig {e['sig'] if e else '-'} lost {e['lost'] if e else '-'}")
ev, st, pcm = rx(iq(edit_sym(s_sig, "sig_late", start_frame=37), "sig_late", rate=R), R, "--pub", "N0CALL=" + PUB11)
e = end_of(ev)
check("signature, late join: incomplete", e and e["sig"] == 4, f"sig {e['sig'] if e else '-'}")
ev, st, pcm = rx(iq(s_text, "text", rate=R), R, "--pub", "N0CALL=" + PUB11)
e = end_of(ev)
check("unsigned stream: no signature state", e and e["sig"] == 0)

# ---------------------------------------------------------------- 8. data stream: no audio
ev, st, pcm = rx(iq(s_data, "data", rate=R), R)
check("data stream: CAN, no audio", ev[0]["link"]["data_type"] == 1 and ev[0]["link"]["can"] == 5 and len(pcm) == 0,
      f"audio {len(pcm)} samples")

# ---------------------------------------------------------------- 9. SMS
ev, st, pcm = rx(iq(s_sms, "sms", rate=R), R)
pk = [e for e in ev if e["event"] == "packet"]
check("SMS packet", pk and pk[0]["sms"] == "Test SMS from gr-m17, zażółć gęślą jaźń" and pk[0]["link"]["src"] == "SP5WWP"
      and pk[0]["link"]["dst"] == "N0CALL", f"{pk[0]['sms'] if pk else kinds(ev)}")

# ---------------------------------------------------------------- 10. two transmissions back to back
a = np.fromfile(iq(s_gnss, "b2b_a", rate=R), np.float32)
b = np.fromfile(iq(s_text, "b2b_b", rate=R), np.float32)
np.concatenate([a, b]).tofile(os.path.join(WORK, "b2b.cf32"))
ev, st, pcm = rx(os.path.join(WORK, "b2b.cf32"), R)
srcs = [e["link"]["src"] for e in ev if e["event"] == "stream_start"]
ends = [e for e in ev if e["event"] == "stream_end"]
check("two transmissions", srcs == ["N0CALL", "SP5WWP"] and len(ends) == 2 and all(e["lost"] == 0 for e in ends),
      f"starts {srcs}, ends {len(ends)}")

# ---------------------------------------------------------------- 11. receiver noise after the transmission
# (carrier gone, squelch open): no false lock may bring back the station or play noise as voice
bad = []
for seed in range(1, 7):
    ev, st, pcm = rx(iq(s_text, f"tail{seed}", rate=R, cn=18, pad=8, seed=seed), R)
    starts = sum(1 for e in ev if e["event"] == "stream_start")
    if starts != 1 or len(pcm) != 151 * 320 or st["link_valid"] != 0:
        bad.append((seed, starts, len(pcm), st["link_valid"]))
check("8 s of noise after the TX, 6 runs: no false lock, no stale link", not bad, f"problems {bad}" if bad else "")

fails = [r for r in results if not r[1]]
print(f"\n{len(results) - len(fails)}/{len(results)} passed")
sys.exit(1 if fails else 0)
