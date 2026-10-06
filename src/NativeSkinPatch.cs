using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WeTypeSkinStudio
{
    [DataContract]
    internal sealed class PatchEdit
    {
        [DataMember] public string Name;
        [DataMember] public int Offset;
        [DataMember] public string OriginalBytes;
        [DataMember] public string NewBytes;
    }
    internal sealed class PatchPlan
    {
        public byte[] Bytes;
        public string Hash;
        public byte[] AssetBytes;
        public string AssetHash;
        public List<PatchEdit> Edits = new List<PatchEdit>();
    }
    internal static class NativeSkinPatch
    {
        public const string Version = WeTypeInstallation.Version;
        public const string OriginalHash = "6c9d0022484110c12cd6b4e8ee5feef2946bcdfe326b182454605278edd05425";
        public const string OriginalAssetHash = "eb8537f4cf1f9de35f08f69cdd6def773a86873de29cd3e86e6bb542183c9537";
        // The encoded image retains its source aspect. Flutter covers the
        // live candidate bounds with a native Alignment; no stretching.
        public const int AssetCanvasWidth = 720;
        public const int AssetCanvasHeight = 112;
        public static readonly string InstallDirectory = WeTypeInstallation.DirectoryPath;
        public static readonly string TargetPath = Path.Combine(InstallDirectory, "flutter_datas", "business_data", "app.so");
        public const string AssetRelativePath = "flutter_datas\\business_data\\flutter_assets\\assets\\modules\\setting\\images\\ctrl_space_guide.gif";
        public static readonly string AssetPath = Path.Combine(InstallDirectory, "flutter_datas", "business_data", "flutter_assets", "assets", "modules", "setting", "images", "ctrl_space_guide.gif");
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", " "); }
        public static void RequireOriginal(byte[] bytes, string version)
        {
            if (version != Version) throw new NotSupportedException("仅支持已核对的微信输入法 " + Version + "。");
            if (Hash(bytes) != OriginalHash) throw new InvalidDataException("原始 app.so 的 SHA-256 不匹配，拒绝生成补丁。");
        }
        public static void RequireOriginalAsset(byte[] bytes)
        {
            if (bytes == null || Hash(bytes) != OriginalAssetHash)
                throw new InvalidDataException("原始候选背景资源的 SHA-256 不匹配，拒绝覆盖输入法文件。");
        }
        // Dart snapshot words use signed 7-bit groups with a terminal byte offset by 192.
        // Use a fixed four-byte low word and one-byte zero high word to preserve every offset.
        public static byte[] EncodeColor(string color)
        {
            Color c = SkinTheme.ParseColor(color);
            int value = c.ToArgb();
            return new byte[] { (byte)(value & 127), (byte)((value >> 7) & 127), (byte)((value >> 14) & 127), (byte)(192 + (value >> 21)), 192 };
        }
        public static SkinTheme NativeTheme(SkinTheme theme)
        {
            theme.Validate(); return theme.Clone();
        }

        private static byte[] HexBytes(string value)
        {
            string[] parts = value.Split(new char[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] result = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToByte(parts[i], 16);
            return result;
        }
        private static readonly byte[] ColorAssignmentOriginal = HexBytes("48 8b 45 e0");
        public static byte[] BuildAsset(SkinTheme theme, byte[] source)
        {
            theme.Validate();
            using (var output = new MemoryStream())
            {
                if (!string.IsNullOrEmpty(theme.BackgroundImageData))
                {
                    byte[] inputBytes = Convert.FromBase64String(theme.BackgroundImageData);
                    using (var input = new MemoryStream(inputBytes))
                    using (var sourceImage = Image.FromStream(input, true, true))
                    using (var rendered = new Bitmap(sourceImage.Width, sourceImage.Height, PixelFormat.Format32bppArgb))
                    using (var graphics = Graphics.FromImage(rendered))
                    {
                        graphics.Clear(SkinTheme.ParseColor(theme.Background));
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // Crop zoom within the source's own aspect ratio. Native
                        // BoxFit.cover and Alignment perform the remaining crop
                        // against the real (collapsed or expanded) window size.
                        float cropWidth = (float)(sourceImage.Width / theme.ImageZoom);
                        float cropHeight = (float)(sourceImage.Height / theme.ImageZoom);
                        RectangleF crop = new RectangleF((float)((sourceImage.Width - cropWidth) * theme.ImagePositionX),
                            (float)((sourceImage.Height - cropHeight) * theme.ImagePositionY), cropWidth, cropHeight);
                        graphics.DrawImage(sourceImage, new RectangleF(0, 0, rendered.Width, rendered.Height), crop, GraphicsUnit.Pixel);
                        if (theme.ImageTint > 0)
                        {
                            using (var tint = new SolidBrush(Color.FromArgb((int)Math.Round(255 * theme.ImageTint), SkinTheme.ParseColor(theme.Background))))
                                graphics.FillRectangle(tint, 0, 0, rendered.Width, rendered.Height);
                        }
                        // Keep full 8-bit alpha for transparent PNG/JPEG
                        // compositions. Flutter's codec detects the payload
                        // signature, so the existing .gif asset key remains
                        // stable while the bytes use PNG encoding.
                        rendered.Save(output, ImageFormat.Png);
                    }
                }
                else
                {
                    // Keep the injected DecorationImage on the same fixed canvas
                    // whether or not a user image is selected. A 1x1 asset can make
                    // Flutter recreate the candidate surface during startup and
                    // leave the renderer without an input window.
                    using (var transparent = new Bitmap(AssetCanvasWidth, AssetCanvasHeight, PixelFormat.Format32bppArgb))
                    using (var graphics = Graphics.FromImage(transparent))
                    {
                        graphics.Clear(SkinTheme.ParseColor(theme.Background)); transparent.Save(output, ImageFormat.Png);
                    }
                }
                return output.ToArray();
            }
        }
        // Version 0.3.0 created a 1x1 transparent PNG for a theme without a
        // background image. Keep its exact hash calculable so an interrupted or
        // already-applied legacy state can be restored and migrated safely.
        public static byte[] BuildLegacyTransparentAsset()
        {
            using (var output = new MemoryStream())
            using (var transparent = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            {
                transparent.SetPixel(0, 0, Color.Transparent); transparent.Save(output, ImageFormat.Png); return output.ToArray();
            }
        }
        internal static void Edit(PatchPlan plan, int offset, byte[] expected, byte[] replacement, string name)
        {
            if (expected.Length != replacement.Length || offset < 0 || offset + expected.Length > plan.Bytes.Length)
                throw new InvalidDataException("补丁长度或偏移无效：" + name);
            for (int i = 0; i < expected.Length; i++)
                if (plan.Bytes[offset + i] != expected[i]) throw new InvalidDataException("补丁原始字节不匹配：" + name + " @ 0x" + offset.ToString("X"));
            Array.Copy(replacement, 0, plan.Bytes, offset, replacement.Length);
            plan.Edits.Add(new PatchEdit { Name = name, Offset = offset, OriginalBytes = Hex(expected), NewBytes = Hex(replacement) });
        }
        private static void ColorEdit(PatchPlan plan, int offset, string original, string value, string name)
        {
            Edit(plan, offset, EncodeColor(original), EncodeColor(value), name);
        }
        public static PatchPlan Create(byte[] original, SkinTheme theme, string version)
        {
            return CreateCore(original, theme, version, false, false);
        }
        public static PatchPlan CreateLegacy(byte[] original, SkinTheme theme, string version)
        {
            return CreateCore(original, theme, version, true, true);
        }
        private static PatchPlan CreateCore(byte[] original, SkinTheme theme, string version, bool forceImageCode, bool legacyImageCode)
        {
            RequireOriginal(original, version); theme.Validate();
            var plan = new PatchPlan { Bytes = (byte[])original.Clone() };
            ColorEdit(plan, 0x1251A3, "#151515", theme.Background, "明暗背景共用对象");
            // Redirect light bgColors[01], preserving the shared two-byte white constant.
            Edit(plan, 0x8484CC, new byte[] { 0x4D, 0x8B, 0x9F, 0xF7, 0x99, 0, 0 }, new byte[] { 0x4D, 0x8B, 0x9F, 0x67, 0x9A, 0, 0 }, "浅色背景引用 PP[4939]");
            ColorEdit(plan, 0x125185, "#00B176", theme.Accent, "浅色强调色");
            ColorEdit(plan, 0x125199, "#00A86F", theme.Accent, "深色强调色");
            ColorEdit(plan, 0x125163, "#333333", theme.Foreground, "浅色文字");
            ColorEdit(plan, 0x125159, "#CCCCCC", theme.Foreground, "深色文字");
            ColorEdit(plan, 0x125145, "#777777", theme.Border, "辅助色 07/08");
            ColorEdit(plan, 0x12514A, "#888888", theme.Border, "辅助色 08/07");
            ColorEdit(plan, 0x12518A, "#E8F7F0", theme.Foreground, "候选文字白色分支对象");
            Edit(plan, 0xA9C714, new byte[] { 0x49, 0x8B, 0x87, 0xF7, 0x99, 0, 0 }, new byte[] { 0x49, 0x8B, 0x87, 0x07, 0x9A, 0, 0 }, "候选序号引用 PP[4927]");
            Edit(plan, 0xA9C887, new byte[] { 0x49, 0x8B, 0x8F, 0xF7, 0x99, 0, 0 }, new byte[] { 0x49, 0x8B, 0x8F, 0x07, 0x9A, 0, 0 }, "候选正文引用 PP[4927]");
            Edit(plan, 0xA9C993, new byte[] { 0x49, 0x8B, 0x8F, 0xF7, 0x99, 0, 0 }, new byte[] { 0x49, 0x8B, 0x8F, 0x07, 0x9A, 0, 0 }, "拼音引用 PP[4927]");
            // The existing Color.withOpacity call at A98DC8 belongs to BoxShadow.
            // Preserve its 0.1 alpha. The composed image applies background
            // opacity exactly once; an opaque color underneath would cancel it.
            Edit(plan, 0xA98EC3, ColorAssignmentOriginal, HexBytes("49 8b 46 68"), "候选根背景移交原生图片合成");
            // HomeView's Scaffold paints on top of the outer decoration. Use
            // the same transparent Color as its native bubble branch. RAX's
            // previous reload here is dead before the Scaffold allocation.
            Edit(plan, 0x92A1AF, HexBytes("48 89 c1 48 8b 45 f8"), HexBytes("49 8b 8f c7 4c 00 00"), "候选内容背景透明以显示外层图片");
            // EAX is overwritten immediately after this block, before any call or allocation.
            // Keep sizeRatio so decoration, clipping and border painting share the native radius.
            byte[] radius = new byte[] { 0xB8, (byte)theme.CornerRadius, 0, 0, 0, 0xF2, 0x0F, 0x2A, 0xC8, 0xF2, 0x0F, 0x59, 0xC8 };
            Edit(plan, 0xA98801, new byte[] { 0xF2, 0x41, 0x0F, 0x10, 0x8F, 0xD7, 0xB1, 0, 0, 0xF2, 0x0F, 0x59, 0xC8 }, radius, "候选原生圆角 × sizeRatio");
            NativeCandidateImageCode.Apply(plan, original, theme);
            plan.Hash = Hash(plan.Bytes); return plan;
        }
    }

    [DataContract]
    internal sealed class PatchState
    {
        [DataMember] public string Version = NativeSkinPatch.Version;
        [DataMember] public string OriginalHash = NativeSkinPatch.OriginalHash;
        [DataMember] public string CurrentHash = NativeSkinPatch.OriginalHash;
        [DataMember] public string OriginalAssetHash;
        [DataMember] public string CurrentAssetHash;
        [DataMember] public SkinTheme CurrentTheme;
        [DataMember] public bool Pending;
        [DataMember] public string NextHash;
        [DataMember] public string NextAssetHash;
        [DataMember] public SkinTheme NextTheme;
    }
    [DataContract]
    internal sealed class PatchAudit
    {
        [DataMember] public string Time;
        [DataMember] public string Operation;
        [DataMember] public string Phase;
        [DataMember] public string Version;
        [DataMember] public string OriginalHash;
        [DataMember] public string PreviousHash;
        [DataMember] public string NewHash;
        [DataMember] public string PreviousAssetHash;
        [DataMember] public string NewAssetHash;
        [DataMember] public List<PatchEdit> Edits;
    }
    internal sealed class NativePatchStore
    {
        private readonly string target, assetTarget, directory;
        public string BackupPath { get { return Path.Combine(directory, "app.so.original"); } }
        public string AssetBackupPath { get { return Path.Combine(directory, "ctrl_space_guide.gif.original"); } }
        public string StatePath { get { return Path.Combine(directory, "state.json"); } }
        public string AuditDirectory { get { return Path.Combine(directory, "audit"); } }
        public static NativePatchStore Installed()
        {
            return new NativePatchStore(NativeSkinPatch.TargetPath, NativeSkinPatch.AssetPath, Path.Combine(ThemeStore.DirectoryPath, "backups", NativeSkinPatch.Version));
        }
        // Alternate paths are used only by offline fixture tests; the elevated entry always uses Installed().
        internal NativePatchStore(string targetPath, string backupDirectory)
            : this(targetPath, Path.Combine(Path.GetDirectoryName(targetPath), "ctrl_space_guide.gif"), backupDirectory) { }
        internal NativePatchStore(string targetPath, string assetPath, string backupDirectory)
        { target = targetPath; assetTarget = assetPath; directory = backupDirectory; }
        private byte[] Original(byte[] current)
        {
            byte[] original;
            if (File.Exists(BackupPath)) original = File.ReadAllBytes(BackupPath);
            else
            {
                NativeSkinPatch.RequireOriginal(current, NativeSkinPatch.Version);
                original = current;
            }
            NativeSkinPatch.RequireOriginal(original, NativeSkinPatch.Version); return original;
        }
        private byte[] OriginalAsset(byte[] current, bool appIsOfficial)
        {
            byte[] original;
            if (File.Exists(AssetBackupPath)) original = File.ReadAllBytes(AssetBackupPath);
            else
            {
                if (!appIsOfficial) throw new InvalidDataException("缺少背景资源原始备份，拒绝覆盖已修改的输入法文件。");
                original = current;
            }
            NativeSkinPatch.RequireOriginalAsset(original);
            return original;
        }
        private string ThemeHash(byte[] original, SkinTheme theme)
        {
            return theme == null ? NativeSkinPatch.OriginalHash : NativeSkinPatch.Create(original, theme, NativeSkinPatch.Version).Hash;
        }
        private string LegacyThemeHash(byte[] original, SkinTheme theme)
        {
            return theme == null ? NativeSkinPatch.OriginalHash : NativeSkinPatch.CreateLegacy(original, theme, NativeSkinPatch.Version).Hash;
        }
        private string ThemeAssetHash(byte[] originalAsset, SkinTheme theme)
        {
            return theme == null ? NativeSkinPatch.Hash(originalAsset) : NativeSkinPatch.Hash(NativeSkinPatch.BuildAsset(theme, originalAsset));
        }
        private string LegacyThemeAssetHash(byte[] originalAsset, SkinTheme theme)
        {
            if (theme == null || !string.IsNullOrEmpty(theme.BackgroundImageData)) return ThemeAssetHash(originalAsset, theme);
            return NativeSkinPatch.Hash(NativeSkinPatch.BuildLegacyTransparentAsset());
        }
        private PatchState ReadState(byte[] original, byte[] originalAsset, string observedHash = null, string observedAssetHash = null)
        {
            if (!File.Exists(StatePath)) return new PatchState { OriginalAssetHash = NativeSkinPatch.Hash(originalAsset), CurrentAssetHash = NativeSkinPatch.Hash(originalAsset) };
            PatchState state = JsonFile.Read<PatchState>(StatePath);
            if (state != null && string.IsNullOrEmpty(state.OriginalAssetHash)) state.OriginalAssetHash = NativeSkinPatch.Hash(originalAsset);
            if (state != null && string.IsNullOrEmpty(state.CurrentAssetHash)) state.CurrentAssetHash = state.OriginalAssetHash;
            bool currentHashKnown = state != null && observedHash != null && state.CurrentHash == observedHash;
            bool currentAssetKnown = state != null && observedAssetHash != null && state.CurrentAssetHash == observedAssetHash;
            if (state == null || state.Version != NativeSkinPatch.Version || state.OriginalHash != NativeSkinPatch.OriginalHash
                || (!currentHashKnown && state.CurrentHash != ThemeHash(original, state.CurrentTheme) && state.CurrentHash != LegacyThemeHash(original, state.CurrentTheme))
                || state.OriginalAssetHash != NativeSkinPatch.Hash(originalAsset)
                || (!currentAssetKnown && state.CurrentAssetHash != ThemeAssetHash(originalAsset, state.CurrentTheme) && state.CurrentAssetHash != LegacyThemeAssetHash(originalAsset, state.CurrentTheme))
                || (state.Pending && ((state.NextHash != ThemeHash(original, state.NextTheme) && state.NextHash != LegacyThemeHash(original, state.NextTheme))
                    || (state.NextAssetHash != ThemeAssetHash(originalAsset, state.NextTheme) && state.NextAssetHash != LegacyThemeAssetHash(originalAsset, state.NextTheme)))))
                throw new InvalidDataException("补丁记录无法通过校验，拒绝覆盖安装文件。");
            return state;
        }
        private void ValidateCurrent(string hash, string assetHash, PatchState state)
        {
            bool appKnown = hash == NativeSkinPatch.OriginalHash || hash == state.CurrentHash || (state.Pending && hash == state.NextHash);
            bool assetKnown = assetHash == state.OriginalAssetHash || assetHash == state.CurrentAssetHash || (state.Pending && assetHash == state.NextAssetHash);
            if (!appKnown || !assetKnown) throw new InvalidDataException("输入法原生文件或背景资源已更新；当前 SHA-256 未被本工具记录，拒绝覆盖。\n"
                + "app.so: " + hash + " / recorded: " + state.CurrentHash
                + "\nasset: " + assetHash + " / recorded: " + state.CurrentAssetHash
                + "\nstate: " + StatePath);
        }
        public SkinTheme Inspect()
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string hash = NativeSkinPatch.Hash(current);
            byte[] currentAsset = File.ReadAllBytes(assetTarget);
            byte[] original = Original(current); byte[] originalAsset = OriginalAsset(currentAsset, hash == NativeSkinPatch.OriginalHash);
            var state = ReadState(original, originalAsset, hash, NativeSkinPatch.Hash(currentAsset)); ValidateCurrent(hash, NativeSkinPatch.Hash(currentAsset), state);
            if (hash == NativeSkinPatch.OriginalHash) return null;
            SkinTheme theme = state.Pending && hash == state.NextHash ? state.NextTheme : state.CurrentTheme;
            if (theme == null) throw new InvalidDataException("候选框补丁记录缺少皮肤信息。");
            theme.MigrateLegacyFields();
            return theme.Clone();
        }

        internal bool MatchesAppliedTheme(SkinTheme theme)
        {
            if (theme == null) return false;
            ValidatePaths(); theme.Validate();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeSkinPatch.Hash(current);
            byte[] currentAsset = File.ReadAllBytes(assetTarget); string currentAssetHash = NativeSkinPatch.Hash(currentAsset);
            byte[] original = Original(current); byte[] originalAsset = OriginalAsset(currentAsset, currentHash == NativeSkinPatch.OriginalHash);
            PatchPlan expected = MakePlan(original, originalAsset, NativeSkinPatch.NativeTheme(theme));
            if (!string.Equals(currentHash, expected.Hash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentAssetHash, expected.AssetHash, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        internal void RecoverFromAppliedTheme(SkinTheme theme)
        {
            if (!MatchesAppliedTheme(theme)) throw new InvalidDataException("候选框与恢复皮肤不匹配，保留原状态记录。");
            byte[] original = File.ReadAllBytes(BackupPath);
            byte[] originalAsset = File.ReadAllBytes(AssetBackupPath);
            NativeSkinPatch.RequireOriginal(original, NativeSkinPatch.Version);
            NativeSkinPatch.RequireOriginalAsset(originalAsset);
            PatchPlan expected = MakePlan(original, originalAsset, NativeSkinPatch.NativeTheme(theme));
            if (NativeSkinPatch.Hash(File.ReadAllBytes(target)) != expected.Hash || NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) != expected.AssetHash)
                throw new IOException("候选文件在恢复记录前发生变化，未提交恢复状态。");
            PreserveStateBeforeRecovery();
            JsonFile.Write(StatePath, new PatchState {
                CurrentHash = expected.Hash, CurrentAssetHash = expected.AssetHash,
                OriginalAssetHash = NativeSkinPatch.Hash(originalAsset), CurrentTheme = NativeSkinPatch.NativeTheme(theme)
            });
        }
        private void PreserveStateBeforeRecovery()
        {
            if (!File.Exists(StatePath)) return;
            string previous = Path.Combine(directory, "state.before-recovery.json");
            JsonFile.RejectReparsePath(previous);
            if (!File.Exists(previous)) JsonFile.AtomicWrite(previous, File.ReadAllBytes(StatePath));
        }
        public PatchPlan Prepare(SkinTheme theme)
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeSkinPatch.Hash(current);
            byte[] currentAsset = File.ReadAllBytes(assetTarget); byte[] original = Original(current); byte[] originalAsset = OriginalAsset(currentAsset, currentHash == NativeSkinPatch.OriginalHash);
            ValidateCurrent(currentHash, NativeSkinPatch.Hash(currentAsset), ReadState(original, originalAsset, currentHash, NativeSkinPatch.Hash(currentAsset)));
            return MakePlan(original, originalAsset, theme == null ? null : NativeSkinPatch.NativeTheme(theme));
        }
        private PatchPlan MakePlan(byte[] original, byte[] originalAsset, SkinTheme theme)
        {
            if (theme == null) return new PatchPlan { Bytes = original, Hash = NativeSkinPatch.OriginalHash, AssetBytes = originalAsset, AssetHash = NativeSkinPatch.Hash(originalAsset) };
            PatchPlan plan = NativeSkinPatch.Create(original, theme, NativeSkinPatch.Version);
            plan.AssetBytes = NativeSkinPatch.BuildAsset(theme, originalAsset); plan.AssetHash = NativeSkinPatch.Hash(plan.AssetBytes);
            return plan;
        }
        private void Audit(string operation, string phase, string oldHash, string newHash, string oldAssetHash, string newAssetHash, List<PatchEdit> edits)
        {
            string path = Path.Combine(AuditDirectory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            JsonFile.Write(path, new PatchAudit { Time = DateTime.UtcNow.ToString("o"), Operation = operation, Phase = phase, Version = NativeSkinPatch.Version,
                OriginalHash = NativeSkinPatch.OriginalHash, PreviousHash = oldHash, NewHash = newHash, PreviousAssetHash = oldAssetHash, NewAssetHash = newAssetHash, Edits = edits });
        }
        private static List<PatchEdit> Differences(byte[] current, byte[] next)
        {
            var edits = new List<PatchEdit>();
            if (current.Length != next.Length) throw new InvalidDataException("文件长度发生变化，拒绝应用。");
            int i = 0;
            while (i < current.Length)
            {
                if (current[i] == next[i]) { i++; continue; }
                int begin = i;
                while (i < current.Length && current[i] != next[i]) i++;
                byte[] oldBytes = new byte[i - begin], newBytes = new byte[i - begin];
                Array.Copy(current, begin, oldBytes, 0, oldBytes.Length); Array.Copy(next, begin, newBytes, 0, newBytes.Length);
                edits.Add(new PatchEdit { Name = "实际文件差异", Offset = begin, OriginalBytes = NativeSkinPatch.Hex(oldBytes), NewBytes = NativeSkinPatch.Hex(newBytes) });
            }
            return edits;
        }
        public void Change(SkinTheme theme)
        {
            using (var mutex = new System.Threading.Mutex(false, "Local\\WeTypeSkinStudio.NativePatch"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("另一个应用或还原操作正在进行。");
                try { ChangeLocked(theme); } finally { mutex.ReleaseMutex(); }
            }
        }
        private void ChangeLocked(SkinTheme theme)
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string oldHash = NativeSkinPatch.Hash(current);
            byte[] currentAsset = File.ReadAllBytes(assetTarget); string oldAssetHash = NativeSkinPatch.Hash(currentAsset);
            byte[] original = Original(current); byte[] originalAsset = OriginalAsset(currentAsset, oldHash == NativeSkinPatch.OriginalHash);
            PatchState previous = ReadState(original, originalAsset, oldHash, oldAssetHash); ValidateCurrent(oldHash, oldAssetHash, previous);
            if (!File.Exists(BackupPath)) JsonFile.AtomicWrite(BackupPath, original);
            if (!File.Exists(AssetBackupPath)) JsonFile.AtomicWrite(AssetBackupPath, originalAsset);
            NativeSkinPatch.RequireOriginal(File.ReadAllBytes(BackupPath), NativeSkinPatch.Version);
            NativeSkinPatch.RequireOriginalAsset(File.ReadAllBytes(AssetBackupPath));
            SkinTheme nextTheme = theme == null ? null : NativeSkinPatch.NativeTheme(theme);
            PatchPlan plan = MakePlan(original, originalAsset, nextTheme);
            SkinTheme currentTheme = oldHash == NativeSkinPatch.OriginalHash ? null : (previous.Pending && oldHash == previous.NextHash ? previous.NextTheme : previous.CurrentTheme);
            var pending = new PatchState { CurrentHash = oldHash, CurrentAssetHash = oldAssetHash, OriginalAssetHash = NativeSkinPatch.Hash(originalAsset), CurrentTheme = currentTheme,
                Pending = true, NextHash = plan.Hash, NextAssetHash = plan.AssetHash, NextTheme = nextTheme };
            string operation = theme == null ? "restore" : "apply";
            var edits = Differences(current, plan.Bytes);
            Audit(operation, "prepared", oldHash, plan.Hash, oldAssetHash, plan.AssetHash, edits);
            // Journal both valid states before replacing the file so interrupted writes are recoverable.
            JsonFile.Write(StatePath, pending);
            try
            {
                if (NativeSkinPatch.Hash(File.ReadAllBytes(target)) != oldHash || NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) != oldAssetHash) throw new IOException("安装文件在准备期间发生变化，已取消操作。");
                JsonFile.AtomicWrite(target, plan.Bytes);
                JsonFile.AtomicWrite(assetTarget, plan.AssetBytes);
                if (NativeSkinPatch.Hash(File.ReadAllBytes(target)) != plan.Hash) throw new IOException("写入后哈希校验失败。");
                if (NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) != plan.AssetHash) throw new IOException("背景资源写入后哈希校验失败。");
                Audit(operation, "committed", oldHash, plan.Hash, oldAssetHash, plan.AssetHash, edits);
                JsonFile.Write(StatePath, new PatchState { CurrentHash = plan.Hash, CurrentAssetHash = plan.AssetHash, OriginalAssetHash = NativeSkinPatch.Hash(originalAsset), CurrentTheme = nextTheme });
            }
            catch
            {
                bool restoredAsset = false, restoredApp = false;
                if (File.Exists(assetTarget) && NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) == plan.AssetHash)
                {
                    JsonFile.AtomicWrite(assetTarget, currentAsset); restoredAsset = true;
                }
                if (File.Exists(target) && NativeSkinPatch.Hash(File.ReadAllBytes(target)) == plan.Hash)
                {
                    JsonFile.AtomicWrite(target, current);
                    Audit(operation, "rolled-back", plan.Hash, oldHash, plan.AssetHash, oldAssetHash, Differences(plan.Bytes, current));
                    restoredApp = true;
                }
                if (restoredAsset && restoredApp) JsonFile.Write(StatePath, previous);
                throw;
            }
        }
        private void ValidatePaths()
        {
            foreach (string path in new string[] { target, assetTarget, BackupPath, AssetBackupPath, StatePath, AuditDirectory }) JsonFile.RejectReparsePath(path);
        }
    }
}
