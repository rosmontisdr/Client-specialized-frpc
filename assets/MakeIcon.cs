// 由源图生成多尺寸 app.ico：抠掉与画面边缘相连的黑底，再逐级折半缩放
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class MakeIcon
{
    const int FillThreshold = 8;    // 视为"黑底"的亮度上限
    const int EdgeLo = 6;           // 边缘羽化：亮度下限（更暗 = 全透明）
    const int EdgeHi = 44;          // 边缘羽化：亮度上限（更亮 = 全不透明）

    static readonly Dictionary<int, Bitmap> Chain = new Dictionary<int, Bitmap>();

    static void Main(string[] args)
    {
        string src = args[0];
        string outPath = args[1];
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

        Bitmap cut;
        using (Image img = Image.FromFile(src)) cut = CutBackground(img);

        Bitmap work = ToPremultiplied(cut);      // 预乘后再缩放，避免透明区渗黑
        cut.Dispose();

        int top = 16;
        while (top * 2 <= Math.Min(work.Width, work.Height)) top *= 2;
        Chain[top] = Scale(work, top, top);
        for (int s = top / 2; s >= 16; s /= 2) Chain[s] = Scale(Chain[s * 2], s, s);
        work.Dispose();

        List<byte[]> frames = new List<byte[]>();
        foreach (int sz in sizes) frames.Add(Dib(ToStraight(Frame(sz))));

        Write(outPath, sizes, frames);
        Console.WriteLine("wrote " + outPath + " / " + sizes.Length + " frames");
    }

    // ---- 抠黑底 ----

    static Bitmap CutBackground(Image img)
    {
        int w = img.Width, h = img.Height;
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(img, new Rectangle(0, 0, w, h));
        if (w != img.Width || h != img.Height) return bmp;   // 尺寸一致，直接返回

        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] px = new byte[stride * h];
        Marshal.Copy(d.Scan0, px, 0, px.Length);

        bool[] bg = new bool[w * h];
        int[] stack = new int[w * h];
        int sp = 0;
        for (int x = 0; x < w; x++) { Push(px, stride, bg, stack, ref sp, x, 0, w, h); Push(px, stride, bg, stack, ref sp, x, h - 1, w, h); }
        for (int y = 0; y < h; y++) { Push(px, stride, bg, stack, ref sp, 0, y, w, h); Push(px, stride, bg, stack, ref sp, w - 1, y, w, h); }
        while (sp > 0)
        {
            int i = stack[--sp];
            int x = i % w, y = i / w;
            Push(px, stride, bg, stack, ref sp, x - 1, y, w, h);
            Push(px, stride, bg, stack, ref sp, x + 1, y, w, h);
            Push(px, stride, bg, stack, ref sp, x, y - 1, w, h);
            Push(px, stride, bg, stack, ref sp, x, y + 1, w, h);
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, o = y * stride + x * 4;
                int a;
                if (bg[i]) a = 0;
                else if (TouchesBg(bg, x, y, w, h))
                {
                    // 边界像素按亮度做羽化，避免锯齿
                    int m = Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]));
                    a = (m - EdgeLo) * 255 / (EdgeHi - EdgeLo);
                    if (a < 0) a = 0;
                    if (a > 255) a = 255;
                    if (a > 0)
                    {
                        // 反预乘：这些像素原本是"叠在黑底上"的，不还原真实颜色的话，
                        // 缩小后会在边缘留一圈深色（浅色底上就是黑边）
                        for (int c = 0; c < 3; c++)
                        {
                            int v = px[o + c] * 255 / a;
                            px[o + c] = (byte)(v > 255 ? 255 : v);
                        }
                    }
                }
                else a = 255;
                px[o + 3] = (byte)a;
            }
        }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        return bmp;
    }

    static void Push(byte[] px, int stride, bool[] bg, int[] stack, ref int sp, int x, int y, int w, int h)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = y * w + x;
        if (bg[i]) return;
        int o = y * stride + x * 4;
        int m = Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]));
        if (m > FillThreshold) return;
        bg[i] = true;
        stack[sp++] = i;
    }

    static bool TouchesBg(bool[] bg, int x, int y, int w, int h)
    {
        if (x > 0 && bg[y * w + x - 1]) return true;
        if (y > 0 && bg[(y - 1) * w + x]) return true;
        if (x < w - 1 && bg[y * w + x + 1]) return true;
        if (y < h - 1 && bg[(y + 1) * w + x]) return true;
        return false;
    }

    // ---- 预乘 / 反预乘 ----

    static Bitmap ToPremultiplied(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        BitmapData ds = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        byte[] s = new byte[ds.Stride * h];
        Marshal.Copy(ds.Scan0, s, 0, s.Length);
        byte[] t = new byte[dd.Stride * h];
        for (int y = 0; y < h; y++)
        {
            int so = y * ds.Stride, to = y * dd.Stride;
            for (int x = 0; x < w; x++)
            {
                int a = s[so + x * 4 + 3];
                t[to + x * 4 + 0] = (byte)(s[so + x * 4 + 0] * a / 255);
                t[to + x * 4 + 1] = (byte)(s[so + x * 4 + 1] * a / 255);
                t[to + x * 4 + 2] = (byte)(s[so + x * 4 + 2] * a / 255);
                t[to + x * 4 + 3] = (byte)a;
            }
        }
        Marshal.Copy(t, 0, dd.Scan0, t.Length);
        src.UnlockBits(ds);
        dst.UnlockBits(dd);
        return dst;
    }

    // ICO 里的 32bpp 是直通 alpha，写盘前转回来
    static Bitmap ToStraight(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData ds = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, src.PixelFormat);
        BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] s = new byte[ds.Stride * h];
        Marshal.Copy(ds.Scan0, s, 0, s.Length);
        byte[] t = new byte[dd.Stride * h];
        for (int y = 0; y < h; y++)
        {
            int so = y * ds.Stride, to = y * dd.Stride;
            for (int x = 0; x < w; x++)
            {
                int a = s[so + x * 4 + 3];
                for (int c = 0; c < 3; c++)
                {
                    int v = a == 0 ? 0 : s[so + x * 4 + c] * 255 / a;
                    t[to + x * 4 + c] = (byte)(v > 255 ? 255 : v);
                }
                t[to + x * 4 + 3] = (byte)a;
            }
        }
        Marshal.Copy(t, 0, dd.Scan0, t.Length);
        src.UnlockBits(ds);
        dst.UnlockBits(dd);
        return dst;
    }

    // ---- 缩放与写盘 ----

    static Bitmap Frame(int size)
    {
        if (Chain.ContainsKey(size)) return Chain[size];
        int big = 16;
        while (big < size) big *= 2;
        return Scale(Chain[big], size, size);
    }

    static Bitmap Scale(Image img, int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(img, new Rectangle(0, 0, w, h));
        }
        return bmp;
    }

    static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] raw = new byte[d.Stride * h];
        Marshal.Copy(d.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(d);

        int xor = w * h * 4;
        int and = ((w + 31) / 32) * 4 * h;
        byte[] buf = new byte[40 + xor + and];
        using (MemoryStream ms = new MemoryStream(buf))
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);
            bw.Write(xor + and);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
        }
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(raw, (h - 1 - y) * d.Stride, buf, 40 + y * w * 4, w * 4);
        return buf;
    }

    static void Write(string outPath, int[] sizes, List<byte[]> frames)
    {
        using (FileStream fs = File.Create(outPath))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] f in frames) w.Write(f);
        }
    }
}
