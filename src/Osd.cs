// On-screen indicator for the special keys (microphone mute, keyboard backlight, Fn+Q mode, Caps Lock):
// a rounded pill above the taskbar drawn with per-pixel alpha (layered window), that slides in, animates
// the level bar and fades out. It never takes focus and lets clicks through.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapControl
{
    enum OsdIcon { Mic, Backlight, Mode, Caps, Num, Battery }

    sealed class Osd : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT dst, ref SZ size, IntPtr hdcSrc, ref PT src, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();

        const int WsExLayered = 0x80000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExNoActivate = 0x8000000;
        const int ShowNoActivate = 4, UlwAlpha = 2;

        readonly Timer anim = new Timer { Interval = 15 };
        Func<Theme> theme;
        OsdIcon icon;
        string title = "", sub = "";
        bool on;
        int steps;                     // segments in the level bar, 0 = no bar
        float level, shownLevel;       // 0..steps
        DateTime start, changed, hideAt;
        bool visible;
        float alpha, offset;

        const int AppearMs = 220, FadeMs = 260, HoldMs = 1700;

        public Osd(Func<Theme> theme)
        {
            this.theme = theme;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            anim.Tick += delegate { Step(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExTopmost | WsExNoActivate;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        float S { get { return GetDpiForSystem() / 96f; } }
        int Px(float v) { return (int)Math.Round(v * S); }

        // level < 0: no bar. Calling again while visible updates the content and restarts the hold time.
        public void Show(OsdIcon icon, string title, string sub, bool on, int level, int steps)
        {
            bool same = visible && this.icon == icon;
            this.icon = icon; this.title = title; this.sub = sub; this.on = on;
            this.steps = level < 0 ? 0 : steps;
            this.level = Math.Max(0, level);
            if (!same) shownLevel = this.level;
            var now = DateTime.Now;
            if (!visible) { start = now; visible = true; }
            else if (alpha < 1) start = now.AddMilliseconds(-AppearMs * alpha);   // fading out: come back from the current opacity
            changed = now;
            hideAt = now.AddMilliseconds(AppearMs + HoldMs);
            if (!IsHandleCreated) CreateHandle();
            ShowWindow(Handle, ShowNoActivate);
            anim.Start();
            Step();
        }

        static float EaseOut(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (1 - t) * (1 - t) * (1 - t); }

        void Step()
        {
            var now = DateTime.Now;
            float tIn = (float)(now - start).TotalMilliseconds / AppearMs;
            float tOut = (float)(now - hideAt).TotalMilliseconds / FadeMs;
            if (tOut >= 1) { anim.Stop(); visible = false; ShowWindow(Handle, 0); return; }
            float a = EaseOut(tIn);
            if (tOut > 0) a = Math.Min(a, 1 - EaseOut(tOut));
            alpha = a;
            offset = (1 - a) * Px(18);
            shownLevel += (level - shownLevel) * 0.25f;
            if (Math.Abs(level - shownLevel) < 0.01f) shownLevel = level;
            Render();
        }

        void Render()
        {
            var T = theme();
            int w = Px(340), h = Px(76), pad = Px(14);
            var wa = Screen.PrimaryScreen.WorkingArea;
            int x = wa.Left + (wa.Width - w) / 2, y = wa.Bottom - h - Px(40) + (int)offset;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    var box = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
                    using (var path = Round(box, h / 2f))
                    {
                        using (var b = new SolidBrush(Color.FromArgb(244, T.Surface))) g.FillPath(b, path);
                        using (var p = new Pen(T.LineStrong, Math.Max(1, S))) g.DrawPath(p, path);
                    }
                    // icon disc, with a ring that expands for 400 ms after each change
                    float d = h - pad * 2, cx = pad + d / 2, cy = h / 2f;
                    float pulse = (float)(DateTime.Now - changed).TotalMilliseconds / 400f;
                    if (pulse < 1)
                        using (var rp = new Pen(Color.FromArgb((int)(120 * (1 - pulse)), T.Accent), Px(2)))
                        { float rr = d / 2 + Px(7) * EaseOut(pulse); g.DrawEllipse(rp, cx - rr, cy - rr, rr * 2, rr * 2); }
                    using (var b = new SolidBrush(on ? T.Accent : T.Band)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
                    DrawIcon(g, new RectangleF(cx - d * 0.3f, cy - d * 0.3f, d * 0.6f, d * 0.6f), on ? T.OnAccent : T.Text, T);
                    float tx = pad + d + Px(14);
                    using (var f = new Font("Segoe UI Variable Text Semibold", Px(16), GraphicsUnit.Pixel))
                    using (var fs = new Font("Cascadia Mono SemiBold", Px(11.5f), GraphicsUnit.Pixel))
                    using (var bt = new SolidBrush(T.Text))
                    using (var bm = new SolidBrush(T.Muted))
                    {
                        float ty = steps > 0 ? Px(13) : Px(17);
                        g.DrawString(title, f, bt, tx, ty);
                        g.DrawString(sub.ToUpperInvariant(), fs, bm, tx + Px(1), ty + Px(23));
                    }
                    if (steps > 0)
                    {
                        float bx = tx + Px(2), bw = w - bx - pad - Px(14), by = h - Px(17), gap = Px(5);
                        float seg = (bw - gap * (steps - 1)) / steps;
                        for (int i = 0; i < steps; i++)
                        {
                            var r = new RectangleF(bx + i * (seg + gap), by, seg, Px(5));
                            using (var path = Round(r, Px(2.5f)))
                            using (var b = new SolidBrush(T.Line)) g.FillPath(b, path);
                            float fill = Math.Max(0, Math.Min(1, shownLevel - i));
                            if (fill > 0)
                                using (var path = Round(new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * fill), r.Height), Px(2.5f)))
                                using (var b = new SolidBrush(T.Accent)) g.FillPath(b, path);
                        }
                    }
                }
                Push(bmp, x, y, (byte)(255 * alpha));
            }
        }

        void DrawIcon(Graphics g, RectangleF r, Color c, Theme T)
        {
            float s = r.Width, lw = Math.Max(1.6f, s * 0.11f);
            using (var p = new Pen(c, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            {
                switch (icon)
                {
                    case OsdIcon.Mic:
                        using (var cap = Round(new RectangleF(r.X + s * 0.34f, r.Y, s * 0.32f, s * 0.58f), s * 0.16f)) g.DrawPath(p, cap);
                        g.DrawArc(p, r.X + s * 0.18f, r.Y + s * 0.2f, s * 0.64f, s * 0.56f, 0, 180);
                        g.DrawLine(p, r.X + s * 0.5f, r.Y + s * 0.76f, r.X + s * 0.5f, r.Bottom);
                        if (on) g.DrawLine(p, r.X + s * 0.08f, r.Y + s * 0.06f, r.Right - s * 0.08f, r.Bottom - s * 0.06f);
                        break;
                    case OsdIcon.Backlight:
                        using (var kb = Round(new RectangleF(r.X + s * 0.04f, r.Y + s * 0.5f, s * 0.92f, s * 0.46f), s * 0.08f)) g.DrawPath(p, kb);
                        for (int i = 0; i < 4; i++) g.FillRectangle(b, r.X + s * (0.17f + i * 0.19f), r.Y + s * 0.62f, s * 0.08f, s * 0.07f);
                        g.DrawLine(p, r.X + s * 0.3f, r.Y + s * 0.82f, r.X + s * 0.7f, r.Y + s * 0.82f);
                        // one ray per backlight step, spread over the top of the keyboard
                        int rays = (int)Math.Round(shownLevel * 3f / Math.Max(1, steps));
                        float kx = r.X + s * 0.5f, ky = r.Y + s * 0.42f;
                        for (int i = 0; i < rays; i++)
                        {
                            double ang = Math.PI * (rays == 1 ? 0.5 : 0.75 - i * 0.5 / (rays - 1));
                            g.DrawLine(p, kx + (float)Math.Cos(ang) * s * 0.16f, ky - (float)Math.Sin(ang) * s * 0.16f,
                                          kx + (float)Math.Cos(ang) * s * 0.38f, ky - (float)Math.Sin(ang) * s * 0.38f);
                        }
                        break;
                    case OsdIcon.Mode:
                        g.DrawArc(p, r.X, r.Y + s * 0.1f, s, s, 180, 180);
                        float a = (float)(Math.PI * (1 - (steps > 1 ? shownLevel - 1 : 0) / Math.Max(1, steps - 1)));
                        float mx = r.X + s * 0.5f, my = r.Y + s * 0.6f;
                        g.DrawLine(p, mx, my, mx + (float)Math.Cos(a) * s * 0.4f, my - (float)Math.Sin(a) * s * 0.4f);
                        g.FillEllipse(b, mx - lw * 1.2f, my - lw * 1.2f, lw * 2.4f, lw * 2.4f);
                        break;
                    case OsdIcon.Battery:
                        // outline, terminal on the right, fill that follows the animated level
                        var body = new RectangleF(r.X, r.Y + s * 0.24f, s * 0.86f, s * 0.52f);
                        using (var bp = Round(body, s * 0.1f)) g.DrawPath(p, bp);
                        g.DrawLine(p, body.Right + s * 0.08f, body.Y + body.Height * 0.32f, body.Right + s * 0.08f, body.Bottom - body.Height * 0.32f);
                        float fr = steps > 0 ? Math.Max(0, Math.Min(1, shownLevel / steps)) : 1;
                        float inset = lw * 1.4f;
                        if (fr > 0)
                            using (var fp = Round(new RectangleF(body.X + inset, body.Y + inset, (body.Width - inset * 2) * fr, body.Height - inset * 2), s * 0.05f)) g.FillPath(b, fp);
                        break;
                    case OsdIcon.Caps:
                    case OsdIcon.Num:
                        using (var f = new Font("Segoe UI Variable Display", s * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(icon == OsdIcon.Caps ? "A" : "1", f, b, new RectangleF(r.X, r.Y + s * 0.12f, s, s * 0.9f), sf);
                        if (icon == OsdIcon.Caps)
                        {
                            g.DrawLine(p, r.X + s * 0.5f, r.Y - s * 0.02f, r.X + s * 0.32f, r.Y + s * 0.14f);
                            g.DrawLine(p, r.X + s * 0.5f, r.Y - s * 0.02f, r.X + s * 0.68f, r.Y + s * 0.14f);
                        }
                        break;
                }
            }
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            var p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Push(Bitmap bmp, int x, int y, byte a)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
            try
            {
                var dst = new PT { X = x, Y = y };
                var size = new SZ { W = bmp.Width, H = bmp.Height };
                var src = new PT();
                var blend = new BLEND { Op = 0, Flags = 0, Alpha = a, Format = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, UlwAlpha);
            }
            finally { SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }
    }
}
