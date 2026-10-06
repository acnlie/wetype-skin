using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace WeTypeSkinStudio
{
    internal static class JsonFile
    {
        public static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("操作路径不能包含重解析点：" + current);
                current = Path.GetDirectoryName(current);
            }
        }
        public static T Read<T>(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static void Write<T>(string path, T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); AtomicWrite(path, stream.ToArray());
            }
        }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            RejectReparsePath(path);
            string parent = Path.GetDirectoryName(Path.GetFullPath(path)); Directory.CreateDirectory(parent);
            string temp = Path.Combine(parent, ".native-skin-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
