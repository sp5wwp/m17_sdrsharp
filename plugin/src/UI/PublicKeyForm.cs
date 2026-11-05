// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.M17.Engine;

namespace SDRSharp.M17.UI
{
    /// <summary>Asks for a name (usually the callsign) and a P-256 public key in hex or PEM.</summary>
    public sealed class PublicKeyForm : Form
    {
        readonly TextBox _name, _key;
        readonly Label _hint;
        readonly Button _ok;

        public string KeyName => _name.Text.Trim();
        public string KeyValue => _key.Text.Trim();

        public PublicKeyForm(Theme T)
        {
            Text = "Add public key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = T.Ui;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(380, 250);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            Label Cap(string s) => new Label { Text = s, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
            t.Controls.Add(Cap("Name (the callsign the key belongs to)"));
            _name = new TextBox { Dock = DockStyle.Fill, MaxLength = 31 };
            t.Controls.Add(_name);
            t.Controls.Add(Cap("Public key: 128 hex digits as gr-m17 prints it, or PEM"));
            _key = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 84, Font = T.Mono, ScrollBars = ScrollBars.Vertical };
            t.Controls.Add(_key);
            _hint = new Label { AutoSize = false, Height = 2 * T.Small.Height + 4, Dock = DockStyle.Fill, Font = T.Small, ForeColor = T.Dim };
            t.Controls.Add(_hint);

            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            _ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            row.Controls.Add(cancel);
            row.Controls.Add(_ok);
            t.Controls.Add(row);
            Controls.Add(t);
            AcceptButton = _ok;
            CancelButton = cancel;

            _name.TextChanged += (o, e) => Validate2();
            _key.TextChanged += (o, e) => Validate2();
            T.Apply(this);
            _hint.ForeColor = T.Dim;
            NativeScrollTheme.Set(_key, T.Dark);
            Validate2();
        }

        void Validate2()
        {
            var k = KeyText.PublicKey(_key.Text);
            bool nameOk = KeyName.Length > 0;
            _ok.Enabled = nameOk && k != null;
            _hint.Text = _key.Text.Trim().Length == 0 ? "The receiver only needs the public key, never the private one."
                       : k == null ? "Not a key in a known format (hex of 64, 65 or 33 bytes, or PEM)."
                       : !nameOk ? "Give the key a name." : "Format OK; the key is checked against the curve when added.";
        }
    }
}
