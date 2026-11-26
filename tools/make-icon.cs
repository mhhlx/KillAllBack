// Build-time icon generator for KillAllBack (Windows-native flat edition).
//
// Style = a Windows 11 style flat rounded tile + the real Segoe Fluent Icons
// power glyph (U+E7E8), i.e. the same shape Windows uses for its own power
// button. Glyphs are converted to paths and ink-centred, so they stay crisp
// at every size.
//
// Usage: make-icon.exe app.ico preview-256.png preview-strip.png [style] [sheet]
//   style: tile-blue (default) | tile-forest | tile-graphite | tile-light | mono-dark
//   sheet: optional path for a 4-style comparison sheet
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

class MakeIcon
{
    static readonly string GlyphPower = "\uE7E8";
    static readonly string GlyphFont = "Segoe Fluent Icons";

    class Style
    {
        public string Name;
        public Color Tile;
        public Color TileTo;         // 非空 = 渐变瓦片（Tile → TileTo，135°）
        public bool HasGradient;
        public Color Ink;
        public bool HasTile;
        public float GlyphHeight;   // relative to the 256 grid
    }

    static Style GetStyle(string name)
    {
        Style s = new Style();
        s.Name = name;
        s.HasTile = true;
        s.GlyphHeight = 132f;
        switch (name)
        {
            case "tile-forest":
                s.Tile = Color.FromArgb(255, 0x27, 0x4B, 0x3C);
                s.TileTo = Color.FromArgb(255, 0x15, 0x2D, 0x25);
                s.HasGradient = true;
                s.Ink = Color.White;
                break;
            case "tile-graphite":
                s.Tile = Color.FromArgb(255, 60, 65, 74);
                s.Ink = Color.White;
                break;
            case "tile-light":
                s.Tile = Color.FromArgb(255, 0xDD, 0xFD, 0xE8);   // 绿白
                s.Ink = Color.FromArgb(255, 0x01, 0x2B, 0xAF);    // 飞燕草蓝
                break;
            case "mono-dark":
                s.HasTile = false;
                s.GlyphHeight = 224f;
                s.Ink = Color.FromArgb(255, 32, 34, 38);
                break;
            default: // tile-blue —— 案 A「电蓝电源」：克莱因蓝渐变瓦片 + 白电源键
                s.Name = "tile-blue";
                s.Tile = Color.FromArgb(255, 0x1A, 0x3F, 0xD4);   // 克莱因蓝（亮）
                s.TileTo = Color.FromArgb(255, 0x00, 0x2F, 0xA7); // 克莱因蓝（主）
                s.HasGradient = true;
                s.Ink = Color.White;
                break;
        }
        return s;
    }

    static void Main(string[] args)
    {
        string icoPath = args.Length > 0 ? args[0] : "app.ico";
        string preview256 = args.Length > 1 ? args[1] : null;
        string previewStrip = args.Length > 2 ? args[2] : null;
        string styleName = args.Length > 3 ? args[3] : "tile-blue";
        string sheetPath = args.Length > 4 ? args[4] : null;

        Style style = GetStyle(styleName);
        int master = 1024;

        Bitmap source = Render(master, style, false);
        Bitmap simple = Render(master, style, true);

        List<IconEntry> entries = new List<IconEntry>();
        int[] small = new int[] { 16, 24 };
        int[] dib = new int[] { 32, 48, 64 };
        int[] png = new int[] { 128, 256 };
        foreach (int s in small) { using (Bitmap b = Resize(simple, s)) { entries.Add(new IconEntry(s, MakeDib(b))); } }
        foreach (int s in dib) { using (Bitmap b = Resize(source, s)) { entries.Add(new IconEntry(s, MakeDib(b))); } }
        foreach (int s in png) { using (Bitmap b = Resize(source, s)) { entries.Add(new IconEntry(s, MakePng(b))); } }
        WriteIco(icoPath, entries);

        if (preview256 != null) { using (Bitmap b = Resize(source, 256)) { b.Save(preview256, ImageFormat.Png); } }
        if (previewStrip != null) { MakeStrip(source, simple, previewStrip); }
        if (sheetPath != null) { MakeStyleSheet(sheetPath, styleName); }

        source.Dispose();
        simple.Dispose();
        Console.WriteLine("icon written: " + icoPath + " (style=" + style.Name + ")");
    }

