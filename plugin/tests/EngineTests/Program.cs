// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SDRSharp.M17.Engine;
using SDRSharp.M17.Native;

static unsafe class Program
{
    static int fails;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
        if (!ok) fails++;
    }

    // Simulate SDR#: every 10 ms the DSP thread delivers IQ, then the audio thread wants 10 ms of 48 kHz stereo.
    static (List<M17Event> ev, float[] audio, int rebuffers) Run(string iqPath, double rate, int channels = 2, Action<M17Engine> setup = null)
    {
        float[] iq = File.ReadAllBytes(iqPath) is var b ? new float[b.Length / 4] : null;
        Buffer.BlockCopy(File.ReadAllBytes(iqPath), 0, iq, 0, iq.Length * 4);
        using var eng = new M17Engine();
        eng.SetInputRate(rate);
        setup?.Invoke(eng);
        var events = new List<M17Event>();
        var outAudio = new List<float>();
        int perBlockIq = (int)(rate / 100), perBlockOut = 480, rebuffers = 0;
        bool wasPlaying = false;
        float[] buf = new float[perBlockOut * channels];
        // keep going 1 s after the input ends so the jitter buffer plays out
        int blocks = iq.Length / 2 / perBlockIq + 100;
        for (int blk = 0; blk < blocks; blk++)
        {
            int start = blk * perBlockIq;
            int n = Math.Max(0, Math.Min(perBlockIq, iq.Length / 2 - start));
            if (n > 0)
                fixed (float* p = &iq[2 * start]) eng.ProcessIq(p, n);
            while (eng.TryGetEvent(out var e)) events.Add(e);
            bool played;
            fixed (float* o = buf) played = eng.Audio.Read(o, perBlockOut, channels, 48000);
            if (played) outAudio.AddRange(buf); else outAudio.AddRange(new float[buf.Length]);
            if (wasPlaying && !played) rebuffers++;
            wasPlaying = played;
        }
        return (events, outAudio.ToArray(), rebuffers);
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "/tmp/m17rx_tests";
        Console.WriteLine($"native: {M17RxNative.Version}  from {M17RxNative.LoadedFrom}");

        // 1. struct layouts agree with the library
        bool layout = true;
        try { M17RxNative.CheckCompatible(); } catch (Exception ex) { layout = false; Console.WriteLine(ex.Message); }
        Check("struct layout and API version", layout,
              $"link {sizeof(M17RxLink)}, status {sizeof(M17RxStatus)}, event {sizeof(M17RxEvent)}, config {sizeof(M17RxConfig)}");

        // 2. voice through engine + jitter buffer, 48 kHz stereo out, compare with the reference decode
        var refPcm = File.ReadAllBytes(Path.Combine(dir, "voice_ref.s16"));
        short[] reference = new short[refPcm.Length / 2];
        Buffer.BlockCopy(refPcm, 0, reference, 0, refPcm.Length);
        var (ev, audio, rebuf) = Run(Path.Combine(dir, "rate32000.cf32"), 32000);
        var kinds = string.Join(",", ev.Select(e => e.Kind.ToString()));
        var start = ev.FirstOrDefault(e => e.Kind == M17EventKind.StreamStart);
        var final = ev.LastOrDefault(e => e.Link.MetaKind == M17MetaKind.Text);
        Check("events through the engine", start != null && start.Link.Source == "SP5WWP" && start.Link.DataTypeText == "VOICE 3200"
              && final != null && final.Link.MetaText == "Hello from the M17 test bench" && final.Link.TextComplete
              && ev.Any(e => e.Kind == M17EventKind.StreamEnd && e.EndReason == M17EndReason.EndOfStream && e.FramesLost == 0),
              kinds);

        // The 8 kHz voice is linearly interpolated to 48 kHz. Model the output as the reference
        // interpolated at one fixed delay: a single delay must fit every second of the stream
        // (a dropout, repeat or skip would show up as a large error in that second).
        var left = Enumerable.Range(0, audio.Length / 2).Select(i => audio[2 * i] * 32768.0).ToArray();
        bool stereoSame = Enumerable.Range(0, audio.Length / 2).All(i => audio[2 * i] == audio[2 * i + 1]);
        int j0 = Array.FindIndex(left, v => v != 0);
        double step = 8000.0 / 48000.0;
        double Model(int j, double dly)
        {
            double t = (j - j0) * step - dly; // x[0] = 0, x[k] = reference[k-1]
            if (t < 0) return 0;
            int i = (int)Math.Floor(t); double f = t - i;
            double a = i == 0 ? 0 : (i - 1 < reference.Length ? reference[i - 1] : 0);
            double b2 = i < reference.Length ? reference[i] : 0;
            return a + (b2 - a) * f;
        }
        double MaxErr(int from, int count, double dly)
        {
            double m = 0;
            for (int j = from; j < from + count && j < left.Length; j++) m = Math.Max(m, Math.Abs(left[j] - Model(j, dly)));
            return m;
        }
        double bestDly = 0, bestErr = double.MaxValue;
        for (double d = -2.0; d <= 0.5; d += 0.0025)
        {
            double e = MaxErr(j0, 48000, d);
            if (e < bestErr) { bestErr = e; bestDly = d; }
        }
        double worst = 0;
        for (int sec = 0; sec < 6; sec++) worst = Math.Max(worst, MaxErr(j0 + sec * 48000, 48000, bestDly));
        Check("audio via jitter buffer = reference decode, continuous", worst < 25 && stereoSame && rebuf == 1,
              $"one delay ({bestDly:F3} samples) fits all 6 s within {worst:F1} LSB, playback stops {rebuf} (end of stream), " +
              $"first audio {j0 / 48.0:F0} ms after the output started");

        // 3. SMS
        var (ev2, _, _) = Run(Path.Combine(dir, "sms.cf32"), 32000);
        var pk = ev2.FirstOrDefault(e => e.Kind == M17EventKind.Packet);
        Check("SMS through the engine", pk != null && pk.Sms == "Test SMS from gr-m17, zażółć gęślą jaźń" && pk.ProtocolText == "SMS",
              pk?.Sms ?? string.Join(",", ev2.Select(e => e.Kind)));
        Check("CSV line", pk != null && CsvLog.Line(pk).Contains("\"Test SMS from gr-m17, zażółć gęślą jaźń\""), pk != null ? CsvLog.Line(pk) : "");

        // 4. GNSS / status / views
        using (var eng = new M17Engine())
        {
            eng.SetInputRate(32000);
            float[] iq = new float[new FileInfo(Path.Combine(dir, "gnss.cf32")).Length / 4];
            Buffer.BlockCopy(File.ReadAllBytes(Path.Combine(dir, "gnss.cf32")), 0, iq, 0, iq.Length * 4);
            // stop half way so the status reflects a live stream
            fixed (float* p = iq) eng.ProcessIq(p, iq.Length / 4);
            var st = eng.GetStatus();
            float[] eye = new float[200 * M17RxNative.EyePoints];
            int traces = eng.GetEye(eye);
            float[] hist = new float[M17RxNative.HistBins];
            eng.GetHistogram(hist);
            float[] evm = new float[M17RxNative.EvmHistory];
            int nevm = eng.GetEvmHistory(evm);
            Check("live status", st.Locked && st.State == 1 && st.MeasValid && st.Link.MetaKind == M17MetaKind.Gnss
                  && Math.Abs(st.Link.Lat - 52.2297) < 1e-3 && st.Deviation > 2300 && st.Deviation < 2500,
                  $"state {st.State}, {st.Link.Source}, {st.Link.MetaSummary}, EVM {st.EvmRms:F2}%, dev {st.Deviation:F0} Hz, frames {st.Frames}");
            int peaks = 0;
            for (int i = 1; i < hist.Length - 1; i++) if (hist[i] > hist[i - 1] && hist[i] >= hist[i + 1] && hist[i] > 10) peaks++;
            Check("signal views", traces == 200 && nevm > 10 && peaks == 4 && eye.Take(traces * 21).All(v => Math.Abs(v) < 6),
                  $"{traces} eye traces, {nevm} EVM points, {peaks} histogram peaks");
        }

        // ---------------------------------------------------------------- keys
        const string K256 = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";
        const string PUB11 = "0217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794";
        // the same key as PEM (SubjectPublicKeyInfo), as openssl ec -pubout writes it
        var spki = new byte[] { 0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86,
                                0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04 }.Concat(KeyText.Hex(PUB11)).ToArray();
        string pem = "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(spki).Insert(64, "\n") + "\n-----END PUBLIC KEY-----\n";
        Check("key formats", KeyText.PublicKey(PUB11)?.Length == 64 && KeyText.PublicKey("04" + PUB11)?.Length == 65
              && KeyText.PublicKey(pem)?.SequenceEqual(new byte[] { 4 }.Concat(KeyText.Hex(PUB11))) == true
              && KeyText.TryAes("60:3d:eb:10 15ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4", out var ka, out _) && ka.Length == 32
              && !KeyText.TryAes("1234", out _, out _) && KeyText.TrySeed("c3e1", out var sd, out var sb, out _) && sd == 0xC3E1 && sb == 16
              && !KeyText.TrySeed("00", out _, out _, out _));
        var st0 = new Settings { AesKey = K256, ScramblerSeed = "c3e1" };
        Check("stored keys are not plain text in the JSON", !st0.AesKeyStored.Contains(K256) && st0.AesKey == K256 && st0.ScramblerSeed == "c3e1");

        var ks = new Settings { AesKey = K256 };
        ks.PublicKeys.Add(new PublicKeyEntry { Name = "N0CALL", Key = pem });
        ks.PublicKeys.Add(new PublicKeyEntry { Name = "broken", Key = "04" + new string('1', 128) });
        string keyNote = "";
        var (evA, auA, _) = Run(Path.Combine(dir, "signed_aes.cf32"), 32000, 2, e => keyNote = KeyConfig.Apply(e, ks));
        var endA = evA.FirstOrDefault(e => e.Kind == M17EventKind.StreamEnd);
        Check("AES-256 + signed through the engine: decrypted, signature valid, bad key reported",
              endA != null && endA.Link.Decrypt == M17Decrypt.Decrypted && endA.Signature == M17Signature.Valid
              && endA.SignatureKey == "N0CALL" && auA.Any(v => Math.Abs(v) > 0.05f) && keyNote.Contains("broken"),
              endA == null ? "no end" : $"{endA.Link.EncryptionState}, signature {endA.SignatureText}; note: {keyNote.Replace("\n", " / ")}");
        Check("CSV line with the signature", endA != null && CsvLog.Line(endA).EndsWith(",\"valid (N0CALL)\""),
              endA != null ? CsvLog.Line(endA) : "");
        var (evN, auN, _) = Run(Path.Combine(dir, "signed_aes.cf32"), 32000);
        var endN = evN.FirstOrDefault(e => e.Kind == M17EventKind.StreamEnd);
        Check("AES-256 without keys: muted, no key", endN != null && endN.Link.Decrypt == M17Decrypt.NoKey
              && endN.Signature == M17Signature.NoKey && auN.All(v => v == 0),
              endN == null ? "no end" : $"{endN.Link.EncryptionState}, signature {endN.SignatureText}");

        Console.WriteLine(fails == 0 ? "\nall passed" : $"\n{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
