// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;

namespace SDRSharp.M17.Engine
{
    /// <summary>
    /// Decoded voice from the DSP thread (8 kHz, in 40 ms bursts) to SDR#'s audio
    /// thread (any rate, mono or interleaved stereo). A small jitter buffer absorbs
    /// the bursts; latency is capped so it cannot grow if the clocks differ.
    /// </summary>
    public sealed unsafe class AudioOut
    {
        const int Cap = 8000 * 4;          // 4 s
        const int Prebuffer = 8000 * 15 / 100; // 150 ms before playback starts
        const int MaxLatency = 8000 * 6 / 10;  // trim back to Prebuffer above 600 ms

        readonly object _lk = new object();
        readonly short[] _fifo = new short[Cap];
        int _head, _count;
        bool _playing, _draining;
        double _phase;   // fractional position between _s0 and _s1
        float _s0, _s1;
        long _lastWriteTicks;

        public float Gain = 1.0f;

        // diagnostics
        public int Underruns;   // playback ran dry in the middle of a transmission
        public int Trims;       // latency grew past the cap and was cut back
        public int BufferedMs { get { lock (_lk) return _count / 8; } }

        /// <summary>True while there is decoded audio queued or playing.</summary>
        public bool Active
        {
            get { lock (_lk) return _playing || _count > 0; }
        }

        public void Write(short* pcm, int n)
        {
            lock (_lk)
            {
                for (int i = 0; i < n; i++)
                {
                    if (_count == Cap)
                    {
                        _head = (_head + 1) % Cap;
                        _count--;
                    }
                    _fifo[(_head + _count) % Cap] = pcm[i];
                    _count++;
                }
                _lastWriteTicks = Util.Milliseconds;
            }
        }

        /// <summary>The transmission ended: play out what is left, even if below the prebuffer.</summary>
        public void Drain()
        {
            lock (_lk)
                _draining = true;
        }

        public void Clear()
        {
            lock (_lk)
            {
                _head = _count = 0;
                _playing = _draining = false;
                _phase = 0;
                _s0 = _s1 = 0;
            }
        }

        short Pop()
        {
            short v = _fifo[_head];
            _head = (_head + 1) % Cap;
            _count--;
            return v;
        }

        /// <summary>
        /// Fill frames output frames (each of channels interleaved floats) at outRate.
        /// Returns false (buffer untouched) when there is nothing to play.
        /// </summary>
        public bool Read(float* dst, int frames, int channels, double outRate)
        {
            lock (_lk)
            {
                if (!_playing)
                {
                    // input stalled without an end event: play out what we have
                    if (_count > 0 && Util.Milliseconds - _lastWriteTicks > 300)
                        _draining = true;
                    if (_count >= Prebuffer || (_draining && _count > 0))
                    {
                        _playing = true;
                        _phase = 0;
                        _s0 = 0;
                        _s1 = Pop() / 32768.0f;
                    }
                    else
                    {
                        return false;
                    }
                }

                if (_count > MaxLatency) // drop the excess, keeping continuity at the cut
                {
                    Trims++;
                    int drop = _count - Prebuffer;
                    _head = (_head + drop) % Cap;
                    _count -= drop;
                }

                double step = 8000.0 / outRate;
                float g = Gain;
                for (int f = 0; f < frames; f++)
                {
                    float v = (float)(_s0 + (_s1 - _s0) * _phase) * g;
                    for (int c = 0; c < channels; c++)
                        dst[f * channels + c] = v;
                    _phase += step;
                    while (_phase >= 1.0)
                    {
                        _phase -= 1.0;
                        _s0 = _s1;
                        if (_count > 0)
                        {
                            _s1 = Pop() / 32768.0f;
                        }
                        else
                        {
                            _s1 = 0;
                            if (_s0 == 0) // ran dry: stop and rebuffer (fill the rest with silence)
                            {
                                if (!_draining)
                                    Underruns++;
                                for (int r = f + 1; r < frames; r++)
                                    for (int c = 0; c < channels; c++)
                                        dst[r * channels + c] = 0;
                                _playing = false;
                                _draining = false;
                                return true;
                            }
                        }
                    }
                }
                return true;
            }
        }
    }
}
