// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Globalization;
using SDRSharp.M17.Native;

namespace SDRSharp.M17.Engine
{
    public enum M17DataType { Reserved = 0, Data = 1, Voice = 2, VoiceData = 3 }
    public enum M17Encryption { None = 0, Scrambler = 1, Aes = 2, Reserved = 3 }
    public enum M17MetaKind { None = 0, Text = 1, Gnss = 2, ExtendedCallsign = 3, Raw = 4 }
    public enum M17EventKind { None = 0, StreamStart = 1, LinkUpdate = 2, StreamEnd = 3, Packet = 4, PacketBad = 5 }
    public enum M17EndReason { None = 0, EndOfStream = 1, EndOfTransmission = 2, SignalLost = 3 }

    /// <summary>Link information of one transmission (managed copy of m17rx_link_t).</summary>
    public sealed class LinkInfo
    {
        public bool Valid, FromLich, Stream, Signed, TextComplete;
        public string Source = "", Destination = "", MetaText = "", Ecd1 = "", Ecd2 = "";
        public ushort Type;
        public M17DataType DataType;
        public M17Encryption Encryption;
        public int EncSubtype, Can;
        public M17MetaKind MetaKind;
        public byte[] Meta = new byte[14];
        public bool GnssPosValid, GnssAltValid, GnssSpeedValid;
        public int GnssStation, GnssSource, GnssBearing;
        public double Lat, Lon;
        public float AltM, SpeedKmh;
        public M17Decrypt Decrypt;

        public static unsafe LinkInfo From(in M17RxLink l)
        {
            var r = new LinkInfo();
            fixed (M17RxLink* p = &l)
            {
                r.Valid = p->Valid != 0;
                r.FromLich = p->FromLich != 0;
                r.Source = M17RxNative.Utf8(p->Src, 12);
                r.Destination = M17RxNative.Utf8(p->Dst, 12);
                r.Type = p->Type;
                r.Stream = p->Stream != 0;
                r.DataType = (M17DataType)p->DataType;
                r.Encryption = (M17Encryption)p->EncType;
                r.EncSubtype = p->EncSubtype;
                r.Can = p->Can;
                r.Signed = p->SignedStream != 0;
                for (int i = 0; i < 14; i++)
                    r.Meta[i] = p->Meta[i];
                r.MetaKind = (M17MetaKind)p->MetaKind;
                r.MetaText = M17RxNative.Utf8(p->MetaText, 64);
                r.TextComplete = p->MetaTextComplete != 0;
                r.Ecd1 = M17RxNative.Utf8(p->EcdCf1, 12);
                r.Ecd2 = M17RxNative.Utf8(p->EcdCf2, 12);
                r.GnssPosValid = p->GnssValidPos != 0;
                r.GnssAltValid = p->GnssValidAlt != 0;
                r.GnssSpeedValid = p->GnssValidSpeed != 0;
                r.GnssStation = p->GnssStation;
                r.GnssSource = p->GnssSource;
                r.Lat = p->GnssLat;
                r.Lon = p->GnssLon;
                r.AltM = p->GnssAltM;
                r.SpeedKmh = p->GnssSpeedKmh;
                r.GnssBearing = p->GnssBearingDeg;
                r.Decrypt = (M17Decrypt)p->Decrypt;
            }
            return r;
        }

        /// <summary>"VOICE 3200", "DATA", ...</summary>
        public string DataTypeText => !Stream ? "PACKET" : DataType switch
        {
            M17DataType.Voice => "VOICE 3200",
            M17DataType.VoiceData => "VOICE 1600 + DATA",
            M17DataType.Data => "DATA",
            _ => "RESERVED",
        };

        /// <summary>"CLEAR", "AES-256", "SCRAMBLER 24", ...</summary>
        public string EncryptionText => Encryption switch
        {
            M17Encryption.None => "CLEAR",
            M17Encryption.Aes => EncSubtype switch { 0 => "AES-128", 1 => "AES-192", 2 => "AES-256", _ => "AES ?" },
            M17Encryption.Scrambler => EncSubtype switch { 0 => "SCRAMBLER 8", 1 => "SCRAMBLER 16", 2 => "SCRAMBLER 24", _ => "SCRAMBLER ?" },
            _ => "ENCRYPTED (RESERVED)",
        };

        /// <summary>"clear", "AES-256, decrypted", "scrambler 16-bit, no seed", ...</summary>
        public string EncryptionState
        {
            get
            {
                string what = Encryption switch
                {
                    M17Encryption.None => "clear",
                    M17Encryption.Aes => EncSubtype <= 2 ? "AES-" + (128 + 64 * EncSubtype) : "AES (reserved size)",
                    M17Encryption.Scrambler => EncSubtype <= 2 ? "scrambler " + (8 * (EncSubtype + 1)) + "-bit" : "scrambler (reserved size)",
                    _ => "encrypted (reserved type)",
                };
                return Encryption == M17Encryption.None ? what : what + Decrypt switch
                {
                    M17Decrypt.Decrypted => ", decrypted",
                    M17Decrypt.NoKey => Encryption == M17Encryption.Aes ? ", no key" : ", no seed",
                    _ => ", unsupported",
                };
            }
        }

