using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WeTypeSkinStudio
{
    public sealed class SkinRenderer : IDisposable
    {
        private string imageData;
        private Bitmap image;
        private Bitmap scaledImage;
        private Size scaledSize;
        private double scaledX, scaledY, scaledZoom;

        private void PrepareImage(SkinTheme theme, Size size)
        {
            if (imageData != theme.BackgroundImageData)
            {
                if (image != null) image.Dispose();
                if (scaledImage != null) scaledImage.Dispose();
                image = null; scaledImage = null;
                imageData = theme.BackgroundImageData;
                if (!string.IsNullOrEmpty(imageData))
                    using (var stream = new System.IO.MemoryStream(Convert.FromBase64String(imageData)))
                    using (var loaded = Image.FromStream(stream)) image = new Bitmap(loaded);
            }
            if (image == null) return;
            if (scaledImage != null && scaledSize == size && scaledX == theme.ImagePositionX && scaledY == theme.ImagePositionY && scaledZoom == theme.ImageZoom) return;
            if (scaledImage != null) scaledImage.Dispose();
            scaledImage = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            scaledSize = size;
            scaledX = theme.ImagePositionX; scaledY = theme.ImagePositionY; scaledZoom = theme.ImageZoom;
            using (var g = Graphics.FromImage(scaledImage))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, theme.ImageBounds(image.Size, size));
            }
        }

        public Bitmap Render(Bitmap source, Bitmap backdrop, SkinTheme theme)
        {
            if (source == null || backdrop == null || source.Size != backdrop.Size) throw new ArgumentException("候选框与背景尺寸不一致。");
            PrepareImage(theme, source.Size);
            int w = source.Width, h = source.Height;
            byte[] src = ReadPixels(source), behind = ReadPixels(backdrop);
            byte[] photo = scaledImage == null ? null : ReadPixels(scaledImage);
            byte[] result = new byte[w * h * 4];
            Color bg = SkinTheme.ParseColor(theme.Background), fg = SkinTheme.ParseColor(theme.Foreground);
            Color accent = SkinTheme.ParseColor(theme.Accent), border = SkinTheme.ParseColor(theme.Border);
            Color original = FindBackground(src, w, h);
            double originalLuma = Luma(original.R, original.G, original.B);
            bool dark = originalLuma < 128;
            double radius = Math.Min(theme.CornerRadius, Math.Min(w, h) / 2.0);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double coverage = RoundedCoverage(x, y, w, h, radius);
                if (coverage <= 0) continue;
                double br = bg.R, bgreen = bg.G, bb = bg.B;
                if (photo != null)
                {
                    double tint = theme.ImageTint;
                    double imageAlpha = photo[i + 3] / 255.0;
                    br = bg.R * (1 - imageAlpha) + photo[i + 2] * imageAlpha;
                    bgreen = bg.G * (1 - imageAlpha) + photo[i + 1] * imageAlpha;
                    bb = bg.B * (1 - imageAlpha) + photo[i] * imageAlpha;
                    br = br * (1 - tint) + bg.R * tint;
                    bgreen = bgreen * (1 - tint) + bg.G * tint;
                    bb = bb * (1 - tint) + bg.B * tint;
                }
                br = br * theme.Opacity + behind[i + 2] * (1 - theme.Opacity);
                bgreen = bgreen * theme.Opacity + behind[i + 1] * (1 - theme.Opacity);
                bb = bb * theme.Opacity + behind[i] * (1 - theme.Opacity);
                int sr = src[i + 2], sg = src[i + 1], sb = src[i];
                int saturation = Math.Max(sr, Math.Max(sg, sb)) - Math.Min(sr, Math.Min(sg, sb));
                double luma = Luma(sr, sg, sb);
                double textAlpha = Clamp(dark ? (luma - originalLuma) / Math.Max(1, 255 - originalLuma) : (originalLuma - luma) / Math.Max(1, originalLuma));
                double rr = br, gg = bgreen, blue = bb;
                if (saturation <= 30)
                {
                    rr = br * (1 - textAlpha) + fg.R * textAlpha;
                    gg = bgreen * (1 - textAlpha) + fg.G * textAlpha;
                    blue = bb * (1 - textAlpha) + fg.B * textAlpha;
                }
                else if (sg > sr + 20 && sg > sb + 5)
                {
                    double highlightAlpha = Clamp((sg - Math.Min(sr, sb)) / 90.0);
                    rr = br * (1 - highlightAlpha) + accent.R * highlightAlpha;
                    gg = bgreen * (1 - highlightAlpha) + accent.G * highlightAlpha;
                    blue = bb * (1 - highlightAlpha) + accent.B * highlightAlpha;
                }
                else
                {
                    // Preserve colorful emoji and icons instead of turning them into text.
                    rr = sr; gg = sg; blue = sb;
                }
                double inner = RoundedCoverage(x - 1, y - 1, w - 2, h - 2, Math.Max(0, radius - 1));
                double stroke = Clamp(coverage - inner);
                rr = rr * (1 - stroke) + border.R * stroke;
                gg = gg * (1 - stroke) + border.G * stroke;
                blue = blue * (1 - stroke) + border.B * stroke;
                // The preview bitmap uses premultiplied BGRA.
                result[i] = (byte)Math.Round(Clamp255(blue) * coverage);
                result[i + 1] = (byte)Math.Round(Clamp255(gg) * coverage);
                result[i + 2] = (byte)Math.Round(Clamp255(rr) * coverage);
                result[i + 3] = (byte)Math.Round(255 * coverage);
            }
            return WritePixels(result, w, h);
        }

        private static Color FindBackground(byte[] pixels, int w, int h)
        {
            int[] counts = new int[4096];
            long[] r = new long[4096], g = new long[4096], b = new long[4096];
            int best = 0;
            for (int y = Math.Min(3, h / 4); y < h - Math.Min(3, h / 4); y += 2)
            for (int x = Math.Min(6, w / 4); x < w - Math.Min(6, w / 4); x += 2)
            {
                int i = (y * w + x) * 4;
                int key = (pixels[i + 2] / 16) * 256 + (pixels[i + 1] / 16) * 16 + pixels[i] / 16;
                counts[key]++; r[key] += pixels[i + 2]; g[key] += pixels[i + 1]; b[key] += pixels[i];
                if (counts[key] > counts[best]) best = key;
            }
            if (counts[best] == 0) return Color.White;
            return Color.FromArgb((int)(r[best] / counts[best]), (int)(g[best] / counts[best]), (int)(b[best] / counts[best]));
        }

        private static double RoundedCoverage(double x, double y, int w, int h, double r)
        {
            if (w <= 0 || h <= 0 || x < 0 || y < 0 || x >= w || y >= h) return 0;
            if (r <= 0) return 1;
            double cx = Math.Max(r, Math.Min(w - r, x + 0.5));
            double cy = Math.Max(r, Math.Min(h - r, y + 0.5));
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            return Clamp(r + 0.5 - Math.Sqrt(dx * dx + dy * dy));
        }

        private static double Luma(int r, int g, int b) { return r * 0.2126 + g * 0.7152 + b * 0.0722; }
        private static double Clamp(double x) { return Math.Max(0, Math.Min(1, x)); }
        private static double Clamp255(double x) { return Math.Max(0, Math.Min(255, x)); }

        public static byte[] ReadPixels(Bitmap bmp)
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] result = new byte[bmp.Width * bmp.Height * 4];
                for (int y = 0; y < bmp.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), result, y * bmp.Width * 4, bmp.Width * 4);
                return result;
            }
            finally { bmp.UnlockBits(data); }
        }

        public static Bitmap WritePixels(byte[] pixels, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, IntPtr.Add(data.Scan0, y * data.Stride), w * 4); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public static Bitmap MakeSample(Size size)
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float scale = size.Width / 720f;
                using (var small = new Font("Microsoft YaHei UI", 12 * scale))
                using (var font = new Font("Microsoft YaHei UI", 17 * scale))
                using (var green = new SolidBrush(Color.FromArgb(18, 157, 91)))
                {
                    g.DrawString("ni hao", small, Brushes.DimGray, 24 * scale, 13 * scale);
                    string[] words = { "1  你好", "2  拟好", "3  你号", "4  霓虹" };
                    for (int i = 0; i < words.Length; i++) g.DrawString(words[i], font, i == 0 ? green : Brushes.Black, (24 + i * 164) * scale, 53 * scale);
                }
            }
            return bmp;
        }

        public static Bitmap MakeBackdrop(Size size)
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                for (int y = 0; y < size.Height; y += 20)
                for (int x = 0; x < size.Width; x += 20)
                    using (var brush = new SolidBrush((x / 20 + y / 20) % 2 == 0 ? Color.FromArgb(228, 235, 231) : Color.FromArgb(245, 248, 246)))
                        g.FillRectangle(brush, x, y, 20, 20);
            return bmp;
        }

        public void Dispose()
        {
            if (image != null) image.Dispose();
            if (scaledImage != null) scaledImage.Dispose();
        }
    }
}