    static Bitmap Render(int size, Style style, bool simple)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            DrawEmblem(g, size / 256f, style, simple);
        }
        return bmp;
    }

    // Windows 11 tiles keep a hair of margin; the glyph fills the tile centre.
    static void DrawEmblem(Graphics g, float s, Style style, bool simple)
    {
        float cx = 128f;
        float cy = 128f;

        if (style.HasTile)
        {
            float margin = 4f;
            RectangleF tile = new RectangleF(margin * s, margin * s, (256f - margin * 2) * s, (256f - margin * 2) * s);
            float radius = 56f * s;
            using (GraphicsPath path = RoundedRect(tile, radius))
            {
                if (style.HasGradient)
                {
                    // 与界面首卡同一支克莱因蓝渐变：135° 亮 → 主
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(tile.X, tile.Y, tile.Width, tile.Height + 1), style.Tile, style.TileTo, 135F))
                    {
                        g.FillPath(lg, path);
                    }
                    using (Pen hi = new Pen(Color.FromArgb(28, 255, 255, 255), Math.Max(1f, s)))
                    using (GraphicsPath inner = RoundedRect(new RectangleF(tile.X + s, tile.Y + s,
                        tile.Width - 2 * s, tile.Height - 2 * s), radius - s))
                    {
                        g.DrawPath(hi, inner);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(style.Tile))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }
        }

        float glyphHeight = style.GlyphHeight;
        if (simple) { glyphHeight *= style.HasTile ? 1.10f : 1.0f; }
        float nudge = style.HasTile ? 1f : 0f;   // optical centring for the power glyph
        DrawGlyph(g, s, cx, cy + nudge, glyphHeight, GlyphPower, style.Ink);
    }

    // Converts the font glyph to a path so it can be ink-centred and scaled exactly.
    static void DrawGlyph(Graphics g, float s, float cx, float cy, float targetHeight, string glyph, Color color)
    {
        using (FontFamily family = new FontFamily(GlyphFont))
        using (GraphicsPath path = new GraphicsPath())
        {
            path.AddString(glyph, family, (int)FontStyle.Regular, 200f, new PointF(0f, 0f), StringFormat.GenericTypographic);
            path.Flatten(null, 0.15f);          // tight bounds (curves -> segments)
            RectangleF ink = path.GetBounds();
            if (ink.Height <= 0f || ink.Width <= 0f) { return; }

            float scale = (targetHeight * s) / ink.Height;
            using (Matrix m = new Matrix())
            {
                m.Translate(-ink.X - ink.Width / 2f, -ink.Y - ink.Height / 2f, MatrixOrder.Append);
                m.Scale(scale, scale, MatrixOrder.Append);
                m.Translate(cx * s, cy * s, MatrixOrder.Append);
                path.Transform(m);
            }
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    class IconEntry
    {
        public int Size;
        public byte[] Data;
        public IconEntry(int size, byte[] data) { Size = size; Data = data; }
    }

    static Bitmap Resize(Bitmap src, int size)
    {
        Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        dst.SetResolution(96, 96);
        using (Graphics g = Graphics.FromImage(dst))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, size, size));
        }
        return dst;
    }

    static void MakeStrip(Bitmap source, Bitmap simple, string outPath)
    {
        int w = 430, h = 300;
        using (Bitmap strip = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(strip))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.FromArgb(255, 245, 246, 248));
                using (Font f = new Font("Segoe UI", 11f))
                using (SolidBrush txt = new SolidBrush(Color.FromArgb(255, 80, 86, 94)))
                {
                    using (Bitmap big = Resize(source, 256)) { g.DrawImage(big, 12, 16, 256, 256); }
                    g.DrawString("256 px", f, txt, 284, 16);
                    int[] sizes = new int[] { 48, 32, 24, 16 };
                    int y = 60;
                    foreach (int s in sizes)
                    {
                        Bitmap from = s <= 24 ? simple : source;
                        using (Bitmap b = Resize(from, s)) { g.DrawImage(b, 284, y, s, s); }
                        g.DrawString(s + " px", f, txt, 284 + s + 12, y + Math.Max(0, (s - 16) / 2));
                        y += Math.Max(s, 32) + 20;
                    }
                }
            }
            strip.Save(outPath, ImageFormat.Png);
        }
    }

    static void MakeStyleSheet(string outPath, string selected)
    {
        string[] names = new string[] { "tile-forest", "tile-blue", "tile-graphite", "tile-light", "mono-dark" };
        int cell = 132, pad = 14, header = 34;
        using (Bitmap sheet = new Bitmap(names.Length * cell + pad, cell + header + pad, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.FromArgb(255, 245, 246, 248));
                using (Font f = new Font("Segoe UI", 9f))
                using (SolidBrush txt = new SolidBrush(Color.FromArgb(255, 70, 76, 84)))
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        Style st = GetStyle(names[i]);
                        using (Bitmap master = Render(512, st, false))
                        using (Bitmap small = Resize(master, 104))
                        {
                            int x = pad / 2 + i * cell + 12;
                            g.DrawImage(small, x, header);
                        }
                        string label = names[i] + (names[i] == selected ? "  *" : "");
                        g.DrawString(label, f, txt, pad / 2 + i * cell + 14, 12);
                    }
                }
            }
            sheet.Save(outPath, ImageFormat.Png);
        }
    }

    static byte[] MakePng(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] MakeDib(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int stride = w * 4;
        int maskStride = ((w + 31) / 32) * 4;
        int imageSize = stride * h;
        int maskSize = maskStride * h;

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);
            bw.Write(imageSize + maskSize);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[stride];
                for (int y = h - 1; y >= 0; y--)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                    bw.Write(row);
                }
            }
            finally { bmp.UnlockBits(data); }

            byte[] zeros = new byte[maskStride];
            for (int y = 0; y < h; y++) { bw.Write(zeros); }
            return ms.ToArray();
        }
    }

    static void WriteIco(string path, List<IconEntry> entries)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)entries.Count);

            int offset = 6 + entries.Count * 16;
            foreach (IconEntry e in entries)
            {
                bw.Write((byte)(e.Size >= 256 ? 0 : e.Size));
                bw.Write((byte)(e.Size >= 256 ? 0 : e.Size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(e.Data.Length);
                bw.Write(offset);
                offset += e.Data.Length;
            }
            foreach (IconEntry e in entries)
            {
                bw.Write(e.Data);
            }
        }
    }
}
