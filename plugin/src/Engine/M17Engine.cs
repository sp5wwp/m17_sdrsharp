// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using SDRSharp.M17.Native;

namespace SDRSharp.M17.Engine
{
    /// <summary>
    /// Owns one libm17rx receiver. ProcessIq runs on SDR#'s DSP thread; status,
    /// events and the signal views are read from the UI thread. Every native call
    /// is made under one lock, as the library requires.
    /// </summary>
    public sealed unsafe class M17Engine : IDisposable
    {
        readonly object _lk = new object();
        IntPtr _rx;
        double _rate;
        readonly short[] _pcm = new short[8192];
        readonly ConcurrentQueue<M17Event> _events = new ConcurrentQueue<M17Event>();
        M17RxConfig _cfg;

        public readonly AudioOut Audio = new AudioOut();

        // diagnostics: the IQ rate actually delivered, against the rate SDR# reported
        readonly System.Diagnostics.Stopwatch _iqClock = new System.Diagnostics.Stopwatch();
        long _iqSamples;
        public double MeasuredIqRate { get; private set; }
        public int LastIqBlock { get; private set; }

        /// <summary>Set false to stop processing (the plugin's Enabled switch).</summary>
        public volatile bool Enabled = true;

        public M17Engine()
        {
            M17RxNative.CheckCompatible();
            _rx = M17RxNative.m17rx_create();
            if (_rx == IntPtr.Zero)
                throw new OutOfMemoryException("m17rx_create failed");
            fixed (M17RxConfig* c = &_cfg)
                M17RxNative.m17rx_default_config(c);
        }

        public static string NativeVersion => M17RxNative.Version;

        public double InputRate
        {
            get { lock (_lk) return _rate; }
        }

        public void SetInputRate(double fs)
        {
            lock (_lk)
            {
                if (_rx == IntPtr.Zero || Math.Abs(fs - _rate) < 1e-6)
                    return;
                if (M17RxNative.m17rx_set_input_rate(_rx, fs) == 0)
                    _rate = fs;
            }
        }

        public void Configure(bool invert, float syncThreshold, float viterbiMute, bool eyeFromDiscriminator)
        {
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return;
                _cfg.Invert = invert ? 1 : 0;
                _cfg.SyncThreshold = syncThreshold;
                _cfg.ViterbiMute = viterbiMute;
                _cfg.EyeSource = eyeFromDiscriminator ? 1 : 0;
                fixed (M17RxConfig* c = &_cfg)
                    M17RxNative.m17rx_set_config(_rx, c);
            }
        }

        /// <summary>
        /// Hands the keys to the decoder. Returns the names of public keys it rejected (not a point on
        /// P-256, or the keyring full). Empty AES key / zero bits turn that decryption off.
        /// </summary>
        public List<string> SetKeys(byte[] aesKey, uint scramblerSeed, int scramblerBits, IEnumerable<PublicKeyEntry> publicKeys)
        {
            var rejected = new List<string>();
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return rejected;
                fixed (byte* k = aesKey)
                    M17RxNative.m17rx_set_aes_key(_rx, k, aesKey.Length);
                M17RxNative.m17rx_set_scrambler_seed(_rx, scramblerSeed, scramblerBits);
                M17RxNative.m17rx_clear_public_keys(_rx);
                foreach (var e in publicKeys)
                {
                    var key = KeyText.PublicKey(e.Key);
                    var name = System.Text.Encoding.UTF8.GetBytes((e.Name ?? "").Trim() + "\0");
                    int r = -1;
                    if (key != null)
                        fixed (byte* kp = key)
                        fixed (byte* np = name)
                            r = M17RxNative.m17rx_add_public_key(_rx, np, kp, key.Length);
                    if (r != 0)
                        rejected.Add(e.Name ?? "");
                }
            }
            return rejected;
        }

        public void Reset()
        {
            lock (_lk)
            {
                if (_rx != IntPtr.Zero)
                    M17RxNative.m17rx_reset(_rx);
            }
            Audio.Clear();
        }

        /// <summary>Interleaved I/Q floats (SDR#'s Complex layout), n complex samples.</summary>
        public void ProcessIq(float* iq, int n)
        {
            if (!Enabled || n <= 0)
                return;
            MeasureIq(n);
            lock (_lk)
            {
                if (_rx == IntPtr.Zero || _rate <= 0)
                    return;
                M17RxNative.m17rx_process_iq(_rx, iq, n);
                Drain();
            }
        }

        /// <summary>FM discriminator output in Hz (for tests and alternative hosts).</summary>
        public void ProcessFm(float* hz, int n)
        {
            if (!Enabled || n <= 0)
                return;
            lock (_lk)
            {
                if (_rx == IntPtr.Zero || _rate <= 0)
                    return;
                M17RxNative.m17rx_process_fm(_rx, hz, n);
                Drain();
            }
        }

        void MeasureIq(int n)
        {
            if (!_iqClock.IsRunning)
                _iqClock.Start();
            _iqSamples += n;
            LastIqBlock = n;
            double s = _iqClock.Elapsed.TotalSeconds;
            if (s >= 2.0)
            {
                MeasuredIqRate = _iqSamples / s;
                _iqSamples = 0;
                _iqClock.Restart();
            }
        }

        // called with _lk held
        void Drain()
        {
            fixed (short* p = _pcm)
            {
                int k;
                while ((k = M17RxNative.m17rx_read_audio(_rx, p, _pcm.Length)) > 0)
                    Audio.Write(p, k);
            }
            M17RxEvent ev;
            while (M17RxNative.m17rx_next_event(_rx, &ev) != 0)
            {
                var e = M17Event.From(ev);
                if (e.Kind == M17EventKind.StreamEnd)
                    Audio.Drain();
                _events.Enqueue(e);
                while (_events.Count > 256 && _events.TryDequeue(out _)) { } // nobody reading
            }
        }

        public bool TryGetEvent(out M17Event e) => _events.TryDequeue(out e!);

        public StatusInfo GetStatus()
        {
            M17RxStatus st;
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return new StatusInfo();
                M17RxNative.m17rx_get_status(_rx, &st);
            }
            return StatusInfo.From(st);
        }

        /// <summary>Copies up to traces.Length / EyePoints eye traces (newest last); returns the count.</summary>
        public int GetEye(float[] traces)
        {
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return 0;
                fixed (float* p = traces)
                    return M17RxNative.m17rx_get_eye(_rx, p, traces.Length / M17RxNative.EyePoints);
            }
        }

        public void GetHistogram(float[] bins)
        {
            if (bins.Length < M17RxNative.HistBins)
                throw new ArgumentException("histogram buffer too small");
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return;
                fixed (float* p = bins)
                    M17RxNative.m17rx_get_histogram(_rx, p);
            }
        }

        public int GetEvmHistory(float[] values)
        {
            if (values.Length < M17RxNative.EvmHistory)
                throw new ArgumentException("history buffer too small");
            lock (_lk)
            {
                if (_rx == IntPtr.Zero)
                    return 0;
                fixed (float* p = values)
                    return M17RxNative.m17rx_get_evm_history(_rx, p);
            }
        }

        public void Dispose()
        {
            lock (_lk)
            {
                if (_rx != IntPtr.Zero)
                {
                    M17RxNative.m17rx_destroy(_rx);
                    _rx = IntPtr.Zero;
                }
            }
        }
    }
}