        static readonly string[] Stations = { "fixed", "mobile", "handheld" };
        public string StationText => GnssStation >= 0 && GnssStation < Stations.Length ? Stations[GnssStation] : "other";

        public string PositionText => GnssPosValid
            ? string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", Lat, Lon)
            : "not valid";

        /// <summary>One-line META summary for logs.</summary>
        public string MetaSummary => MetaKind switch
        {
            M17MetaKind.Text => MetaText,
            M17MetaKind.Gnss => "GNSS " + PositionText + (GnssAltValid ? string.Format(CultureInfo.InvariantCulture, ", {0:F0} m", AltM) : ""),
            M17MetaKind.ExtendedCallsign => "via " + Ecd1 + (Ecd2.Length > 0 ? " / " + Ecd2 : ""),
            M17MetaKind.Raw => "META " + BitConverter.ToString(Meta).Replace("-", ""),
            _ => "",
        };
    }

    public sealed class M17Event
    {
        public DateTime Time = DateTime.Now;
        public M17EventKind Kind;
        public M17EndReason EndReason;
        public int Frames, FramesLost, Protocol;
        public float Duration, Evm;
        public LinkInfo Link = new LinkInfo();
        public M17Signature Signature;
        public string SignatureKey = "";
        public byte[] Data = Array.Empty<byte>();
        public string Sms = "";

        public static unsafe M17Event From(in M17RxEvent e)
        {
            var r = new M17Event();
            fixed (M17RxEvent* p = &e)
            {
                r.Kind = (M17EventKind)p->Kind;
                r.EndReason = (M17EndReason)p->EndReason;
                r.Frames = p->Frames;
                r.FramesLost = p->FramesLost;
                r.Duration = p->DurationS;
                r.Evm = p->EvmRmsPct;
                r.Link = LinkInfo.From(p->Link);
                r.Signature = (M17Signature)p->SigState;
                r.SignatureKey = M17RxNative.Utf8(p->SigKey, 32);
                r.Protocol = p->Protocol;
                int n = Util.Clamp(p->Length, 0, 33 * 25);
                r.Data = new byte[n];
                for (int i = 0; i < n; i++)
                    r.Data[i] = p->Data[i];
                r.Sms = M17RxNative.Utf8(p->Sms, 33 * 25 + 1);
            }
            return r;
        }

        /// <summary>"", "valid (SP5WWP)", "INVALID", ...</summary>
        public string SignatureText => Signature switch
        {
            M17Signature.Valid => "valid (" + SignatureKey + ")",
            M17Signature.Invalid => "INVALID",
            M17Signature.NoKey => "not checked, no key for " + Link.Source,
            M17Signature.Incomplete => "not checked, frames missed",
            M17Signature.Missing => "not received",
            _ => "",
        };

        public string ProtocolText => Protocol switch
        {
            0x00 => "raw",
            0x01 => "AX.25",
            0x02 => "APRS",
            0x03 => "6LoWPAN",
            0x04 => "IPv4",
            0x05 => "SMS",
            0x06 => "Winlink",
            _ => "0x" + Protocol.ToString("X2"),
        };
    }

    /// <summary>Snapshot of the receiver state and measurements (managed copy of m17rx_status_t).</summary>
    public sealed class StatusInfo
    {
        public bool Locked, MeasValid, InvertedHint, DataAided;
        public int State, LastFn, Frames, FramesLost, LichChunks;
        public float Duration, EvmRms, EvmPeak, Mer, BerPreFec, ViterbiBer, EyeOpening, Deviation, InnerOuter,
                     FreqOffset, ClockPpm, TimingFrac, SyncQuality, InputRate;
        public float[] LevelMean = new float[4], LevelSigma = new float[4];
        public LinkInfo Link = new LinkInfo();

        public static unsafe StatusInfo From(in M17RxStatus s)
        {
            var r = new StatusInfo();
            fixed (M17RxStatus* p = &s)
            {
                r.State = p->State;
                r.Locked = p->Locked != 0;
                r.LastFn = p->LastFn;
                r.Frames = p->Frames;
                r.FramesLost = p->FramesLost;
                r.Duration = p->DurationS;
                r.LichChunks = p->LichChunks;
                r.MeasValid = p->MeasValid != 0;
                r.EvmRms = p->EvmRmsPct;
                r.EvmPeak = p->EvmPeakPct;
                r.Mer = p->MerDb;
                r.BerPreFec = p->BerPreFec;
                r.ViterbiBer = p->ViterbiBer;
                r.EyeOpening = p->EyeOpeningPct;
                r.Deviation = p->DeviationHz;
                r.InnerOuter = p->InnerOuterRatio;
                r.FreqOffset = p->FreqOffsetHz;
                r.ClockPpm = p->ClockPpm;
                r.TimingFrac = p->TimingFracSym;
                for (int i = 0; i < 4; i++)
                {
                    r.LevelMean[i] = p->LevelMean[i];
                    r.LevelSigma[i] = p->LevelSigma[i];
                }
                r.SyncQuality = p->SyncQuality;
                r.DataAided = p->EvmReference == 0;
                r.InvertedHint = p->PolarityHint != 0;
                r.InputRate = p->InputRate;
                r.Link = LinkInfo.From(p->Link);
            }
            return r;
        }
    }
}
