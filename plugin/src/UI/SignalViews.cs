// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SDRSharp.M17.UI
{
    /// <summary>Base for the custom-painted signal views.</summary>
    public abstract class ViewBase : Control
    {
        protected Theme T;

        protected ViewBase(Theme t)
        {
            T = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        // only the host theme's colours
        protected Color PlotBack => T.Editor;
        protected Color Grid => T.Line;
        protected Color Trace => T.Fore;
        protected Color Threshold => T.Dim;
    }

    /// <summary>
    /// Eye diagram: traces of M17RxNative.EyePoints points over two symbols (-1 .. +1 around the
    /// symbol instant), normalised so the ideal levels are -3, -1, +1, +3.
    /// </summary>
    public sealed class EyeView : ViewBase
    {
        float[] _traces = Array.Empty<float>();
        int _count;
        const int Points = 21;
        const float Span = 4.5f;

        public EyeView(Theme t) : base(t) { }

        public void SetTraces(float[] traces, int count)
        {
            _traces = traces;
            _count = count;
            Invalidate();
        }

        float Y(float v, RectangleF r) => r.Top + (Span - v) / (2 * Span) * r.Height;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(PlotBack)) g.FillRectangle(b, r);

            using (var gp = new Pen(Grid))
            using (var tp = new Pen(Threshold) { DashStyle = DashStyle.Dash })
            using (var sp = new Pen(Grid) { DashStyle = DashStyle.Dot })
            {
                foreach (float lvl in new[] { -3f, -1f, 1f, 3f })
                    g.DrawLine(gp, r.Left, Y(lvl, r), r.Right, Y(lvl, r));
                foreach (float th in new[] { -2f, 0f, 2f })
                    g.DrawLine(tp, r.Left, Y(th, r), r.Right, Y(th, r));
                float xc = r.Left + r.Width / 2;
                g.DrawLine(sp, xc, r.Top, xc, r.Bottom); // sampling instant
                g.DrawRectangle(gp, r.X, r.Y, r.Width, r.Height);
            }

            using (var f = new SolidBrush(T.Dim))
            {
                foreach (float lvl in new[] { 3f, 1f, -1f, -3f })
                    g.DrawString((lvl > 0 ? "+" : "") + lvl.ToString("0"), T.Small, f, r.Right - 18, Y(lvl, r) - 7);
            }

            if (_count == 0)
            {
                using var f = new SolidBrush(T.Dim);
                g.DrawString("No signal", T.Ui, f, 8, 8);
                return;
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            int alpha = Util.Clamp(6000 / Math.Max(_count, 1), 8, 90);
            using var pen = new Pen(Color.FromArgb(alpha, Trace), 1f);
            var pts = new PointF[Points];
            for (int t = 0; t < _count; t++)
            {
                for (int i = 0; i < Points; i++)
                {
                    float v = Util.Clamp(_traces[t * Points + i], -Span, Span);
                    pts[i] = new PointF(r.Left + i * r.Width / (Points - 1), Y(v, r));
                }
                g.DrawLines(pen, pts);
            }
        }
    }

    /// <summary>Histogram of the symbol levels, drawn sideways to line up with the eye's level axis.</summary>
    public sealed class HistogramView : ViewBase
    {
        float[] _bins = Array.Empty<float>();

        public HistogramView(Theme t) : base(t) { }

        public void SetBins(float[] bins)
        {
            _bins = bins;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(PlotBack)) g.FillRectangle(b, r);
            using (var gp = new Pen(Grid)) g.DrawRectangle(gp, r.X, r.Y, r.Width, r.Height);
            if (_bins.Length == 0)
                return;
            float max = 1e-6f;
            foreach (float v in _bins) max = Math.Max(max, v);
            float bh = r.Height / _bins.Length;
            using var br = new SolidBrush(Trace);
            for (int i = 0; i < _bins.Length; i++)
            {
                // bin 0 is -4.5 (bottom), the last is +4.5 (top)
                float w = _bins[i] / max * (r.Width - 2);
                float y = r.Bottom - (i + 1) * bh;
                g.FillRectangle(br, r.Left + 1, y, w, Math.Max(1f, bh - 0.5f));
            }
        }
    }

    /// <summary>EVM per frame over the last frames, with a warning line.</summary>
    public sealed class SparkView : ViewBase
    {
        float[] _v = Array.Empty<float>();
        int _n;
        public float Warn = 15f;

        public SparkView(Theme t) : base(t) { }

        public void SetValues(float[] v, int n)
        {
            _v = v;
            _n = n;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(PlotBack)) g.FillRectangle(b, r);
            using (var gp = new Pen(Grid)) g.DrawRectangle(gp, r.X, r.Y, r.Width, r.Height);
            float top = Math.Max(Warn * 1.3f, 5f);
            for (int i = 0; i < _n; i++) top = Math.Max(top, _v[i] * 1.1f);
            float Y(float v) => r.Bottom - Math.Min(v, top) / top * (r.Height - 2) - 1;
            using (var wp = new Pen(T.Dim) { DashStyle = DashStyle.Dash })
                g.DrawLine(wp, r.Left, Y(Warn), r.Right, Y(Warn));
            if (_n < 2)
                return;
            var pts = new PointF[_n];
            for (int i = 0; i < _n; i++)
                pts[i] = new PointF(r.Left + i * r.Width / (_n - 1), Y(_v[i]));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var p = new Pen(Trace, 1.5f);
            g.DrawLines(p, pts);
        }
    }
}
