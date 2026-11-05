// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SDRSharp.M17.Engine
{
    public enum M17Decrypt { Clear = 0, Decrypted = 1, NoKey = 2, Unsupported = 3 }
    public enum M17Signature { None = 0, Valid = 1, Invalid = 2, NoKey = 3, Incomplete = 4, Missing = 5 }

    /// <summary>A named public key for signature checks (stored in the clear: public keys are public).</summary>
    public sealed class PublicKeyEntry
    {
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";   // as the user entered it: hex or PEM
    }

    /// <summary>Parsing of the key formats the Settings tab accepts.</summary>
    public static class KeyText
    {
        /// <summary>Hex digits, ignoring spaces, colons, dashes and a leading 0x. Null if not hex.</summary>
        public static byte[] Hex(string s)
        {
            if (s == null)
                return null;
            var sb = new StringBuilder();
            string t = s.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(2);
            foreach (char c in t)
            {
                if (Uri.IsHexDigit(c))
                    sb.Append(c);
                else if (c != ' ' && c != ':' && c != '-' && c != '\r' && c != '\n' && c != '\t')
                    return null;
            }
            if (sb.Length == 0 || sb.Length % 2 != 0)
                return null;
            var b = new byte[sb.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(sb.ToString(2 * i, 2), 16);
            return b;
        }

        /// <summary>AES key: 16, 24 or 32 bytes of hex. Empty text means no key.</summary>
        public static bool TryAes(string s, out byte[] key, out string error)
        {
            key = Array.Empty<byte>();
            error = "";
            if (string.IsNullOrWhiteSpace(s))
                return true;
            var b = Hex(s);
            if (b == null || (b.Length != 16 && b.Length != 24 && b.Length != 32))
            {
                error = "AES key: 32, 48 or 64 hex digits (128, 192 or 256 bits)";
                return false;
            }
            key = b;
            return true;
        }

        /// <summary>Scrambler seed: 2, 4 or 6 hex digits (8, 16 or 24-bit LFSR). Empty text means none.</summary>
        public static bool TrySeed(string s, out uint seed, out int bits, out string error)
        {
            seed = 0;
            bits = 0;
            error = "";
            if (string.IsNullOrWhiteSpace(s))
                return true;
            var b = Hex(s);
            if (b == null || b.Length < 1 || b.Length > 3)
            {
                error = "Scrambler seed: 2, 4 or 6 hex digits (8, 16 or 24-bit)";
                return false;
            }
            foreach (byte x in b)
                seed = (seed << 8) | x;
            bits = 8 * b.Length;
            if (seed == 0)
            {
                error = "Scrambler seed: all zeros would not scramble anything";
                return false;
            }
            return true;
        }

        /// <summary>
        /// secp256r1 public key in any common form: hex of 64 bytes (X||Y, as gr-m17 prints it),
        /// 65 bytes (04||X||Y) or 33 bytes (compressed); or PEM / base64 of a SubjectPublicKeyInfo.
        /// Returns the bytes to hand to the decoder, which checks the point itself.
        /// </summary>
        public static byte[] PublicKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;
            var h = Hex(s);
            if (h != null && (h.Length == 64 || h.Length == 65 || h.Length == 33))
                return h;
            // PEM or bare base64 DER
            var b64 = new StringBuilder();
            foreach (var line in s.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("-----"))
                    continue;
                b64.Append(t);
            }
            try
            {
                var der = Convert.FromBase64String(b64.ToString());
                // P-256 SubjectPublicKeyInfo: 26 bytes of algorithm header, then 04||X||Y (or 02/03||X)
                if (der.Length == 91 && der[26] == 0x04)
                    return der.AsSpan(26, 65).ToArray();
                if (der.Length == 59 && (der[26] == 0x02 || der[26] == 0x03))
                    return der.AsSpan(26, 33).ToArray();
            }
            catch (FormatException)
            {
            }
            return null;
        }
    }

    /// <summary>
    /// The AES key and scrambler seed are kept with Windows DPAPI (current user), so settings.json
    /// does not hold them in the clear. On other systems (tests) they are stored as-is.
    /// </summary>
    public static class Secret
    {
        const string Tag = "dpapi:";

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            var data = Encoding.UTF8.GetBytes(plain);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "plain:" + Convert.ToBase64String(data);
            var r = Dpapi(data, true);
            return r == null ? "" : Tag + Convert.ToBase64String(r);
        }

        public static string Unprotect(string stored)
        {
            try
            {
                if (string.IsNullOrEmpty(stored))
                    return "";
                if (stored.StartsWith("plain:"))
                    return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(6)));
                if (stored.StartsWith(Tag) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var r = Dpapi(Convert.FromBase64String(stored.Substring(Tag.Length)), false);
                    return r == null ? "" : Encoding.UTF8.GetString(r);
                }
            }
            catch (FormatException)
            {
            }
            return ""; // another user's or machine's blob: start without the key
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob input, string desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        static byte[] Dpapi(byte[] data, bool protect)
        {
            const int UiForbidden = 1;
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
                bool ok = protect
                    ? CryptProtectData(ref input, "M17 Decoder key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok)
                    return null;
                try
                {
                    var r = new byte[output.Size];
                    Marshal.Copy(output.Data, r, 0, output.Size);
                    return r;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                h.Free();
            }
        }
    }
}

namespace SDRSharp.M17.Engine
{
    /// <summary>Applies the stored keys to an engine and describes the result in one or two lines.</summary>
    public static class KeyConfig
    {
        public static string Apply(M17Engine eng, Settings s)
        {
            var notes = new System.Collections.Generic.List<string>();
            if (!KeyText.TryAes(s.AesKey, out var aes, out var e1))
            {
                notes.Add(e1);
                aes = Array.Empty<byte>();
            }
            if (!KeyText.TrySeed(s.ScramblerSeed, out var seed, out var bits, out var e2))
            {
                notes.Add(e2);
                seed = 0;
                bits = 0;
            }
            var rejected = eng?.SetKeys(aes, seed, bits, s.PublicKeys) ?? new System.Collections.Generic.List<string>();
            if (rejected.Count > 0)
                notes.Add("Not a valid P-256 public key: " + string.Join(", ", rejected));
            if (notes.Count > 0)
                return string.Join("\n", notes);
            int pubs = s.PublicKeys.Count;
            return (aes.Length > 0 ? "AES-" + (8 * aes.Length) + " key set" : "No AES key") + " · " +
                   (bits > 0 ? bits + "-bit seed set" : "no scrambler seed") + "\n" +
                   (pubs == 0 ? "No public keys" : pubs == 1 ? "1 public key" : pubs + " public keys");
        }
    }
}
