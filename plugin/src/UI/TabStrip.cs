// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace SDRSharp.M17.UI
{
    /// <summary>
    /// Tabs drawn entirely by the plugin. The WinForms TabControl leaves its header strip and
    /// page border to Windows, which paints them light whatever the theme, so this replaces it:
    /// a strip of headers on top and a host panel below that shows one page at a time.
    /// Headers wrap onto a second row when the panel is very narrow.
    /// </summary>
    public sealed class TabStrip : Control
    {
        readonly Theme T;
        readonly List<string> _names = new List<string>();
        readonly List<Control> _pages = new List<Control>();
        readonly List<Rectangle> _hit = new List<Rectangle>();
        readonly Panel _host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        readonly HeaderBar _bar;
        int _sel = -1, _hover = -1;

        public event EventHandler SelectedIndexChanged;

        public TabStrip(Theme theme)
        {
            T = theme;
            _bar = new HeaderBar(this) { Dock = DockStyle.Top };
            Controls.Add(_host);
            Controls.Add(_bar);
        }

        public int TabCount => _pages.Count;

        public void AddPage(string name, Control page)
        {
            _names.Add(name);
            _pages.Add(page);
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _host.Controls.Add(page);
            if (_sel < 0)
                SelectedIndex = 0;
            Relayout();
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _sel;
            set
            {
                if (value < 0 || value >= _pages.Count || value == _sel)
                    return;
                var old = _sel >= 0 ? _pages[_sel] : null;
                _sel = value;
                _pages[_sel].Visible = true;
                if (old != null)
                    old.Visible = false;
                _bar.Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void ApplyTheme()
        {
            BackColor = _host.BackColor = T.Back;
            _bar.BackColor = T.Panel;
            Relayout();
            _bar.Invalidate();
            _host.Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Relayout();
        }

        /// <summary>Lays the headers out in rows; the strip's height changes only with the panel's width.</summary>
        void Relayout()
        {
            _hit.Clear();
            int rowH = T.UiBold.Height + 8, x = 0, y = 0, w = Math.Max(1, ClientSize.Width);
            for (int i = 0; i < _names.Count; i++)
            {
                // measured in bold so the selected header doesn't change width
                int tw = TextRenderer.MeasureText(_names[i], T.UiBold).Width + 12;
                if (x > 0 && x + tw > w)
                {
                    x = 0;
                    y += rowH;
                }
                _hit.Add(new Rectangle(x, y, tw, rowH));
                x += tw;
            }
            int h = y + rowH + 1;
            if (_bar.Height != h)
                _bar.Height = h;
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < _hit.Count; i++)
                if (_hit[i].Contains(p))
                    return i;
            return -1;
        }

        void PaintBar(Graphics g, Rectangle client)
        {
            using (var b = new SolidBrush(T.Panel))
                g.FillRectangle(b, client);
            using (var line = new Pen(T.Line))
            {
                for (int i = 0; i < _hit.Count; i++)
                {
                    var r = _hit[i];
                    bool on = i == _sel;
                    if (on)
                        using (var b = new SolidBrush(T.Back))
                            g.FillRectangle(b, r.X, r.Y, r.Width, r.Height + 1);
                    TextRenderer.DrawText(g, _names[i], on ? T.UiBold : T.Ui, r,
                        on || i == _hover ? T.Fore : T.Dim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
                // a hairline under the strip, broken where the selected header joins its page
                int yb = client.Bottom - 1;
                if (_sel >= 0 && _sel < _hit.Count && _hit[_sel].Bottom >= yb)
                {
                    var s = _hit[_sel];
                    g.DrawLine(line, 0, yb, s.Left, yb);
                    g.DrawLine(line, s.Right, yb, client.Right, yb);
                }
                else
                    g.DrawLine(line, 0, yb, client.Right, yb);
            }
        }

        sealed class HeaderBar : Control
        {
            readonly TabStrip _o;

            public HeaderBar(TabStrip owner)
            {
                _o = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Default;
            }

            protected override void OnPaint(PaintEventArgs e) => _o.PaintBar(e.Graphics, ClientRectangle);

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                int i = _o.HitTest(e.Location);
                if (i >= 0)
                    _o.SelectedIndex = i;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = _o.HitTest(e.Location);
                if (i != _o._hover)
                {
                    _o._hover = i;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_o._hover != -1)
                {
                    _o._hover = -1;
                    Invalidate();
                }
            }
        }
    }
}
