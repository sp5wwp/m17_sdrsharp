// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SDRSharp.M17.Engine
{
    /// <summary>Plugin settings, stored as JSON under %APPDATA%\SDRSharp\M17Decoder.</summary>
    public sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public bool Invert { get; set; } = false;
        public float SyncThreshold { get; set; } = 0.90f;
        public float ViterbiMute { get; set; } = 0.08f;
        public bool MuteWhenIdle { get; set; } = true;
        public int EyePersistence { get; set; } = 1;     // 0: 1 frame, 1: 10 frames, 2: 40 frames
        public bool LogToCsv { get; set; } = false;
        public string LogPath { get; set; } = "";
        public int SelectedTab { get; set; } = 0;

        // keys: AES key and scrambler seed are DPAPI-protected (see Secret); public keys in the clear
        public string AesKeyStored { get; set; } = "";
        public string ScramblerSeedStored { get; set; } = "";
        public List<PublicKeyEntry> PublicKeys { get; set; } = new List<PublicKeyEntry>();

        [System.Text.Json.Serialization.JsonIgnore]
        public string AesKey { get => Secret.Unprotect(AesKeyStored); set => AesKeyStored = Secret.Protect(value); }

        [System.Text.Json.Serialization.JsonIgnore]
        public string ScramblerSeed { get => Secret.Unprotect(ScramblerSeedStored); set => ScramblerSeedStored = Secret.Protect(value); }

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDRSharp", "M17Decoder");

        static string FilePath => Path.Combine(Folder, "settings.json");

        public string EffectiveLogPath => string.IsNullOrWhiteSpace(LogPath) ? Path.Combine(Folder, "m17-log.csv") : LogPath;

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch
            {
                // a damaged file falls back to defaults
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // settings are a convenience; never let saving break the receiver
            }
        }
    }
}
