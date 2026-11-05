// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SDRSharp.M17.Native
{
    /// <summary>P/Invoke bindings for libm17rx (see core/include/m17rx.h).</summary>
    public static unsafe class M17RxNative
    {
        public const string Lib = "m17rx";
        public const int ApiVersion = 2;
        public const int EyePoints = 21;
        public const int HistBins = 64;
        public const int EvmHistory = 64;

        /// <summary>Where the native library was loaded from (for the About text and error messages).</summary>
        public static string LoadedFrom { get; private set; } = "";

#if NETCOREAPP
        static M17RxNative()
        {
            NativeLibrary.SetDllImportResolver(typeof(M17RxNative).Assembly, Resolve);
        }


        static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
        {
            if (name != Lib)
                return IntPtr.Zero;

            // Explicit override, used by the Linux tests
            string env = Environment.GetEnvironmentVariable("M17RX_LIBRARY");
            if (!string.IsNullOrEmpty(env) && NativeLibrary.TryLoad(env, out IntPtr h0))
            {
                LoadedFrom = env;
                return h0;
            }

            string dir = Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory;
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            string file = OperatingSystem.IsWindows() ? "m17rx.dll" : "libm17rx.so";
            foreach (string candidate in new[] { Path.Combine(dir, arch, file), Path.Combine(dir, file) })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr h))
                {
                    LoadedFrom = candidate;
                    return h;
                }
            }
            return IntPtr.Zero; // fall back to the default search
        }
#endif

        /// <summary>Throws if the native library is missing or its structs differ from ours.</summary>
        public static void CheckCompatible()
        {
            int api = m17rx_api_version();
            if (api != ApiVersion)
                throw new InvalidOperationException($"libm17rx API version {api}, expected {ApiVersion}");
            Check(0, sizeof(M17RxLink), "link");
            Check(1, sizeof(M17RxStatus), "status");
            Check(2, sizeof(M17RxEvent), "event");
            Check(3, sizeof(M17RxConfig), "config");
        }

        static void Check(int which, int managed, string name)
        {
            int native = m17rx_struct_size(which);
            if (native != managed)
                throw new InvalidOperationException($"libm17rx {name} struct is {native} bytes, the plugin expects {managed}");
        }

        /// <summary>NUL-terminated UTF-8 from a fixed buffer.</summary>
        public static string Utf8(byte* p, int max)
        {
            int n = 0;
            while (n < max && p[n] != 0)
                n++;
            return Encoding.UTF8.GetString(p, n);
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_api_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr m17rx_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_struct_size(int which);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr m17rx_create();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_destroy(IntPtr rx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_reset(IntPtr rx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_default_config(M17RxConfig* cfg);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_set_config(IntPtr rx, M17RxConfig* cfg);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_set_input_rate(IntPtr rx, double fs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_process_iq(IntPtr rx, float* iq, int n);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_process_fm(IntPtr rx, float* hz, int n);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_read_audio(IntPtr rx, short* pcm, int max);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_next_event(IntPtr rx, M17RxEvent* ev);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_get_status(IntPtr rx, M17RxStatus* st);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_get_eye(IntPtr rx, float* out_, int maxTraces);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_get_histogram(IntPtr rx, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_get_evm_history(IntPtr rx, float* out_);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_set_aes_key(IntPtr rx, byte* key, int len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_set_scrambler_seed(IntPtr rx, uint seed, int bits);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void m17rx_clear_public_keys(IntPtr rx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int m17rx_add_public_key(IntPtr rx, byte* nameUtf8, byte* key, int len);

        public static string Version => Marshal.PtrToStringAnsi(m17rx_version()) ?? "?";
    }
}
