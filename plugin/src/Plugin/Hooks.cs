// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System.Diagnostics;
using SDRSharp.M17.Engine;
using SDRSharp.Radio;

namespace SDRSharp.M17.Plugin
{
    /// <summary>Receives the VFO's decimated, filtered IQ (ProcessorType.DecimatedAndFilteredIQ).</summary>
    public sealed unsafe class IqHook : IIQProcessor
    {
        readonly M17Engine _engine;

        public IqHook(M17Engine engine)
        {
            _engine = engine;
        }

        public bool Enabled { get; set; } = true;

        public double SampleRate
        {
            set { _engine.SetInputRate(value); }
        }

        public void Process(Complex* buffer, int length)
        {
            // SDRSharp.Radio.Complex is { float Real; float Imag; }: interleaved I/Q as the core expects
            _engine.ProcessIq((float*)buffer, length);
        }
    }

    /// <summary>
    /// Replaces SDR#'s filtered audio (ProcessorType.FilteredAudioOutput) with decoded M17 voice
    /// while there is any, and optionally mutes the FM noise in between.
    /// </summary>
    public sealed unsafe class AudioHook : IRealProcessor
    {
        readonly M17Engine _engine;
        readonly Stopwatch _clock = new Stopwatch();
        double _rate = 48000;
        long _floats;
        int _channels = 2; // SDR# delivers interleaved stereo; confirmed by measurement below

        public AudioHook(M17Engine engine)
        {
            _engine = engine;
        }

        public bool Enabled { get; set; } = true;
        public volatile bool MuteWhenIdle = true;

        public int Channels => _channels;
        public double Rate => _rate;
        public double MeasuredRatio { get; private set; } // floats per second / rate: 1 mono, 2 stereo
        public int LastLength { get; private set; }

        public double SampleRate
        {
            set
            {
                if (value > 0)
                    _rate = value;
                _floats = 0;
                _clock.Restart();
            }
        }

        public void Process(float* buffer, int length)
        {
            LastLength = length;
            DetectChannels(length);
            if (!_engine.Enabled || length <= 0)
                return;

            int ch = _channels;
            int frames = length / ch;
            if (_engine.Audio.Read(buffer, frames, ch, _rate))
                return;

            if (MuteWhenIdle)
                for (int i = 0; i < length; i++)
                    buffer[i] = 0;
        }

        // floats per second divided by the audio rate is the channel count
        void DetectChannels(int length)
        {
            if (!_clock.IsRunning)
                _clock.Start();
            _floats += length;
            double s = _clock.Elapsed.TotalSeconds;
            if (s >= 2.0)
            {
                double ratio = _floats / s / _rate;
                MeasuredRatio = ratio;
                if (ratio > 0.7 && ratio < 1.4)
                    _channels = 1;
                else if (ratio > 1.6 && ratio < 2.6)
                    _channels = 2;
                _floats = 0;
                _clock.Restart();
            }
        }
    }
}
