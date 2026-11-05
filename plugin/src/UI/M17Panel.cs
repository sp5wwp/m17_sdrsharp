// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using SDRSharp.Common;
using SDRSharp.M17.Engine;
using SDRSharp.M17.Native;
using SDRSharp.M17.Plugin;
using SDRSharp.Radio;

namespace SDRSharp.M17.UI
{
    /// <summary>
    /// The plugin panel: Live, Log, Signal and Settings. Plain controls in SDR#'s theme colours,
    /// laid out to fit SDR#'s side panel (about 200 px and up).
    /// </summary>
    public sealed class M17Panel : UserControl
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const int Pad = 6;

        readonly ISharpControl _control;
        readonly M17Engine _eng;
        readonly AudioHook _audio;
        readonly Settings _s;
        readonly string _loadError;
        readonly Theme T;
        readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

        // header
        Label _lStatus;
        TabStrip _tabs;
        readonly List<Control> _pages = new List<Control>();
        // Live
        Label _lSig, _lKeys;
        TextBox _tbAes, _tbSeed;
        ListView _lvKeys;
        M17Event _lastEnd;
        Label _lSrc, _lInfo, _lMode, _lFrame, _lNotice, _lMetaTitle, _lMetaBody, _lSms, _lSmsTitle;
        Button _bCopyPos;
        ListView _lvHeard;
        // Log
        ListView _lvLog;
        Label _lLogPath;
        readonly List<M17Event> _log = new List<M17Event>();
        // Signal
        EyeView _eye;
        HistogramView _hist;
        SparkView _spark;
        CheckBox _cbFreeze;
        ComboBox _cbPersist;
        Label _lLevels;
        readonly Dictionary<string, Label> _metric = new Dictionary<string, Label>();
        readonly float[] _eyeBuf = new float[768 * M17RxNative.EyePoints];
        readonly float[] _histBuf = new float[M17RxNative.HistBins];
        readonly float[] _evmBuf = new float[M17RxNative.EvmHistory];
        // Settings
        Label _lSetup;
        // labels whose width follows the panel, and labels shown in the secondary text colour
        readonly List<Label> _wrap = new List<Label>();
        readonly List<Control> _dim = new List<Control>();
        readonly List<TableLayoutPanel> _stacks = new List<TableLayoutPanel>();
        Label _lDiag;

        // state
        LinkInfo _shown;
        bool _shownLive;
        DateTime _shownEnded;
        int _tick;

        public M17Panel(ISharpControl control, M17Engine engine, AudioHook audio, Settings settings, string loadError)
        {
            _control = control;
            _eng = engine;
            _audio = audio;
            _s = settings;
            _loadError = loadError;
            T = Theme.From(control);

            Font = T.Ui;
            Dock = DockStyle.Fill;
            Build();
            ApplyTheme();
            try
            {
                _control.PropertyChanged += (o, e) =>
                {
                    if (e.PropertyName != null && e.PropertyName.StartsWith("Theme", StringComparison.Ordinal) && IsHandleCreated)
                        BeginInvoke((Action)(() => { if (T.Reload(_control)) ApplyTheme(); }));
                };
            }
            catch
            {
                // the theme is also checked once a second
            }
            ShowTab(Util.Clamp(_s.SelectedTab, 0, _pages.Count - 1));
            Resize += (o, e) => Rewrap();
            Rewrap();
            foreach (var t in _stacks)
                SetScrollExtent(t);

            _timer.Interval = 100;
            _timer.Tick += (o, e) => UpdateUi();
            _timer.Start();
        }

        public void Shutdown()
        {
            _timer.Stop();
            _s.Save();
        }

        // ------------------------------------------------------------------ helpers

