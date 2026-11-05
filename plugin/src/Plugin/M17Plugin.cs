// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.M17.Engine;
using SDRSharp.M17.UI;
using SDRSharp.Radio;

namespace SDRSharp.M17.Plugin
{
    /// <summary>M17 decoder for SDR#: voice, link information, packets and signal measurements.</summary>
    public sealed class M17Plugin : ISharpPlugin, ICanLazyLoadGui, ISupportStatus, IExtendedNameProvider
    {
        ISharpControl _control;
        M17Engine _engine;
        IqHook _iq;
        AudioHook _audio;
        Settings _settings;
        M17Panel _gui;
        string _loadError;

        public string DisplayName => "M17 Decoder";
        public string Category => "Digital Voice";
        public string MenuItemName => DisplayName;
        public bool IsActive => _gui != null && _gui.Visible;

        public UserControl Gui
        {
            get
            {
                LoadGui();
                return _gui;
            }
        }

        public void Initialize(ISharpControl control)
        {
            _control = control;
            _settings = Settings.Load();
            try
            {
                _engine = new M17Engine();
                _engine.Enabled = _settings.Enabled;
                _engine.Configure(_settings.Invert, _settings.SyncThreshold, _settings.ViterbiMute, false);
                KeyConfig.Apply(_engine, _settings);

                _iq = new IqHook(_engine);
                _audio = new AudioHook(_engine) { MuteWhenIdle = _settings.MuteWhenIdle };
                _control.RegisterStreamHook(_iq, ProcessorType.DecimatedAndFilteredIQ);
                _control.RegisterStreamHook(_audio, ProcessorType.FilteredAudioOutput);
            }
            catch (Exception ex)
            {
                // Missing or mismatched native library: show the reason in the panel instead of failing SDR#.
                _loadError = ex.GetType().Name + ": " + ex.Message;
                _engine?.Dispose();
                _engine = null;
            }
        }

        public void LoadGui()
        {
            if (_gui == null)
                _gui = new M17Panel(_control, _engine, _audio, _settings, _loadError);
        }

        public void Close()
        {
            try
            {
                if (_iq != null)
                    _control.UnregisterStreamHook(_iq);
                if (_audio != null)
                    _control.UnregisterStreamHook(_audio);
            }
            catch
            {
                // SDR# is shutting down anyway
            }
            _gui?.Shutdown();
            _settings?.Save();
            _engine?.Dispose();
            _engine = null;
        }
    }
}
