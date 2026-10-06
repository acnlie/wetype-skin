using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace WeTypeSkinStudio
{
    internal sealed class PetToolbarModes
    {
        internal IntPtr Target;
        internal Rectangle Bounds;
        internal bool? FullWidth, ChinesePunctuation;
    }
    // Only native mode icons are inspected. No candidate or user text is read.
    internal static class PetToolbarProbe
    {
        internal static PetToolbarModes ReadModes(IntPtr toolbar, Rectangle bounds, uint dpi)
        {
            var modes = new PetToolbarModes { Target = toolbar, Bounds = bounds };
            try
            {
                modes.FullWidth = Read(toolbar, bounds, dpi, false);
                modes.ChinesePunctuation = Read(toolbar, bounds, dpi, true);
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (System.Runtime.InteropServices.ExternalException) { }
            return modes;
        }
        internal static bool? ReadAngle(IntPtr toolbar, Rectangle bounds, uint dpi)
        {
            try { return Read(toolbar, bounds, dpi, false); }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (System.Runtime.InteropServices.ExternalException) { return null; }
        }
        private static bool? Read(IntPtr toolbar, Rectangle bounds, uint dpi, bool punctuation)
        {
            if (bounds.IsEmpty || dpi == 0) return null;
            int size = Math.Max(16, (int)Math.Round(20 * dpi / 96.0));
            var area = new Rectangle(bounds.Left + (int)Math.Round((punctuation ? 58 : 84) * dpi / 96.0),
                bounds.Top + (int)Math.Round(9 * dpi / 96.0), size, size);
            if (PetNative.GetAncestor(PetNative.WindowFromPoint(new PetNative.Point { X = area.Left + size / 2, Y = area.Top + size / 2 }), 2) != toolbar) return null;
            using (var image = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
                if (punctuation) return ClassifyPunctuation(image);
                bool[] mask = Mask(image); if (mask == null) return null;
                double half = Score(mask, Reference(false)), full = Score(mask, Reference(true));
                if (Math.Max(half, full) < 0.56 || Math.Abs(full - half) < 0.07) return null;
                return full > half;
            }
        }
        internal static bool? ClassifyPunctuation(Bitmap image)
        {
            bool[] mask = Mask(image); if (mask == null) return null;
            double chinese = Score(mask, PunctuationReference(true)), english = Score(mask, PunctuationReference(false));
            if (Math.Max(chinese, english) < 0.55 || Math.Abs(chinese - english) < 0.10) return null;
            return chinese > english;
        }
        private static bool[] chinesePunctuationReference, englishPunctuationReference;
        private static bool[] PunctuationReference(bool chinese)
        {
            bool[] reference = chinese ? chinesePunctuationReference : englishPunctuationReference;
            if (reference != null) return reference;
            using (var image = new Bitmap(80, 80))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White); graphics.ScaleTransform(4, 4); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                if (chinese)
                {
                    using (var ring = new GraphicsPath(FillMode.Alternate))
                    {
                        ring.AddEllipse(5.171f, 8.143f, 3.5f, 3.5f); ring.AddEllipse(5.757f, 8.728f, 2.329f, 2.329f);
                        graphics.FillPath(Brushes.Black, ring);
                    }
                    using (var comma = new GraphicsPath())
                    {
                        comma.AddBezier(12.221f, 13.900f, 13.953f, 13.290f, 15.074f, 11.937f, 15.074f, 10.156f);
                        comma.AddBezier(15.074f, 10.156f, 15.074f, 9.001f, 14.579f, 8.259f, 13.672f, 8.259f);
                        comma.AddBezier(13.672f, 8.259f, 12.996f, 8.259f, 12.419f, 8.671f, 12.419f, 9.446f);
                        comma.AddBezier(12.419f, 9.446f, 12.419f, 10.222f, 12.980f, 10.618f, 13.656f, 10.618f);
                        comma.AddLine(13.656f, 10.618f, 13.936f, 10.601f);
                        comma.AddBezier(13.936f, 10.601f, 13.854f, 11.723f, 13.128f, 12.498f, 11.858f, 13.026f);
                        comma.CloseFigure(); graphics.FillPath(Brushes.Black, comma);
                    }
                }
                else
                {
                    graphics.FillEllipse(Brushes.Black, 5.357f, 8.143f, 2.379f, 2.379f);
                    graphics.FillPolygon(Brushes.Black, new[] { new PointF(13.169f, 12.504f), new PointF(14.524f, 8.143f), new PointF(12.610f, 8.143f), new PointF(11.649f, 12.504f) });
                }
                reference = Mask(image);
            }
            if (chinese) chinesePunctuationReference = reference; else englishPunctuationReference = reference;
            return reference;
        }
        private static bool[] halfReference, fullReference;
        private static bool[] Reference(bool full)
        {
            bool[] reference = full ? fullReference : halfReference;
            if (reference != null) return reference;
            using (var bitmap = new Bitmap(40, 40))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Microsoft YaHei", 26, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.White); graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.DrawString(full ? "全" : "半", font, Brushes.Black, -1, -1);
                }
                reference = Mask(bitmap);
            }
            if (full) fullReference = reference; else halfReference = reference;
            return reference;
        }
        private static bool[] Mask(Bitmap bitmap)
        {
            Color background = bitmap.GetPixel(0, 0);
            int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
            var ink = new bool[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) < 180) continue;
                ink[y * bitmap.Width + x] = true;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (maxX - minX < 4 || maxY - minY < 4) return null;
            bool[] result = new bool[256];
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                int sx = minX + (int)Math.Round(x * (maxX - minX) / 15.0), sy = minY + (int)Math.Round(y * (maxY - minY) / 15.0);
                result[y * 16 + x] = ink[sy * bitmap.Width + sx];
            }
            return result;
        }
        private static double Score(bool[] a, bool[] b)
        {
            if (b == null) return 0;
            int intersection = 0, total = 0;
            for (int i = 0; i < a.Length; i++) { if (a[i] && b[i]) intersection++; if (a[i]) total++; if (b[i]) total++; }
            return total > 0 ? 2.0 * intersection / total : 0;
        }
    }
}