        Label L(string text, Font f = null, bool dim = false, bool wrap = false)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = true,
                Font = f ?? T.Ui,
                Margin = new Padding(0, 0, 0, 2),
            };
            if (dim)
                Dim(l);
            if (wrap)
                _wrap.Add(l);
            return l;
        }

        C Dim<C>(C c) where C : Control
        {
            c.ForeColor = T.Dim;
            if (!_dim.Contains(c))
                _dim.Add(c);
            return c;
        }

        /// <summary>Re-read SDR#'s theme and recolour everything (SDR# can switch themes while running).</summary>
        void ApplyTheme()
        {
            T.Apply(this);
            foreach (var c in _dim)
                c.ForeColor = T.Dim;
            _tabs.ApplyTheme();
            foreach (Control c in _stacks)
                NativeScrollTheme.Set(c, T.Dark);
            NativeScrollTheme.Set(_lvHeard, T.Dark);
            NativeScrollTheme.Set(_lvLog, T.Dark);
            foreach (var v in new Control[] { _eye, _hist, _spark, _lvHeard, _lvLog })
                v.Invalidate();
        }

        /// <summary>A small dim caption above a group of values.</summary>
        Label Caption(string text)
        {
            var l = L(text, T.Small, dim: true);
            l.Margin = new Padding(0, 8, 0, 1);
            return l;
        }

        /// <summary>
        /// A label of a fixed number of lines: its text changes, its size never does, so nothing
        /// below it moves while receiving. Text that does not fit ends in "…".
        /// </summary>
        Label Fixed(string text, Font f, int lines, bool dim = false)
        {
            var l = new Label
            {
                Text = text,
                AutoSize = false,
                AutoEllipsis = true,
                Font = f,
                Height = lines * f.Height + 2,
                Margin = new Padding(0, 0, 0, 2),
                TextAlign = ContentAlignment.TopLeft,
            };
            if (dim)
                Dim(l);
            return l;
        }

        static void AddFixed(TableLayoutPanel t, Control c)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, c.Height + c.Margin.Vertical));
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
        }

        void Rewrap()
        {
            int w = Math.Max(120, ClientSize.Width - 2 * Pad - SystemInformation.VerticalScrollBarWidth);
            foreach (var l in _wrap)
                l.MaximumSize = new Size(w, 0);
        }

        /// <summary>A single scrollable column; children stack from the top.</summary>
        TableLayoutPanel Stack()
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(Pad, 2, Pad, Pad),
                AutoScroll = true,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _stacks.Add(t);
            return t;
        }

        /// <summary>
        /// A tab scrolls vertically when SDR#'s panel is shorter than its content: the fixed rows
        /// plus a usable minimum for the stretching ones (a TableLayoutPanel does not do this itself).
        /// </summary>
        static void SetScrollExtent(TableLayoutPanel t)
        {
            int h = t.Padding.Vertical;
            for (int i = 0; i < t.RowStyles.Count; i++)
            {
                var c = t.GetControlFromPosition(0, i);
                var rs = t.RowStyles[i];
                if (rs.SizeType == SizeType.Absolute)
                    h += (int)rs.Height;
                else if (rs.SizeType == SizeType.Percent)
                    h += c is ListView ? 110 : 0;
                else if (c != null)
                    h += c.PreferredSize.Height + c.Margin.Vertical;
            }
            t.AutoScrollMinSize = new Size(0, h);
        }

        /// <summary>Takes the spare height at the bottom of a tab that has no stretching row.</summary>
        static void AddFiller(TableLayoutPanel t)
        {
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(new Panel { Margin = new Padding(0), Dock = DockStyle.Fill }, 0, t.RowStyles.Count - 1);
        }

        static void Add(TableLayoutPanel t, Control c, bool fill = false)
        {
            t.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            c.Margin = c.Margin == Padding.Empty ? new Padding(0) : c.Margin;
            if (fill || c is TableLayoutPanel || c is FlowLayoutPanel || c is ViewBase || c is ListView || c is TrackBar)
                c.Dock = DockStyle.Fill;
            t.Controls.Add(c, 0, t.RowStyles.Count - 1);
        }

        static FlowLayoutPanel Row(params Control[] cs)
        {
            var f = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0),
                Padding = new Padding(0),
            };
            f.Controls.AddRange(cs);
            return f;
        }

        Button Btn(string text, EventHandler click)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat,
                Padding = new Padding(4, 0, 4, 0),
                Margin = new Padding(0, 0, 4, 4),
            };
            b.Click += click;
            return b;
        }

        ListView List(params (string name, int width, bool right)[] cols)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.FixedSingle,
                OwnerDraw = true,
                Font = T.Ui,
                MinimumSize = new Size(0, 80),
            };
            foreach (var c in cols)
                lv.Columns.Add(c.name, c.width, c.right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            // header in the theme's colours; rows drawn by the system
            lv.DrawColumnHeader += (o, e) =>
            {
                using (var b = new SolidBrush(T.Back))
                    e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(T.Line))
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                            (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, T.Small, Rectangle.Inflate(e.Bounds, -3, 0), T.Dim, flags);
            };
            lv.DrawItem += (o, e) => e.DrawDefault = true;
            lv.DrawSubItem += (o, e) => e.DrawDefault = true;
            // the widest column(s) share what is left, so the list never needs to scroll sideways
            lv.Resize += (o, e) =>
            {
                int max = 0, used = 0, n = 0;
                foreach (var c in cols) max = Math.Max(max, c.width);
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i].width == max) n++; else used += lv.Columns[i].Width;
                int w = (lv.ClientSize.Width - used) / n;
                if (w > 30)
                    for (int i = 0; i < cols.Length; i++)
                        if (cols[i].width == max) lv.Columns[i].Width = w;
            };
            return lv;
        }

        // ------------------------------------------------------------------ build

        void Build()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _lStatus = Fixed("", T.Ui, 1);
            _lStatus.Margin = new Padding(Pad, Pad, Pad, 4);
            _lStatus.Dock = DockStyle.Fill;
            root.RowStyles[0] = new RowStyle(SizeType.Absolute, _lStatus.Height + _lStatus.Margin.Vertical);
            root.Controls.Add(_lStatus, 0, 0);

            _tabs = new TabStrip(T) { Dock = DockStyle.Fill, Margin = new Padding(Pad, 0, Pad, Pad) };
            string[] names = { "Live", "Log", "Signal", "Settings" };
            _pages.Add(BuildLive());
            _pages.Add(BuildLog());
            _pages.Add(BuildSignal());
            _pages.Add(BuildSettings());
            for (int i = 0; i < names.Length; i++)
                _tabs.AddPage(names[i], _pages[i]);
            _tabs.SelectedIndexChanged += (o, e) => ShowTab(_tabs.SelectedIndex);
            root.Controls.Add(_tabs, 0, 1);
            Controls.Add(root);
        }

        Control BuildLive()
        {
            var t = Stack();
            AddFixed(t, CaptionFixed("Source"));
            _lSrc = Fixed("-", T.Big, 1);
            AddFixed(t, _lSrc);
            _lInfo = Fixed("", T.Ui, 1);
            AddFixed(t, _lInfo);
            _lMode = Fixed("", T.Ui, 1, dim: true);
            AddFixed(t, _lMode);
            _lFrame = Fixed("FN -  ·  LICH -/6", T.Ui, 1, dim: true);
            AddFixed(t, _lFrame);
            _lSig = Fixed("", T.Ui, 1, dim: true);
            AddFixed(t, _lSig);

            _lNotice = Fixed("", T.Ui, 2);
            _lNotice.Margin = new Padding(0, 6, 0, 0);
            AddFixed(t, _lNotice);

            _lMetaTitle = CaptionFixed("META");
            AddFixed(t, _lMetaTitle);
            _lMetaBody = Fixed("-", T.Ui, 3);
            AddFixed(t, _lMetaBody);
            _bCopyPos = Btn("Copy position", (o, e) =>
            {
                if (_shown != null && _shown.GnssPosValid)
                    Clipboard.SetText(string.Format(Inv, "{0:F6}, {1:F6}", _shown.Lat, _shown.Lon));
            });
            _bCopyPos.Enabled = false;
            _bCopyPos.Margin = new Padding(0, 2, 0, 0);
            var copyRow = new Panel { Height = _bCopyPos.PreferredSize.Height + 2, Margin = new Padding(0) };
            copyRow.Controls.Add(_bCopyPos);
            AddFixed(t, copyRow);

            _lSmsTitle = CaptionFixed("Last SMS");
            AddFixed(t, _lSmsTitle);
            _lSms = Fixed("-", T.Ui, 3);
            AddFixed(t, _lSms);

            AddFixed(t, CaptionFixed("Last heard"));
            _lvHeard = List(("Time", 40, false), ("From", 64, false), ("To", 64, false), ("EVM", 32, true));
            Add(t, _lvHeard, fill: true);
            return t;
        }

        Label CaptionFixed(string text)
        {
            var l = Fixed(text, T.Small, 1, dim: true);
            l.Margin = new Padding(0, 6, 0, 1);
            return l;
        }

        Control BuildLog()
        {
            var t = Stack();
            _lvLog = List(("Time", 52, false), ("From", 64, false), ("Info", 120, false));
            _lvLog.ShowItemToolTips = true;
            Add(t, _lvLog, fill: true);
            Add(t, Row(
                Btn("Clear", (o, e) => { _log.Clear(); _lvLog.Items.Clear(); }),
                Btn("Export CSV…", (o, e) => ExportCsv()),
                Btn("Open folder", (o, e) =>
                {
                    try
                    {
                        Directory.CreateDirectory(Settings.Folder);
                        Process.Start(new ProcessStartInfo("explorer.exe", Settings.Folder) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "M17 Decoder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                })));
            _lLogPath = Fixed("", T.Small, 2, dim: true);
            AddFixed(t, _lLogPath);
            return t;
        }

        Control BuildSignal()
        {
            var t = Stack();

            // eye controls, above the plot
            _cbFreeze = new CheckBox { Text = "Freeze", AutoSize = true, Margin = new Padding(0, 4, 8, 0) };
            _cbPersist = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72, Margin = new Padding(0, 2, 0, 2) };
            _cbPersist.Items.AddRange(new object[] { "1 frame", "2 frames", "4 frames" });
            _cbPersist.SelectedIndex = Util.Clamp(_s.EyePersistence, 0, 2);
            _cbPersist.SelectedIndexChanged += (o, e) => { _s.EyePersistence = _cbPersist.SelectedIndex; _s.Save(); };
            var controls = Row(_cbFreeze, _cbPersist);
            controls.AutoSize = false;
            controls.Height = _cbPersist.PreferredHeight + 6;
            AddFixed(t, controls);

            var scope = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 150, Margin = new Padding(0, 4, 0, 0) };
            scope.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scope.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
            scope.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _eye = new EyeView(T) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 3, 0) };
            _hist = new HistogramView(T) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            scope.Controls.Add(_eye, 0, 0);
            scope.Controls.Add(_hist, 1, 0);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            scope.Dock = DockStyle.Fill;
            t.Controls.Add(scope, 0, t.RowStyles.Count - 1);

            // two columns of "caption  value" pairs
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 6, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            void M(string key, string caption)
            {
                var c = L(caption, T.Ui, dim: true);
                c.Margin = new Padding(0, 0, 3, 2);
                c.AutoSize = false;
                c.Width = TextRenderer.MeasureText(caption, T.Ui).Width + 2;
                var v = Fixed("-", T.Ui, 1); // fixed size: changing values never relayout the grid
                v.Margin = new Padding(0, 0, 2, 2);
                v.Dock = DockStyle.Fill;
                c.Height = v.Height;
                grid.Controls.Add(c);
                grid.Controls.Add(v);
                _metric[key] = v;
            }
            M("evm", "EVM");
            M("mer", "MER");
            M("eye", "Eye");
            M("ber", "BER");
            M("dev", "Dev");
            M("ratio", "Ratio");
            M("off", "Offset");
            M("ppm", "Clock");
            Add(t, grid);

            _lLevels = Fixed("", T.Mono, 5, dim: true);
            _lLevels.Margin = new Padding(0, 4, 0, 0);
            AddFixed(t, _lLevels);

            Add(t, Caption("EVM per frame (dashed: 15 %)"));
            _spark = new SparkView(T);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _spark.Dock = DockStyle.Fill;
            _spark.Margin = new Padding(0);
            t.Controls.Add(_spark, 0, t.RowStyles.Count - 1);

            AddFiller(t);
            return t;
        }

        Control BuildSettings()
        {
            var t = Stack();
            CheckBox Check(string text, bool value, Action<bool> set)
            {
                var c = new CheckBox { Text = text, AutoSize = true, Checked = value, Margin = new Padding(0, 3, 0, 0) };
                c.CheckedChanged += (o, e) => set(c.Checked);
                return c;
            }
            Add(t, Check("Decoder enabled", _s.Enabled, v => { _s.Enabled = v; ApplyEngineSettings(); }));
            Add(t, Check("Invert polarity", _s.Invert, v => { _s.Invert = v; ApplyEngineSettings(); }));
            var mute = Check("Mute audio when there's no Codec2 stream", _s.MuteWhenIdle, v => { _s.MuteWhenIdle = v; ApplyEngineSettings(); });
            mute.AutoSize = false;
            mute.CheckAlign = ContentAlignment.TopLeft;
            mute.TextAlign = ContentAlignment.TopLeft;
            mute.Height = 2 * T.Ui.Height + 4;
            AddFixed(t, mute);
            Add(t, Check("Log to CSV file", _s.LogToCsv, v => { _s.LogToCsv = v; _s.Save(); }));


            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            void Num(string caption, decimal min, decimal max, decimal step, int dec, decimal value, Action<decimal> set)
            {
                var c = L(caption, T.Ui);
                c.Anchor = AnchorStyles.Left;
                var n = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = dec, Width = 52,
                                            Value = Math.Min(max, Math.Max(min, value)), Margin = new Padding(4, 2, 0, 2) };
                n.ValueChanged += (o, e) => set(n.Value);
                grid.Controls.Add(c);
                grid.Controls.Add(n);
            }
            Num("Sync threshold", 0.70m, 0.98m, 0.01m, 2, (decimal)_s.SyncThreshold, v => { _s.SyncThreshold = (float)v; ApplyEngineSettings(); });
            Num("Mute above % errors", 1, 30, 1, 0, (decimal)(_s.ViterbiMute * 100f), v => { _s.ViterbiMute = (float)v / 100f; ApplyEngineSettings(); });
            Add(t, grid);

            var setup = Btn("Set up receiver for M17", (o, e) => SetupReceiver());
            setup.Margin = new Padding(0, 10, 0, 2);
            Add(t, setup);
            _lSetup = Fixed("NFM, 12.5 kHz filter, squelch off.", T.Small, 2, dim: true);
            AddFixed(t, _lSetup);

            BuildKeys(t);

            AddFixed(t, CaptionFixed("Diagnostics"));
            _lDiag = Fixed("", T.Small, 8, dim: true);
            AddFixed(t, _lDiag);

            var about = new StringBuilder("M17 Decoder 0.2.2");
            if (_eng != null)   // "libm17rx 0.2.0 (libm17 1.4.1)" -> "libm17rx 0.2.0, libm17 1.4.1", single spaces only
                about.Append("\n" + System.Text.RegularExpressions.Regex.Replace(
                    M17Engine.NativeVersion.Replace(" (", ", ").Replace(")", ""), @"\s+", " ").Trim());
            about.Append("\nGPL-2.0-or-later with an SDR# exception");
            var a = L(about.ToString(), T.Small, dim: true, wrap: true);
            a.Margin = new Padding(0, 12, 0, 0);
            Add(t, a);
            AddFiller(t);
            return t;
        }

        void BuildKeys(TableLayoutPanel t)
        {
            AddFixed(t, CaptionFixed("Keys"));
            TextBox Secret(string caption, string value)
            {
                var c = Fixed(caption, T.Small, 1, dim: true);
                AddFixed(t, c);
                var tb = new TextBox { Text = value, UseSystemPasswordChar = true, Dock = DockStyle.Fill,
                                       Margin = new Padding(0, 0, 0, 4), Font = T.Mono };
                tb.TextChanged += (o, e) => OnKeysEdited();
                AddFixed(t, tb);
                return tb;
            }
            _tbAes = Secret("AES key, hex (128, 192 or 256 bits)", _s.AesKey);
            _tbSeed = Secret("Scrambler seed, hex (8, 16 or 24 bits)", _s.ScramblerSeed);
            var show = new CheckBox { Text = "Show", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            show.CheckedChanged += (o, e) => _tbAes.UseSystemPasswordChar = _tbSeed.UseSystemPasswordChar = !show.Checked;
            AddFixed(t, show);

            AddFixed(t, Fixed("Public keys, to check signatures", T.Small, 1, dim: true));
            _lvKeys = List(("Name", 70, false), ("Key", 90, false));
            _lvKeys.Height = 74;
            _lvKeys.MinimumSize = new Size(0, 74);
            AddFixed(t, _lvKeys);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                                            Margin = new Padding(0, 2, 0, 0) };
            var add = Btn("Add…", (o, e) => AddPublicKey());
            var del = Btn("Remove", (o, e) => RemovePublicKey());
            add.Margin = new Padding(0, 0, 4, 0);
            row.Controls.Add(add);
            row.Controls.Add(del);
            row.AutoSize = false;
            row.Height = Math.Max(add.PreferredSize.Height, del.PreferredSize.Height) + 2;
            AddFixed(t, row);
            _lKeys = Fixed("", T.Small, 2, dim: true);
            AddFixed(t, _lKeys);
            FillKeyList();
            _lKeys.Text = KeyConfig.Apply(_eng, _s);
        }

        void OnKeysEdited()
        {
            _s.AesKey = _tbAes.Text.Trim();
            _s.ScramblerSeed = _tbSeed.Text.Trim();
            _lKeys.Text = KeyConfig.Apply(_eng, _s);
            _s.Save();
        }

        void FillKeyList()
        {
            _lvKeys.BeginUpdate();
            _lvKeys.Items.Clear();
            foreach (var k in _s.PublicKeys)
            {
                var b = KeyText.PublicKey(k.Key);
                var it = new ListViewItem(k.Name);
                it.SubItems.Add(b == null ? "(unreadable)" : BitConverter.ToString(b, 0, Math.Min(6, b.Length)).Replace("-", "").ToLowerInvariant() + "…");
                it.ToolTipText = k.Key;
                _lvKeys.Items.Add(it);
            }
            _lvKeys.EndUpdate();
        }

        void AddPublicKey()
        {
            using (var f = new PublicKeyForm(T))
            {
                if (f.ShowDialog(this) != DialogResult.OK)
                    return;
                _s.PublicKeys.RemoveAll(k => string.Equals(k.Name, f.KeyName, StringComparison.OrdinalIgnoreCase));
                _s.PublicKeys.Add(new PublicKeyEntry { Name = f.KeyName, Key = f.KeyValue });
            }
            FillKeyList();
            _lKeys.Text = KeyConfig.Apply(_eng, _s);
            _s.Save();
        }

        void RemovePublicKey()
        {
            if (_lvKeys.SelectedIndices.Count == 0)
                return;
            int i = _lvKeys.SelectedIndices[0];
            if (i >= 0 && i < _s.PublicKeys.Count)
                _s.PublicKeys.RemoveAt(i);
            FillKeyList();
            _lKeys.Text = KeyConfig.Apply(_eng, _s);
            _s.Save();
        }

        void ShowTab(int idx)
        {
            if (_tabs.SelectedIndex != idx)
                _tabs.SelectedIndex = idx;
            if (_s.SelectedTab != idx)
            {
                _s.SelectedTab = idx;
                _s.Save();
            }
        }

        // ------------------------------------------------------------------ actions

        void ApplyEngineSettings()
        {
            if (_eng != null)
            {
                _eng.Enabled = _s.Enabled;
                _eng.Configure(_s.Invert, _s.SyncThreshold, _s.ViterbiMute, false);
                if (!_s.Enabled)
                    _eng.Reset();
            }
            if (_audio != null)
                _audio.MuteWhenIdle = _s.MuteWhenIdle;
            _s.Save();
        }

        void SetupReceiver()
        {
            try
            {
                _control.DetectorType = DetectorType.NFM;
                _control.FilterBandwidth = 12500;
                _control.SquelchEnabled = false;
                _lSetup.Text = "Done: NFM, 12.5 kHz, squelch off.";
            }
            catch (Exception ex)
            {
                _lSetup.Text = "Could not change the receiver: " + ex.Message;
            }
        }

        void ExportCsv()
        {
            using var d = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = "m17-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv) + ".csv",
            };
            if (d.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                using var w = new StreamWriter(d.FileName, false, new UTF8Encoding(false));
                w.WriteLine(CsvLog.Header);
                foreach (var e in _log)
                    w.WriteLine(CsvLog.Line(e));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "M17 Decoder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------ updates

        void UpdateUi()
        {
            _tick++;
            if (_eng == null)
            {
                _lStatus.Text = "Not loaded";
                ShowNotice("The decoder library could not be loaded: " + _loadError);
                return;
            }

            while (_eng.TryGetEvent(out var ev))
                OnEvent(ev);

            if (_tick % 10 == 0 && T.Reload(_control)) // once a second, in case SDR# raises no event
                ApplyTheme();

            var st = _eng.GetStatus();
            UpdateStatusLine(st);
            if (_tabs.SelectedIndex == 3 && _tick % 5 == 0)
                UpdateDiagnostics(st);
            UpdateLive(st);
            if (_tabs.SelectedIndex == 2 && !_cbFreeze.Checked && _tick % 2 == 0)
                UpdateSignal(st);
            _lLogPath.Text = _s.LogToCsv ? "Logging to " + _s.EffectiveLogPath : "Not logging to a file";
        }

        void UpdateDiagnostics(StatusInfo st)
        {
            // short lines: each fits the narrowest panel, so nothing wraps or gets cut off
            var a = _eng.Audio;
            string ch = _audio == null ? "-" : _audio.MeasuredRatio > 0
                ? (_audio.Channels == 1 ? "1 channel" : _audio.Channels + " channels")
                : "channels: measuring";
            _lDiag.Text = string.Format(Inv,
                "IQ {0:F0} Hz, measured {1:F0} Hz\n" +
                "Audio {2:F0} Hz, {3}\n" +
                "Blocks: IQ {4}, audio {5}\n" +
                "Voice buffer {6} ms\n" +
                "Underruns {7}, trims {8}\n" +
                "Clock {9:+0;-0} ppm\n" +
                "Offset {10:+0;-0} Hz\n" +
                "Frames {11}, lost {12}, sync {13:F2}",
                st.InputRate, _eng.MeasuredIqRate,
                _audio?.Rate ?? 0, ch,
                _eng.LastIqBlock, _audio?.LastLength ?? 0,
                a.BufferedMs, a.Underruns, a.Trims,
                st.ClockPpm, st.FreqOffset,
                st.Frames, st.FramesLost, st.SyncQuality);
        }

        void OnEvent(M17Event ev)
        {
            switch (ev.Kind)
            {
                case M17EventKind.StreamStart:
                case M17EventKind.LinkUpdate:
                    _shown = ev.Link;
                    _shownLive = true;
                    if (ev.Kind == M17EventKind.StreamStart)
                        _lastEnd = null;
                    break;
                case M17EventKind.StreamEnd:
                    _lastEnd = ev;
                    _shown = ev.Link;
                    _shownLive = false;
                    _shownEnded = DateTime.Now;
                    AddHeard(ev);
                    AddLog(ev);
                    break;
                case M17EventKind.Packet:
                case M17EventKind.PacketBad:
                    if (ev.Kind == M17EventKind.Packet && ev.Protocol == 0x05)
                    {
                        _lSms.Text = $"{ev.Time:HH:mm:ss} {ev.Link.Source} to {ev.Link.Destination}:\n{ev.Sms}";
                    }
                    AddLog(ev);
                    break;
            }
        }

        void AddHeard(M17Event e)
        {
            var it = new ListViewItem(e.Time.ToString("HH:mm", Inv));
            it.SubItems.Add(e.Link.Source);
            it.SubItems.Add(e.Link.Destination);
            it.SubItems.Add(e.Evm > 0 ? e.Evm.ToString("F1", Inv) : "-");
            _lvHeard.Items.Insert(0, it);
            while (_lvHeard.Items.Count > 50)
                _lvHeard.Items.RemoveAt(_lvHeard.Items.Count - 1);
        }

        void AddLog(M17Event e)
        {
            _log.Add(e);
            if (_log.Count > 5000)
                _log.RemoveAt(0);
            string info;
            if (e.Kind == M17EventKind.StreamEnd)
            {
                string kind = e.Link.DataType == M17DataType.Data ? "Data" : "Voice";
                info = string.Format(Inv, "{0} to {1}, {2:F1} s", kind, e.Link.Destination, e.Duration);
                if (e.Link.Encryption != M17Encryption.None)
                    info += ", " + e.Link.EncryptionState;
                if (e.Link.Signed)
                    info += ", signature " + e.SignatureText;
                if (e.Link.MetaSummary.Length > 0)
                    info += ", " + e.Link.MetaSummary;
            }
            else if (e.Kind == M17EventKind.Packet)
            {
                info = e.Protocol == 0x05 ? "SMS to " + e.Link.Destination + ": " + e.Sms
                                          : e.ProtocolText + " packet, " + e.Data.Length + " bytes";
            }
            else
            {
                info = "Packet with a CRC or framing error";
            }
            var it = new ListViewItem(e.Time.ToString("HH:mm:ss", Inv)) { ToolTipText = info };
            it.SubItems.Add(e.Link.Source);
            it.SubItems.Add(info);
            _lvLog.Items.Insert(0, it);
            while (_lvLog.Items.Count > 2000)
                _lvLog.Items.RemoveAt(_lvLog.Items.Count - 1);
            if (_s.LogToCsv)
                CsvLog.Append(_s.EffectiveLogPath, e);
        }

        void UpdateStatusLine(StatusInfo st)
        {
            string s;
            if (!_s.Enabled)
                s = "Decoder off";
            else if (!st.Locked)
                s = "No signal";
            else
            {
                var sb = new StringBuilder("Sync");
                if (st.State == 2)
                    sb.Append(" · Packet");
                else if (st.Link.Valid)
                    sb.Append(st.Link.FromLich ? " · LSF from LICH" : " · LSF OK");
                else
                    sb.Append(" · LICH " + Bits(st.LichChunks) + "/6");
                if (st.MeasValid)
                    sb.Append(string.Format(Inv, " · EVM {0:F1} % · {1:+0;-0} Hz", st.EvmRms, st.FreqOffset));
                s = sb.ToString();
            }
            if (_lStatus.Text != s)
                _lStatus.Text = s;
        }

        static int Bits(int v)
        {
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }

        void UpdateLive(StatusInfo st)
        {
            string fr = st.Locked && st.State == 1 && st.LastFn >= 0
                ? string.Format(Inv, "FN {0}  ·  LICH {1}/6  ·  lost {2}", st.LastFn & 0x7FFF, Bits(st.LichChunks), st.FramesLost)
                : "FN -  ·  LICH -/6";
            if (_lFrame.Text != fr)
                _lFrame.Text = fr;

            // the receiver's live link wins; otherwise the last one, kept for 30 s
            LinkInfo l = null;
            bool live = false;
            if (st.Locked && st.State == 1 && st.Link.Valid)
            {
                l = st.Link;
                live = true;
            }
            else if (_shown != null && (_shownLive || (DateTime.Now - _shownEnded).TotalSeconds < 30))
            {
                l = _shown;
                live = _shownLive && st.Locked;
            }

            if (l == null)
            {
                _lSrc.Text = "-";
                _lSrc.ForeColor = T.Dim;
                _lInfo.Text = st.InputRate > 0 ? "Listening" : "Waiting for IQ from SDR#";
                _lMode.Text = "";
                _lSig.Text = "";
                _lMetaTitle.Text = "META";
                _lMetaBody.Text = "-";
                _bCopyPos.Enabled = false;
                IdleNotice(st);
                return;
            }

            _lSrc.Text = l.Source;
            _lSrc.ForeColor = live ? T.Fore : T.Dim;
            _lInfo.Text = string.Format(Inv, "To {0}, CAN {1}, {2}", l.Destination, l.Can,
                                        live ? st.Duration.ToString("F1", Inv) + " s" : "ended");
            _lMode.Text = Sentence(l.DataTypeText) + ", " + l.EncryptionState;
            var end = !live ? _lastEnd : null;
            _lSig.Text = !l.Signed ? "Not signed"
                : end == null ? "Signed · checked at the end"
                : end.Signature == M17Signature.Valid ? "Signature valid · " + end.SignatureKey
                : end.Signature == M17Signature.Invalid ? "Signature INVALID"
                : "Signed · " + end.SignatureText;

            if (st.InvertedHint)
                ShowNotice("The signal looks inverted. Turn on Invert polarity in Settings.");
            else if (end != null && end.Signature == M17Signature.Invalid)
                ShowNotice("No public key verifies this signature: the sender may not be " + l.Source + ".");
            else if (l.Decrypt == M17Decrypt.NoKey)
                ShowNotice(l.Encryption == M17Encryption.Aes
                    ? "Audio is muted: set an AES-" + (128 + 64 * l.EncSubtype) + " key in Settings."
                    : "Audio is muted: set a " + (8 * (l.EncSubtype + 1)) + "-bit scrambler seed in Settings.");
            else if (l.Decrypt == M17Decrypt.Unsupported)
                ShowNotice("Reserved encryption type: audio is muted.");
            else if (l.DataType == M17DataType.VoiceData)
                ShowNotice("Voice + data (Codec2 1600) is not decoded in this version.");
            else if (end != null && end.Signature == M17Signature.NoKey)
                ShowNotice("Add the sender's public key in Settings to check signatures.");
            else
                ShowNotice("");

            bool copy = false;
            switch (l.MetaKind)
            {
                case M17MetaKind.Text:
                    _lMetaTitle.Text = "META · Text";
                    _lMetaBody.Text = l.MetaText.Length > 0 ? l.MetaText + (l.TextComplete ? "" : " …") : "-";
                    break;
                case M17MetaKind.Gnss:
                    _lMetaTitle.Text = "META · GNSS (" + l.StationText + ")";
                    var sb = new StringBuilder(l.PositionText);
                    if (l.GnssAltValid) sb.Append(string.Format(Inv, "\n{0:F0} m", l.AltM));
                    if (l.GnssSpeedValid) sb.Append(string.Format(Inv, ", {0:F1} km/h, {1}°", l.SpeedKmh, l.GnssBearing));
                    _lMetaBody.Text = sb.ToString();
                    copy = l.GnssPosValid;
                    break;
                case M17MetaKind.ExtendedCallsign:
                    _lMetaTitle.Text = "META · Extended callsign";
                    _lMetaBody.Text = l.Ecd1 + (l.Ecd2.Length > 0 ? " via " + l.Ecd2 : "");
                    break;
                case M17MetaKind.Raw:
                    _lMetaTitle.Text = "META · Reserved type";
                    _lMetaBody.Text = BitConverter.ToString(l.Meta).Replace("-", " ");
                    break;
                default:
                    _lMetaTitle.Text = "META · None";
                    _lMetaBody.Text = l.Encryption != M17Encryption.None ? "Carries the encryption nonce" : "-";
                    break;
            }
            if (_bCopyPos.Enabled != copy)
                _bCopyPos.Enabled = copy;
        }

        static string Sentence(string upper) =>
            upper.Length == 0 ? upper : upper.Substring(0, 1) + upper.Substring(1).ToLowerInvariant();

        void IdleNotice(StatusInfo st)
        {
            if (st.InvertedHint)
            {
                ShowNotice("An inverted M17 signal is on frequency. Turn on Invert polarity in Settings.");
                return;
            }
            DetectorType dt;
            try { dt = _control.DetectorType; } catch { dt = DetectorType.NFM; }
            if (dt != DetectorType.NFM && dt != DetectorType.RAW)
                ShowNotice("M17 needs NFM with a 12.5 kHz filter: see Settings.");
            else
                ShowNotice("");
        }

        void ShowNotice(string text)
        {
            if (_lNotice.Text != text)
                _lNotice.Text = text;
        }

        void UpdateSignal(StatusInfo st)
        {
            int want = 192 << Util.Clamp(_s.EyePersistence, 0, 2); // 1, 2 or 4 frames of 192 symbols
            int n = _eng.GetEye(_eyeBuf);
            if (n > want)
            {
                Array.Copy(_eyeBuf, (n - want) * M17RxNative.EyePoints, _eyeBuf, 0, want * M17RxNative.EyePoints);
                n = want;
            }
            _eye.SetTraces(_eyeBuf, st.MeasValid ? n : 0);
            _eng.GetHistogram(_histBuf);
            _hist.SetBins(_histBuf);
            _spark.SetValues(_evmBuf, _eng.GetEvmHistory(_evmBuf));

            if (!st.MeasValid)
            {
                foreach (var v in _metric.Values) v.Text = "-";
                _lLevels.Text = "";
                return;
            }
            _metric["evm"].Text = st.EvmRms.ToString("F1", Inv) + " %";
            _metric["mer"].Text = st.Mer.ToString("F1", Inv) + " dB";
            _metric["eye"].Text = st.EyeOpening.ToString("F0", Inv) + " %";
            _metric["ber"].Text = (st.BerPreFec * 100).ToString("F2", Inv) + " %";
            _metric["dev"].Text = (st.Deviation / 1000).ToString("F2", Inv) + "k";
            _metric["ratio"].Text = st.InnerOuter.ToString("F3", Inv);
            _metric["off"].Text = st.FreqOffset.ToString("+0;-0", Inv) + " Hz";
            _metric["ppm"].Text = st.ClockPpm.ToString("+0;-0", Inv) + " ppm";
            var lv = new StringBuilder();
            string[] names = { "-3", "-1", "+1", "+3" };
            for (int i = 3; i >= 0; i--)
                lv.AppendFormat(Inv, "{0}  {1,6:+0.00;-0.00}  σ {2:0.00}{3}", names[i], st.LevelMean[i], st.LevelSigma[i], i > 0 ? "\n" : "");
            if (!st.DataAided)
                lv.Append("\n(reference: nearest level)");
            _lLevels.Text = lv.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
