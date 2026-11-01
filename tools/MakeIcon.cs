// Generates src\app.ico: dark tile with a dot-matrix "S" in the theme LED color.
// Usage: MakeIcon.exe <app.ico> [preview.png]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly Color Tile = ColorTranslator.FromHtml("#141413");
    static readonly Color Edge = ColorTranslator.FromHtml("#2e2e2c");
    static readonly Color On = ColorTranslator.FromHtml("#ff5a1f");
    static readonly Color Off = ColorTranslator.FromHtml("#2b2b2b");

    // 5x7 for large sizes, 3x5 below 32 px where small dots blur together.
    static readonly string[] Big = { "01111", "10000", "10000", "01110", "00001", "00001", "11110" };
    static readonly string[] Small = { "111", "100", "111", "001", "111" };

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float m = s <= 24 ? 0 : s * 0.05f;
            var tile = new RectangleF(m, m, s - 2 * m, s - 2 * m);
            using (var path = Rounded(tile, tile.Width * 0.22f))
            {
                using (var b = new SolidBrush(Tile)) g.FillPath(b, path);
                if (s >= 48) using (var p = new Pen(Edge, Math.Max(1f, s / 128f))) g.DrawPath(p, path);
            }

            bool small = s < 32;
            string[] glyph = small ? Small : Big;
            int cols = glyph[0].Length, rows = glyph.Length;
            float pitch = tile.Height * (small ? 0.62f : 0.66f) / rows;
            float gx = tile.X + (tile.Width - cols * pitch) / 2, gy = tile.Y + (tile.Height - rows * pitch) / 2;
            float r = pitch * (small ? 0.46f : 0.38f);
            using (var on = new SolidBrush(On))
            using (var off = new SolidBrush(Off))
            using (var glow = new SolidBrush(Color.FromArgb(60, On)))
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        float cx = gx + x * pitch + pitch / 2, cy = gy + y * pitch + pitch / 2;
                        bool lit = glyph[y][x] == '1';
                        if (lit && s >= 48) g.FillEllipse(glow, cx - r * 1.6f, cy - r * 1.6f, r * 3.2f, r * 3.2f);
                        if (lit) g.FillEllipse(on, cx - r, cy - r, r * 2, r * 2);
                        else if (!small) g.FillEllipse(off, cx - r, cy - r, r * 2, r * 2);
                    }
        }
        return bmp;
    }

    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var b = Render(s))
            using (var ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }

        // ICO with PNG entries (supported since Windows Vista).
        using (var f = new BinaryWriter(File.Create(args[0])))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)0); f.Write((byte)0);
                f.Write((short)1); f.Write((short)32);
                f.Write(pngs[i].Length); f.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) f.Write(p);
        }

        if (args.Length > 1)
        {
            // preview: all sizes side by side on light and dark backgrounds
            int w = 0;
            foreach (int s in sizes) w += s + 16;
            using (var prev = new Bitmap(w + 16, 2 * (256 + 32)))
            using (var g = Graphics.FromImage(prev))
            {
                g.FillRectangle(new SolidBrush(ColorTranslator.FromHtml("#f3f2ef")), 0, 0, prev.Width, 288);
                g.FillRectangle(new SolidBrush(ColorTranslator.FromHtml("#202020")), 0, 288, prev.Width, 288);
                int x = 16;
                foreach (int s in sizes)
                    using (var b = Render(s))
                    {
                        g.DrawImageUnscaled(b, x, 16 + 256 - s);
                        g.DrawImageUnscaled(b, x, 288 + 16 + 256 - s);
                        x += s + 16;
                    }
                prev.Save(args[1], ImageFormat.Png);
            }
        }
    }
}
