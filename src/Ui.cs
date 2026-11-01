// "Technical Minimal" interface (Nothing / Teenage Engineering), modeled on
// github.com/mich-de/technical-minimal-web_2.0: paper, thin lines, dot grid,
// orange LED, numbered mono labels, dot-matrix digits.
// All drawn in GDI+/GDI, with no continuous animation: the GPU stays idle.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapControl
{
    sealed class Theme
    {
        public string Key, Name;
        public Color Bg, Surface, Line, LineStrong, Text, Muted, Accent, AccentInk, OnAccent, Dot, Band;
        public bool Dark;

        static Color C(string hex) { return ColorTranslator.FromHtml(hex); }

        // Same values as the :root / [data-theme] tokens of the web theme.
        public static readonly Theme[] All =
        {
            new Theme { Key = "light", Name = "LIGHT", Bg = C("#f3f2ef"), Surface = C("#fbfaf8"), Line = C("#d9d6d0"), LineStrong = C("#bdb9b1"),
                        Text = C("#141413"), Muted = C("#5f5b55"), Accent = C("#e5481a"), AccentInk = C("#b3360f"), OnAccent = C("#141413"), Dot = C("#cfcbc4"), Band = C("#ecebe6") },
            new Theme { Key = "dark", Name = "DARK", Dark = true, Bg = C("#0e0e0e"), Surface = C("#151515"), Line = C("#262626"), LineStrong = C("#3a3a3a"),
                        Text = C("#ecebe8"), Muted = C("#9a968f"), Accent = C("#ff5a1f"), AccentInk = C("#ff7a45"), OnAccent = C("#0e0e0e"), Dot = C("#2b2b2b"), Band = C("#131313") },
            new Theme { Key = "cactus", Name = "CACTUS", Dark = true, Bg = C("#0a0e0b"), Surface = C("#101512"), Line = C("#1f2a22"), LineStrong = C("#314136"),
                        Text = C("#e9f0e9"), Muted = C("#93a294"), Accent = C("#35d06a"), AccentInk = C("#5fe08d"), OnAccent = C("#06120a"), Dot = C("#1c2a20"), Band = C("#0d120e") },
        };

        const string RegKey = @"Software\SnapControl";

        public static int Load()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string v = k == null ? null : k.GetValue("Theme") as string;
                    for (int i = 0; i < All.Length; i++) if (All[i].Key == v) return i;
                }
            }
            catch { }
            return 0; // like the web theme: light on first start
        }

        public static void Save(int i)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("Theme", All[i].Key); } catch { }
        }
    }

    // 5x7 digits and symbols for the dot matrix (role of the Doto font in the web theme).
    static class DotMatrix
    {
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" } },
            { '1', new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" } },
            { '2', new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" } },
            { '3', new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" } },
            { '4', new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" } },
            { '5', new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" } },
            { '6', new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" } },
            { '7', new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" } },
            { '8', new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" } },
            { '9', new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" } },
            { '%', new[] { "11000", "11001", "00010", "00100", "01000", "10011", "00011" } },
            { '.', new[] { "00000", "00000", "00000", "00000", "00000", "01100", "01100" } },
            { '-', new[] { "00000", "00000", "00000", "11111", "00000", "00000", "00000" } },
            { ' ', new[] { "00000", "00000", "00000", "00000", "00000", "00000", "00000" } },
        };

        public static SizeF Measure(string s, float pitch)
        {
            return new SizeF(s.Length * 6 * pitch - pitch, 7 * pitch);
        }

        public static void Draw(Graphics g, string s, float x, float y, float pitch, Color on, Color off)
        {
            float r = pitch * 0.38f;
            using (var bOn = new SolidBrush(on))
            using (var bOff = new SolidBrush(off))
                foreach (char ch in s)
                {
                    string[] rows;
                    if (!Glyphs.TryGetValue(ch, out rows)) rows = Glyphs[' '];
                    for (int ry = 0; ry < 7; ry++)
                        for (int cx = 0; cx < 5; cx++)
                        {
                            float px = x + cx * pitch + pitch / 2, py = y + ry * pitch + pitch / 2;
                            g.FillEllipse(rows[ry][cx] == '1' ? bOn : bOff, px - r, py - r, r * 2, r * 2);
                        }
                    x += 6 * pitch;
                }
        }
    }

    static class Dwm
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Dark/light title bar matching the theme (Windows 11).
        public static void TitleBar(IntPtr hwnd, Theme t)
        {
            int dark = t.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);                                // DWMWA_USE_IMMERSIVE_DARK_MODE
            int cap = t.Bg.R | (t.Bg.G << 8) | (t.Bg.B << 16);
            DwmSetWindowAttribute(hwnd, 35, ref cap, 4);                                 // DWMWA_CAPTION_COLOR
            int txt = t.Text.R | (t.Text.G << 8) | (t.Text.B << 16);
            DwmSetWindowAttribute(hwnd, 36, ref txt, 4);                                 // DWMWA_TEXT_COLOR
        }
    }
}
