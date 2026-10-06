using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WeTypeSkinStudio
{
    // The update process carries the native status bar skin in a WXZ resource.
    // This is deliberately kept separate from the Flutter app.so patch: the
    // status bar is painted by the native update window and never reaches the
    // candidate renderer's Dart snapshot.
    internal static class NativeToolbarPatch
    {
        public const string OfficialHash = "7aae1bd693bd1e94fd2da9ccf577c8c92bfa88d87b719df3b9330fa81d19c292";
        internal const string LegacyBaselineHash = "960ba4dd7f1b400da206a86aa1618853bc0c46ef132483e3786b8cb18898e95d";
        public const int ResourceId = 105;
        private const string ResourceType = "WXZ";
        private const int HeaderSize = 0x80;
        private const int EntrySize = 48;
        private const int IndexOffset = 0x10;
        private const int NamesOffset = 0x18;
        private const int IndexCompressedSize = 0x20;
        private const int NamesCompressedSize = 0x24;
        private const int NamesUncompressedSize = 0x28;

        private sealed class Entry
        {
            public readonly uint[] Values = new uint[12];
            public byte[] Payload;
            public string Name;
            public byte[] Uncompressed;
        }

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static void RequireOriginal(byte[] bytes)
        {
            if (bytes == null || !string.Equals(Hash(bytes), OfficialHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原生工具条文件的 SHA-256 不匹配，拒绝覆盖输入法文件。");
        }

        public static byte[] Create(byte[] original, SkinTheme theme)
        {
            RequireOriginal(original);
            if (theme == null) return (byte[])original.Clone();
            theme.Validate();
            string temp = Path.Combine(Path.GetTempPath(), "wetype-toolbar-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllBytes(temp, original);
                PatchExecutable(temp, theme);
                return File.ReadAllBytes(temp);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static void PatchExecutable(string path, SkinTheme theme)
        {
            byte[] resource = ReadResource(path);
            byte[] patched = CreateResource(resource, theme);
            IntPtr type = Marshal.StringToHGlobalUni(ResourceType);
            IntPtr update = IntPtr.Zero;
            bool commit = false;
            try
            {
                update = BeginUpdateResource(path, false);
                if (update == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开工具条原生资源。");
                if (!UpdateResource(update, type, new IntPtr(ResourceId), 2052, patched, (uint)patched.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入工具条原生资源。");
                commit = true;
            }
            finally
            {
                if (update != IntPtr.Zero && !EndUpdateResource(update, !commit))
                    if (commit) throw new Win32Exception(Marshal.GetLastWin32Error(), "工具条原生资源提交失败。");
                Marshal.FreeHGlobal(type);
            }
            VerifyResource(ReadResource(path), theme);
        }

        private static byte[] ReadResource(string path)
        {
            IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 0x00000002);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取工具条原生资源。");
            IntPtr type = Marshal.StringToHGlobalUni(ResourceType);
            try
            {
                IntPtr resource = FindResourceEx(module, type, new IntPtr(ResourceId), 2052);
                if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "找不到工具条 WXZ 资源。");
                int size = checked((int)SizeofResource(module, resource));
                IntPtr loaded = LoadResource(module, resource);
                IntPtr dataPtr = LockResource(loaded);
                if (size <= 0 || dataPtr == IntPtr.Zero) throw new InvalidDataException("工具条 WXZ 资源为空。");
                byte[] data = new byte[size]; Marshal.Copy(dataPtr, data, 0, size); return data;
            }
            finally { Marshal.FreeHGlobal(type); FreeLibrary(module); }
        }

        private static void VerifyResource(byte[] raw, SkinTheme theme)
        {
            List<Entry> entries; byte[] names;
            Parse(raw, out entries, out names);
            string background = "#" + Rgb(theme.Background), border = "#" + Rgb(theme.Border);
            bool xmlFound = false, backgroundFound = false, borderFound = false;
            foreach (Entry entry in entries)
            {
                if (entry.Name.Equals("statusbar_wnd_v2.xml", StringComparison.OrdinalIgnoreCase))
                {
                    string xml = Utf8(entry.Uncompressed);
                    xmlFound = xml.IndexOf("StatusBarIconV2\\statusbar_bg_05.svg", StringComparison.OrdinalIgnoreCase) >= 0
                        && xml.IndexOf("shadow_statusbar.svg", StringComparison.OrdinalIgnoreCase) < 0;
                }
                if (entry.Name.StartsWith("StatusBarIconV2\\", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.IndexOf("statusbar_bg_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string svg = Utf8(entry.Uncompressed);
                    backgroundFound |= svg.IndexOf("fill=\"" + background + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    borderFound |= svg.IndexOf("fill=\"" + border + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            if (!xmlFound || !backgroundFound || !borderFound)
                throw new InvalidDataException("工具条原生资源提交后验证失败。");
        }

        private static byte[] CreateResource(byte[] original, SkinTheme theme)
        {
            theme.Validate();

            List<Entry> entries;
            byte[] names;
            Parse(original, out entries, out names);
            string background = Rgb(theme.Background);
            string border = Rgb(theme.Border);
            string foreground = Rgb(theme.Foreground), accent = Rgb(theme.Accent);
            bool changedXml = false, changedBackground = false;
            foreach (Entry entry in entries)
            {
                if (entry.Name.Equals("statusbar_wnd_v2.xml", StringComparison.OrdinalIgnoreCase))
                {
                    string xml = Utf8(entry.Uncompressed);
                    // The shipped XML refers to shadow_statusbar.svg, but this
                    // build does not contain that asset. A real background
                    // resource keeps the native Dui toolbar visible and lets
                    // the same theme work after a renderer restart.
                    string updated = xml.Replace("StatusBarIconV2\\shadow_statusbar.svg", "StatusBarIconV2\\statusbar_bg_05.svg");
                    if (!string.Equals(xml, updated, StringComparison.Ordinal))
                    {
                        entry.Uncompressed = Encoding.UTF8.GetBytes(updated);
                        entry.Payload = EncodeEntry(entry.Uncompressed);
                        changedXml = true;
                    }
                }
                if (entry.Name.StartsWith("StatusBarIconV2\\", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.IndexOf("statusbar_bg_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string svg = Utf8(entry.Uncompressed);
                    string updated = svg.Replace("fill=\"white\"", "fill=\"#" + background + "\"")
                        .Replace("fill=\"#151515\"", "fill=\"#" + background + "\"")
                        .Replace("fill=\"#BBBBBB\"", "fill=\"#" + border + "\"")
                        .Replace("fill=\"#444444\"", "fill=\"#" + border + "\"");
                    if (!string.Equals(svg, updated, StringComparison.Ordinal))
                    {
                        entry.Uncompressed = Encoding.UTF8.GetBytes(updated);
                        entry.Payload = EncodeEntry(entry.Uncompressed);
                        changedBackground = true;
                    }
                }
                else if (entry.Name.StartsWith("StatusBarIconV2\\", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // Both native theme branches must use the chosen glyph
                    // color; dark-mode white icons disappear on a light skin.
                    string svg = Utf8(entry.Uncompressed);
                    string updated = svg.Replace("\"#222222\"", "\"#" + foreground + "\"")
                        .Replace("\"#DDDDDD\"", "\"#" + foreground + "\"")
                        .Replace("\"#1AC785\"", "\"#" + accent + "\"");
                    if (updated != svg)
                    {
                        entry.Uncompressed = Encoding.UTF8.GetBytes(updated);
                        entry.Payload = EncodeEntry(entry.Uncompressed);
                    }
                }
            }
            if (!changedXml && !changedBackground)
                throw new InvalidDataException("未找到微信输入法工具条的原生资源入口。");
            return Build(original, entries, names);
        }

        private static string Rgb(string value)
        {
            var color = SkinTheme.ParseColor(value);
            return color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private static string Utf8(byte[] bytes)
        {
            // The XML resources are UTF-8, including a BOM on some entries.
            return Encoding.UTF8.GetString(bytes);
        }

        private static void Parse(byte[] raw, out List<Entry> entries, out byte[] names)
        {
            if (raw.Length < HeaderSize || raw[0] != (byte)'z' || raw[1] != (byte)'x'
                || raw[2] != (byte)'i' || raw[3] != (byte)'w')
                throw new InvalidDataException("工具条 WXZ 资源头无效。");
            int indexOffset = ReadInt(raw, IndexOffset);
            int namesOffset = ReadInt(raw, NamesOffset);
            int indexSize = ReadInt(raw, IndexCompressedSize);
            int namesSize = ReadInt(raw, NamesCompressedSize);
            if (indexOffset < HeaderSize || namesOffset <= indexOffset || indexSize <= 0 || namesSize <= 0
                || indexOffset + indexSize > raw.Length || namesOffset + namesSize > raw.Length)
                throw new InvalidDataException("工具条 WXZ 索引范围无效。");
            byte[] index = DecodeZlib(raw, indexOffset, indexSize);
            names = DecodeZlib(raw, namesOffset, namesSize);
            if (index.Length % EntrySize != 0) throw new InvalidDataException("工具条 WXZ 索引记录长度无效。");
            string[] nameList = Encoding.Unicode.GetString(names).Split(new[] { '\n' });
            int count = index.Length / EntrySize;
            if (nameList.Length < count) throw new InvalidDataException("工具条 WXZ 名称表不完整。");
            entries = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                var entry = new Entry { Name = nameList[i].TrimEnd('\r') };
                for (int j = 0; j < 12; j++) entry.Values[j] = BitConverter.ToUInt32(index, i * EntrySize + j * 4);
                int offset = CheckedInt(entry.Values[0]);
                int compressed = CheckedInt(entry.Values[4]);
                if (offset < HeaderSize || compressed < 0 || offset + compressed > indexOffset)
                    throw new InvalidDataException("工具条 WXZ 数据记录范围无效：" + entry.Name);
                entry.Payload = Copy(raw, offset, compressed);
                try { entry.Uncompressed = entry.Values[6] == 2 ? DecodeZlibXor(entry.Payload, 0, entry.Payload.Length) : (byte[])entry.Payload.Clone(); }
                catch (Exception ex) { throw new InvalidDataException("工具条 WXZ 条目无法解码：" + entry.Name, ex); }
                entries.Add(entry);
            }
        }

        private static byte[] Build(byte[] original, List<Entry> entries, byte[] names)
        {
            int oldIndexOffset = ReadInt(original, IndexOffset);
            var data = new MemoryStream();
            data.Write(original, 0, HeaderSize);
            int position = HeaderSize;
            foreach (Entry entry in entries)
            {
                int oldOffset = CheckedInt(entry.Values[0]);
                int oldSize = CheckedInt(entry.Values[4]);
                if (oldOffset < position || oldOffset + oldSize > oldIndexOffset)
                    throw new InvalidDataException("工具条 WXZ 数据间隙无效：" + entry.Name);
                data.Write(original, position, oldOffset - position);
                byte[] payload = entry.Payload;
                int newOffset = checked((int)data.Position);
                data.Write(payload, 0, payload.Length);
                entry.Values[0] = (uint)newOffset;
                entry.Values[4] = (uint)payload.Length;
                entry.Values[5] = (uint)entry.Uncompressed.Length;
                entry.Values[10] = entry.Values[5];
                position = oldOffset + oldSize;
            }
            data.Write(original, position, oldIndexOffset - position);
            byte[] dataBytes = data.ToArray();
            byte[] index = new byte[entries.Count * EntrySize];
            for (int i = 0; i < entries.Count; i++)
                for (int j = 0; j < 12; j++)
                    Array.Copy(BitConverter.GetBytes(entries[i].Values[j]), 0, index, i * EntrySize + j * 4, 4);
            byte[] indexCompressed = EncodeZlib(index);
            byte[] namesCompressed = EncodeZlib(names);
            int newIndexOffset = checked(dataBytes.Length);
            int newNamesOffset = checked(newIndexOffset + indexCompressed.Length);
            using (var output = new MemoryStream())
            {
                byte[] header = Copy(original, 0, HeaderSize);
                WriteInt(header, IndexOffset, newIndexOffset);
                WriteInt(header, NamesOffset, newNamesOffset);
                WriteInt(header, IndexCompressedSize, indexCompressed.Length);
                WriteInt(header, NamesCompressedSize, namesCompressed.Length);
                WriteInt(header, NamesUncompressedSize, names.Length);
                output.Write(header, 0, header.Length);
                output.Write(dataBytes, HeaderSize, dataBytes.Length - HeaderSize);
                output.Write(indexCompressed, 0, indexCompressed.Length);
                output.Write(namesCompressed, 0, namesCompressed.Length);
                return output.ToArray();
            }
        }

        private static byte[] DecodeZlibXor(byte[] source, int offset, int length)
        {
            if (length < 6) throw new InvalidDataException("WXZ 压缩记录过短。");
            byte[] encoded = Copy(source, offset, length);
            for (int i = 0; i < encoded.Length; i++) encoded[i] ^= 0x63;
            return DecodeZlib(encoded, 0, encoded.Length);
        }

        private static byte[] DecodeZlib(byte[] source, int offset, int length)
        {
            if (offset < 0 || length < 6 || offset > source.Length - length)
                throw new InvalidDataException("WXZ 压缩记录范围无效。");
            int header = (source[offset] << 8) | source[offset + 1];
            if ((source[offset] & 15) != 8 || (source[offset] >> 4) > 7 || header % 31 != 0 || (source[offset + 1] & 32) != 0)
                throw new InvalidDataException("WXZ zlib 压缩头无效。");
            using (var input = new MemoryStream(source, offset + 2, length - 6, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192]; int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("WXZ 解压大小超过限制。");
                    output.Write(buffer, 0, read);
                }
                byte[] decoded = output.ToArray();
                int end = offset + length - 4;
                uint expected = ((uint)source[end] << 24) | ((uint)source[end + 1] << 16) | ((uint)source[end + 2] << 8) | source[end + 3];
                if (Adler32(decoded) != expected) throw new InvalidDataException("WXZ zlib Adler-32 校验失败。");
                return decoded;
            }
        }

        private static byte[] EncodeEntry(byte[] uncompressed)
        {
            byte[] zlib = EncodeZlib(uncompressed);
            for (int i = 0; i < zlib.Length; i++) zlib[i] ^= 0x63;
            return zlib;
        }

        private static byte[] EncodeZlib(byte[] uncompressed)
        {
            using (var body = new MemoryStream())
            {
                using (var deflate = new DeflateStream(body, CompressionMode.Compress, true))
                    deflate.Write(uncompressed, 0, uncompressed.Length);
                byte[] compressed = body.ToArray();
                using (var result = new MemoryStream())
                {
                    result.WriteByte(0x78); result.WriteByte(0x9C);
                    result.Write(compressed, 0, compressed.Length);
                    uint checksum = Adler32(uncompressed);
                    result.WriteByte((byte)(checksum >> 24)); result.WriteByte((byte)(checksum >> 16));
                    result.WriteByte((byte)(checksum >> 8)); result.WriteByte((byte)checksum);
                    return result.ToArray();
                }
            }
        }

        private static uint Adler32(byte[] bytes)
        {
            uint a = 1, b = 0;
            foreach (byte value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        private static int ReadInt(byte[] bytes, int offset) { return BitConverter.ToInt32(bytes, offset); }
        private static void WriteInt(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static int CheckedInt(uint value) { if (value > int.MaxValue) throw new InvalidDataException("WXZ 数值超出范围。"); return (int)value; }
        private static byte[] Copy(byte[] bytes, int offset, int length) { var result = new byte[length]; Array.Copy(bytes, offset, result, 0, length); return result; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", EntryPoint = "FindResourceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceEx(IntPtr module, IntPtr type, IntPtr name, ushort language);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);
        [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
        [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr update, bool discard);
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class ToolbarPatchState
    {
        [System.Runtime.Serialization.DataMember] public string OriginalHash = NativeToolbarPatch.OfficialHash;
        [System.Runtime.Serialization.DataMember] public string CurrentHash = NativeToolbarPatch.OfficialHash;
        [System.Runtime.Serialization.DataMember] public SkinTheme CurrentTheme;
        [System.Runtime.Serialization.DataMember] public bool Pending;
        [System.Runtime.Serialization.DataMember] public string NextHash;
        [System.Runtime.Serialization.DataMember] public SkinTheme NextTheme;
    }

    internal sealed class NativeToolbarStore
    {
        private readonly string target, directory;
        public string BackupPath { get { return Path.Combine(directory, "wetype_update.exe.original"); } }
        internal string StatePath { get { return Path.Combine(directory, "toolbar-state.json"); } }
        public static NativeToolbarStore Installed()
        {
            return new NativeToolbarStore(
                Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_update.exe"),
                Path.Combine(ThemeStore.DirectoryPath, "backups", NativeSkinPatch.Version));
        }
        internal NativeToolbarStore(string targetPath, string backupDirectory)
        { target = targetPath; directory = backupDirectory; }

        private byte[] Original(byte[] current)
        {
            byte[] original;
            if (File.Exists(BackupPath)) original = File.ReadAllBytes(BackupPath);
            else
            {
                NativeToolbarPatch.RequireOriginal(current);
                original = current;
            }
            NativeToolbarPatch.RequireOriginal(original);
            return original;
        }

        private ToolbarPatchState ReadState(byte[] original, string currentHash)
        {
            ToolbarPatchState state = File.Exists(StatePath) ? JsonFile.Read<ToolbarPatchState>(StatePath) : null;
            if (state == null) state = new ToolbarPatchState();
            if (state.OriginalHash == NativeToolbarPatch.LegacyBaselineHash && state.CurrentHash == NativeToolbarPatch.LegacyBaselineHash
                && (currentHash == NativeToolbarPatch.LegacyBaselineHash || currentHash == NativeToolbarPatch.OfficialHash))
                state.OriginalHash = NativeToolbarPatch.OfficialHash;
            if (state.OriginalHash != NativeToolbarPatch.Hash(original)
                || (currentHash != NativeToolbarPatch.Hash(original) && state.CurrentHash != currentHash
                    && !(state.Pending && state.NextHash == currentHash)))
                throw new InvalidDataException("输入法工具条文件已被其他程序修改，拒绝覆盖。");
            return state;
        }

        private void ValidatePaths()
        {
            foreach (string path in new[] { target, BackupPath, StatePath }) JsonFile.RejectReparsePath(path);
        }

        public SkinTheme Inspect()
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeToolbarPatch.Hash(current);
            byte[] original = Original(current);
            ToolbarPatchState state = ReadState(original, currentHash);
            if (currentHash == NativeToolbarPatch.Hash(original)) return null;
            SkinTheme theme = state.Pending && currentHash == state.NextHash ? state.NextTheme : state.CurrentTheme;
            if (theme == null) throw new InvalidDataException("工具条补丁记录缺少皮肤信息。");
            theme.MigrateLegacyFields();
            return theme.Clone();
        }

        internal bool MatchesAppliedTheme(SkinTheme theme)
        {
            if (theme == null) return false;
            ValidatePaths(); theme.Validate();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeToolbarPatch.Hash(current);
            byte[] original = Original(current);
            byte[] expected = NativeToolbarPatch.Create(original, NativeSkinPatch.NativeTheme(theme));
            string expectedHash = NativeToolbarPatch.Hash(expected);
            return string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        internal void RecoverFromAppliedTheme(SkinTheme theme)
        {
            if (!MatchesAppliedTheme(theme)) throw new InvalidDataException("工具条与恢复皮肤不匹配，保留原状态记录。");
            byte[] original = File.ReadAllBytes(BackupPath);
            string expectedHash = NativeToolbarPatch.Hash(NativeToolbarPatch.Create(original, NativeSkinPatch.NativeTheme(theme)));
            if (NativeToolbarPatch.Hash(File.ReadAllBytes(target)) != expectedHash) throw new IOException("工具条在恢复记录前发生变化。");
            if (File.Exists(StatePath))
            {
                string previous = Path.Combine(directory, "toolbar-state.before-recovery.json");
                JsonFile.RejectReparsePath(previous);
                if (!File.Exists(previous)) JsonFile.AtomicWrite(previous, File.ReadAllBytes(StatePath));
            }
            JsonFile.Write(StatePath, new ToolbarPatchState {
                OriginalHash = NativeToolbarPatch.Hash(original), CurrentHash = expectedHash,
                CurrentTheme = NativeSkinPatch.NativeTheme(theme)
            });
        }

        public void Prepare(SkinTheme theme)
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeToolbarPatch.Hash(current);
            byte[] original = Original(current);
            ReadState(original, currentHash);
            if (theme != null) NativeToolbarPatch.Create(original, NativeSkinPatch.NativeTheme(theme));
            if (!File.Exists(BackupPath)) JsonFile.AtomicWrite(BackupPath, original);
        }

        public void Change(SkinTheme theme)
        {
            ValidatePaths();
            byte[] current = File.ReadAllBytes(target); string currentHash = NativeToolbarPatch.Hash(current);
            byte[] original = Original(current);
            ToolbarPatchState previous = ReadState(original, currentHash);
            if (!File.Exists(BackupPath)) JsonFile.AtomicWrite(BackupPath, original);
            byte[] next = theme == null ? original : NativeToolbarPatch.Create(original, NativeSkinPatch.NativeTheme(theme));
            string nextHash = NativeToolbarPatch.Hash(next);
            SkinTheme previousTheme = currentHash == NativeToolbarPatch.OfficialHash ? null :
                (previous.Pending && currentHash == previous.NextHash ? previous.NextTheme : previous.CurrentTheme);
            // Journal both known states before replacement so interrupted writes
            // remain recoverable on the next launch.
            JsonFile.Write(StatePath, new ToolbarPatchState { CurrentHash = currentHash, CurrentTheme = previousTheme,
                Pending = true, NextHash = nextHash, NextTheme = theme == null ? null : NativeSkinPatch.NativeTheme(theme) });
            try
            {
                if (NativeToolbarPatch.Hash(File.ReadAllBytes(target)) != currentHash) throw new IOException("工具条文件在准备期间发生变化。");
                JsonFile.AtomicWrite(target, next);
                if (NativeToolbarPatch.Hash(File.ReadAllBytes(target)) != nextHash) throw new IOException("工具条文件写入后哈希校验失败。");
                JsonFile.Write(StatePath, new ToolbarPatchState { OriginalHash = NativeToolbarPatch.Hash(original), CurrentHash = nextHash, CurrentTheme = theme == null ? null : NativeSkinPatch.NativeTheme(theme) });
            }
            catch
            {
                if (File.Exists(target) && NativeToolbarPatch.Hash(File.ReadAllBytes(target)) == nextHash) JsonFile.AtomicWrite(target, current);
                if (File.Exists(target) && NativeToolbarPatch.Hash(File.ReadAllBytes(target)) == currentHash)
                    JsonFile.Write(StatePath, previous);
                throw;
            }
        }
    }
}
