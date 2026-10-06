using System;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace WeTypeSkinStudio
{
    [DataContract]
    public sealed class SkinTheme
    {
        [DataMember(Name = "formatVersion")] public int FormatVersion = 1;
        [DataMember(Name = "name")] public string Name = "青竹";
        [DataMember(Name = "background")] public string Background = "#EFFAF3";
        [DataMember(Name = "foreground")] public string Foreground = "#163D2C";
        [DataMember(Name = "accent")] public string Accent = "#12936A";
        [DataMember(Name = "border")] public string Border = "#98CCB4";
        [DataMember(Name = "opacity")] public double Opacity = 0.96;
        [DataMember(Name = "cornerRadius")] public int CornerRadius = 14;
        [DataMember(Name = "imageTint")] public double ImageTint = 0.32;
        [DataMember(Name = "backgroundImage", EmitDefaultValue = false)] public string BackgroundImageData;
        [DataMember(Name = "backgroundImageName", EmitDefaultValue = false)] public string BackgroundImageName;
        [DataMember(Name = "imagePositionX")] public double ImagePositionX = 0.5;
        [DataMember(Name = "imagePositionY")] public double ImagePositionY = 0.5;
        [DataMember(Name = "imageZoom")] public double ImageZoom = 1;
        [DataMember(Name = "petEnabled")] public bool PetEnabled = true;
        [DataMember(Name = "petCandidate")] public bool PetCandidate = true;
        [DataMember(Name = "petToolbar")] public bool PetToolbar = true;
        [DataMember(Name = "petAnimate")] public bool PetAnimate = true;
        [DataMember(Name = "petSize")] public int PetSize = 88;
        [DataMember(Name = "petBackground")] public bool PetBackground = true;
        [DataMember(Name = "petStartWithWindows")] public bool PetStartWithWindows;

        [OnDeserializing]
        private void InitializeImageLayout(StreamingContext context)
        {
            ImagePositionX = ImagePositionY = 0.5; ImageZoom = 1;
            PetEnabled = PetCandidate = PetToolbar = PetAnimate = true; PetSize = 88;
            PetBackground = true;
        }

        // Version 1 exposed petBackground while the runtime was still hosted by
        // the editor. The pet is now an independent process, so keep the field
        // only for old skin files and normalize it at persistence boundaries.
        public void MigrateLegacyFields()
        {
            PetBackground = true;
        }

        public SkinTheme Clone() { return (SkinTheme)MemberwiseClone(); }

        public void Validate()
        {
            if (FormatVersion != 1) throw new InvalidDataException("暂不支持此皮肤文件的版本。");
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new InvalidDataException("皮肤名称需为 1–80 个字符。");
            foreach (string s in new string[] { Background, Foreground, Accent, Border })
                if (s == null || !Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$")) throw new InvalidDataException("颜色必须是 #RRGGBB 格式。");
            if (double.IsNaN(Opacity) || double.IsInfinity(Opacity) || Opacity < 0.3 || Opacity > 1) throw new InvalidDataException("背景不透明度需为 30%–100%。");
            if (double.IsNaN(ImageTint) || double.IsInfinity(ImageTint) || ImageTint < 0 || ImageTint > 1) throw new InvalidDataException("图片蒙层需为 0%–100%。");
            if (CornerRadius < 0 || CornerRadius > 40) throw new InvalidDataException("圆角需为 0–40 像素。");
            if (PetSize < 48 || PetSize > 160) throw new InvalidDataException("桌宠大小需为 48–160 像素。");
            if (!FiniteRange(ImagePositionX, 0, 1) || !FiniteRange(ImagePositionY, 0, 1) || !FiniteRange(ImageZoom, 1, 4))
                throw new InvalidDataException("图片位置需为 0%–100%，缩放需为 100%–400%。");
            if (!string.IsNullOrEmpty(BackgroundImageData))
            {
                if (BackgroundImageData.Length > 12 * 1024 * 1024) throw new InvalidDataException("背景图片不能超过 8 MB。");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(BackgroundImageData); }
                catch (FormatException) { throw new InvalidDataException("背景图片数据无效。"); }
                ValidateImage(bytes);
            }
        }

        private static bool FiniteRange(double value, double minimum, double maximum)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= minimum && value <= maximum;
        }

        public RectangleF ImageBounds(Size image, Size viewport)
        {
            double scale = Math.Max((double)viewport.Width / image.Width, (double)viewport.Height / image.Height) * ImageZoom;
            float width = (float)(image.Width * scale), height = (float)(image.Height * scale);
            return new RectangleF((float)((viewport.Width - width) * ImagePositionX), (float)((viewport.Height - height) * ImagePositionY), width, height);
        }

        public static void ValidateImage(byte[] bytes)
        {
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("背景图片不能超过 8 MB。");
            using (var stream = new MemoryStream(bytes))
            using (var image = Image.FromStream(stream, true, true))
                if (image.Width > 4096 || image.Height > 4096 || image.Width < 1 || image.Height < 1)
                    throw new InvalidDataException("背景图片的宽和高不能超过 4096 像素。");
        }

        public void SetImage(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("背景图片不能超过 8 MB。");
            byte[] bytes = File.ReadAllBytes(path);
            ValidateImage(bytes);
            BackgroundImageData = Convert.ToBase64String(bytes);
            BackgroundImageName = Path.GetFileName(path);
            ImagePositionX = ImagePositionY = 0.5; ImageZoom = 1;
        }

        public static Color ParseColor(string value) { return ColorTranslator.FromHtml(value); }

        public static SkinTheme[] Presets()
        {
            return new SkinTheme[] {
                new SkinTheme(),
                new SkinTheme { Name = "墨夜", Background = "#19232B", Foreground = "#EDF5F1", Accent = "#61D7AF", Border = "#43574E", Opacity = 0.94 },
                new SkinTheme { Name = "樱雾", Background = "#FFF2F6", Foreground = "#67384B", Accent = "#D55382", Border = "#E6B7C8", Opacity = 0.94, CornerRadius = 20 },
                new SkinTheme { Name = "海盐", Background = "#EEF6FF", Foreground = "#223F65", Accent = "#367BD8", Border = "#ADC7E9", Opacity = 0.96 },
                new SkinTheme { Name = "纸笺", Background = "#FFF9EC", Foreground = "#57442E", Accent = "#B57932", Border = "#DDC9A6", Opacity = 1, CornerRadius = 8 },
                new SkinTheme { Name = "暮紫", Background = "#2B233B", Foreground = "#F1EBFA", Accent = "#BB95F4", Border = "#6A5687", Opacity = 0.93, CornerRadius = 18 }
            };
        }
    }

    public static class ThemeStore
    {
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeTypeSkinStudio");
        public static readonly string CurrentPath = Path.Combine(DirectoryPath, "current.wtskin.json");

        public static SkinTheme Load(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 13 * 1024 * 1024) throw new InvalidDataException("皮肤文件过大。");
            SkinTheme result;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                result = (SkinTheme)new DataContractJsonSerializer(typeof(SkinTheme)).ReadObject(stream);
            if (result == null) throw new InvalidDataException("皮肤文件内容为空。");
            result.MigrateLegacyFields();
            result.Validate();
            return result;
        }

        public static void Save(SkinTheme theme, string path)
        {
            theme.MigrateLegacyFields();
            theme.Validate();
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(parent);
            string temp = Path.Combine(parent, ".wtskin-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = File.Create(temp))
                    new DataContractJsonSerializer(typeof(SkinTheme)).WriteObject(stream, theme);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
