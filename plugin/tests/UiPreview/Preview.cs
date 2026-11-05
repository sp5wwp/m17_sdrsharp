// Renders the real plugin panel under Mono WinForms with a fake SDR# host, fed by a test recording
// in real time, and saves each tab as PNG. Preview only: Mono draws controls differently from .NET on Windows.
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.M17.Engine;
using SDRSharp.M17.Plugin;
using SDRSharp.M17.UI;
using SDRSharp.Radio;

class FakeControl : ISharpControl
{
    public DetectorType DetectorType { get; set; } = DetectorType.NFM;
    public int FilterBandwidth { get; set; } = 12500;
    public bool SquelchEnabled { get; set; }
    public bool Dark = true;
    public bool ThemeIsDark => Dark;
    public Color ThemeForeColor => Dark ? Color.FromArgb(230, 232, 235) : Color.FromArgb(20, 20, 20);
    public Color ThemeBackColor => Dark ? Color.FromArgb(37, 37, 38) : Color.FromArgb(240, 240, 240);
    public Color ThemePanelColor => Dark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(225, 225, 228);
    public Color ThemeEditorColor => Dark ? Color.FromArgb(30, 30, 30) : Color.White;
    public double AudioSampleRate => 48000;
    public event PropertyChangedEventHandler PropertyChanged;
    public void SwitchTheme() { Dark = !Dark; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ThemeName")); }
    public void RegisterStreamHook(object h, ProcessorType t) { }
    public void UnregisterStreamHook(object h) { }
}

static unsafe class Preview
{
    [STAThread]
    static void Main(string[] a)
    {
        string iqPath = a[0], outDir = a[1], prefix = a.Length > 2 ? a[2] : "";
        double rate = 32000, stopAt = a.Length > 3 ? double.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 1e9;
        var ctl = new FakeControl { Dark = !prefix.Contains("light") };
        var settings = new Settings { LogToCsv = false };
        if (Environment.GetEnvironmentVariable("PREVIEW_KEYS") == "1")
        {
            settings.AesKey = "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4";
            settings.PublicKeys.Add(new PublicKeyEntry { Name = "N0CALL", Key = "0217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794" });
            settings.PublicKeys.Add(new PublicKeyEntry { Name = "SP5WWP", Key = "d65a93977caa3d1b081852ff57a79e465f1660577304baead505dd3a48589cf350185e895372df6221ea3a137557e473fddb6755f05bd507c3c533fce9c91285" });
        }
        var eng = new M17Engine();
        eng.SetInputRate(rate);
        var audio = new AudioHook(eng);
        var panel = new M17Panel(ctl, eng, audio, settings, null);
        var form = new Form { Width = int.Parse(Environment.GetEnvironmentVariable("PREVIEW_W") ?? "230"), Height = int.Parse(Environment.GetEnvironmentVariable("PREVIEW_H") ?? "700"), Text = "M17 Decoder preview", FormBorderStyle = FormBorderStyle.FixedSingle };
        form.Controls.Add(panel);

        // feed the recording in real time on a DSP-like thread, pulling audio like SDR# would
        float[] iq = new float[new FileInfo(iqPath).Length / 4];
        Buffer.BlockCopy(File.ReadAllBytes(iqPath), 0, iq, 0, iq.Length * 4);
        var dsp = new Thread(() =>
        {
            int blk = (int)(rate / 50); // 20 ms
            float[] outbuf = new float[960 * 2];
            for (int i = 0; i + blk <= iq.Length / 2 && i / rate < stopAt; i += blk)
            {
                fixed (float* p = &iq[2 * i]) eng.ProcessIq(p, blk);
                fixed (float* o = outbuf) audio.Process(o, outbuf.Length);
                Thread.Sleep(20);
            }
        }) { IsBackground = true };

        var show = typeof(M17Panel).GetMethod("ShowTab", BindingFlags.NonPublic | BindingFlags.Instance);
        void Snap(string name)
        {
            using var bmp = new Bitmap(panel.Width, panel.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(panel.PointToScreen(Point.Empty), Point.Empty, panel.Size); // Mono's DrawToBitmap skips children
            bmp.Save(Path.Combine(outDir, prefix + name + ".png"), ImageFormat.Png);
        }
        // positions of every control on the Live tab, relative to the panel
        string Layout()
        {
            var sb = new System.Text.StringBuilder();
            void Walk(Control c)
            {
                foreach (Control k in c.Controls)
                {
                    var r = panel.RectangleToClient(k.RectangleToScreen(k.ClientRectangle));
                    sb.Append($"{k.GetType().Name}:{r.X},{r.Y},{r.Width},{r.Height};");
                    Walk(k);
                }
            }
            Walk(panel);
            return sb.ToString();
        }
        string idle = null;
        Form dlg = null;
        void Diff(string when, string now, string before)
        {
            var a = before.Split(';'); var b = now.Split(';');
            int n = 0;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) { if (n++ < 12) Console.WriteLine($"  {when}: {a[i]}  ->  {b[i]}"); }
            Console.WriteLine($"{when}: {n} control(s) moved or resized" + (a.Length != b.Length ? $", count {a.Length} -> {b.Length}" : ""));
        }
        var steps = new (double t, Action act)[]
        {
            (0.3, () => { show.Invoke(panel, new object[] { 0 }); }),
            (0.4, () => { idle = Layout(); Snap("live_idle"); }),
            (0.1, () => dsp.Start()),
            (double.Parse(a.Length > 4 ? a[4] : "3.0", System.Globalization.CultureInfo.InvariantCulture), () => { show.Invoke(panel, new object[] { 0 }); }),
            (0.6, () => { Snap("live"); Diff("during RX", Layout(), idle); }),
            (0.1, () => show.Invoke(panel, new object[] { 2 })),
            (1.2, () => Snap("signal")),
            (0.1, () => show.Invoke(panel, new object[] { 3 })),
            (0.4, () => Snap("settings")),
            (0.1, () => { var pages = (System.Collections.IList)typeof(M17Panel).GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                          ((ScrollableControl)pages[3]).AutoScrollPosition = new Point(0, 250); }),
            (0.4, () => Snap("settings_keys")),
            (0.1, () => { var th = (Theme)typeof(M17Panel).GetField("T", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                          var f = new PublicKeyForm(th); f.StartPosition = FormStartPosition.Manual; f.Location = new Point(600, 100); f.Show();
                          f.Controls[0].Controls[1].Text = "SP5WWP"; f.Controls[0].Controls[3].Text = "0217e617f0b644";
                          dlg = f; }),
            (0.5, () => { using var bmp = new Bitmap(dlg.Width, dlg.Height); using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(dlg.Location, Point.Empty, dlg.Size);
                          bmp.Save(Path.Combine(outDir, prefix + "dialog.png"), ImageFormat.Png); dlg.Close(); }),
            (double.Parse(a.Length > 5 ? a[5] : "4.0", System.Globalization.CultureInfo.InvariantCulture), () => { show.Invoke(panel, new object[] { 1 }); }),
            (0.4, () => Snap("log")),
            (0.1, () => { show.Invoke(panel, new object[] { 0 }); }),
            (0.4, () => { Snap("live_after"); Diff("after GNSS + SMS", Layout(), idle); }),
            (0.1, () => ctl.SwitchTheme()),
            (0.5, () => Snap("live_switched")),
            (0.1, () => form.Close()),
        };
        int k = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        DateTime next = DateTime.Now.AddSeconds(steps[0].t);
        timer.Tick += (o, e) =>
        {
            if (k < steps.Length && DateTime.Now >= next)
            {
                steps[k].act();
                k++;
                if (k < steps.Length) next = DateTime.Now.AddSeconds(steps[k].t);
            }
        };
        form.Shown += (o, e) => timer.Start();
        Application.Run(form);
    }
}
