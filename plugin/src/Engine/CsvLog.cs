// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SDRSharp.M17.Engine
{
    /// <summary>Appends finished transmissions and packets to a CSV file.</summary>
    public static class CsvLog
    {
        public const string Header = "time,kind,source,destination,can,type,encryption,signed,duration_s,frames,lost,evm_pct,info,signature";

        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        public static string Line(M17Event e)
        {
            var l = e.Link;
            string kind = e.Kind == M17EventKind.Packet ? "packet" : e.Kind == M17EventKind.PacketBad ? "packet_bad" : "stream";
            string info = e.Kind == M17EventKind.Packet ? (e.Protocol == 5 ? e.Sms : e.ProtocolText + " " + e.Data.Length + " bytes")
                                                        : l.MetaSummary;
            var c = CultureInfo.InvariantCulture;
            return string.Join(",",
                e.Time.ToString("yyyy-MM-dd HH:mm:ss", c), kind, Q(l.Source), Q(l.Destination), l.Can.ToString(c),
                Q(l.DataTypeText), Q(l.EncryptionState), l.Signed ? "1" : "0",
                e.Duration.ToString("F2", c), e.Frames.ToString(c), e.FramesLost.ToString(c), e.Evm.ToString("F2", c), Q(info), Q(e.SignatureText));
        }

        public static void Append(string path, M17Event e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                bool fresh = !File.Exists(path);
                using var w = new StreamWriter(path, true, new UTF8Encoding(false));
                if (fresh)
                    w.WriteLine(Header);
                w.WriteLine(Line(e));
            }
            catch
            {
                // logging must not interrupt reception
            }
        }
    }
}
