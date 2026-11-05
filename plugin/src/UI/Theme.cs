// SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
using System;
using System.Drawing;
using System.Windows.Forms;
using SDRSharp.Common;

namespace SDRSharp.M17.UI
{
    /// <summary>
    /// SDR#'s own theme colours, plus two shades derived from them. The plugin adds no colours
    /// of its own, so it looks like the rest of SDR# in any theme.
    /// </summary>
    public sealed class Theme
    {
        public Color Back, Fore, Panel, Editor, Dim, Line;
        public bool Dark;
        public Font Ui, UiBold, Small, Mono, Big;

        public static Theme From(ISharpControl c)
        {
            var t = new Theme();
            t.Reload(c);
            t.Ui = new Font("Segoe UI", 8.25f);
            t.UiBold = new Font("Segoe UI", 8.25f, FontStyle.Bold);
            t.Small = new Font("Segoe UI", 7.5f);
            t.Mono = new Font("Consolas", 8.25f);
            t.Big = new Font("Segoe UI", 13f, FontStyle.Bold);
            return t;
        }

        /// <summary>Read SDR#'s current colours. Returns true if they changed.</summary>
        public bool Reload(ISharpControl c)
        {
            var t = this;
            Color oldBack = Back, oldFore = Fore, oldPanel = Panel, oldEditor = Editor;
            try
            {
                t.Dark = c.ThemeIsDark;
                t.Back = c.ThemeBackColor;
                t.Fore = c.ThemeForeColor;
                t.Panel = c.ThemePanelColor;
                t.Editor = c.ThemeEditorColor;
            }
            catch
            {
                t.Dark = false;
                t.Back = SystemColors.Control;
                t.Fore = SystemColors.ControlText;
                t.Panel = SystemColors.Control;
                t.Editor = SystemColors.Window;
            }
            if (t.Back.A == 0) t.Back = SystemColors.Control;
            if (t.Fore.A == 0) t.Fore = SystemColors.ControlText;
            if (t.Panel.A == 0) t.Panel = t.Back;
            if (t.Editor.A == 0) t.Editor = t.Back;

            t.Dim = Mix(t.Fore, t.Back, 0.4f);   // secondary text
            t.Line = Mix(t.Fore, t.Back, 0.75f); // grid lines, borders
            return Back != oldBack || Fore != oldFore || Panel != oldPanel || Editor != oldEditor;
        }

        public static Color Mix(Color a, Color b, float wb)
        {
            float wa = 1 - wb;
            return Color.FromArgb(
                (int)Math.Round(a.R * wa + b.R * wb),
                (int)Math.Round(a.G * wa + b.G * wb),
                (int)Math.Round(a.B * wa + b.B * wb));
        }

        /// <summary>Apply the theme to a control tree (custom views paint themselves).</summary>
        public void Apply(Control root)
        {
            root.BackColor = Back;
            root.ForeColor = Fore;
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case TextBox or ComboBox or NumericUpDown or ListView:
                        c.BackColor = Editor;
                        c.ForeColor = Fore;
                        break;
                    case ButtonBase b when b.FlatStyle == FlatStyle.Flat:
                        b.FlatAppearance.BorderColor = Line;
                        b.BackColor = Back;
                        b.ForeColor = Fore;
                        break;
                    case ViewBase:
                        break;
                    default:
                        Apply(c);
                        break;
                }
            }
        }
    }
}

namespace SDRSharp.M17.UI
{
    /// <summary>
    /// Scrollbars are drawn by Windows, not by us. On Windows 10 1809+ the "DarkMode_Explorer"
    /// visual style gives them dark colours; elsewhere the call fails quietly and nothing changes.
    /// </summary>
    static class NativeScrollTheme
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void Set(Control c, bool dark)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;
            void apply()
            {
                try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
                catch (Exception) { }   // older Windows, or no uxtheme
            }
            if (c.IsHandleCreated)
                apply();
            else
                c.HandleCreated += (o, e) => apply();
        }
    }
}
