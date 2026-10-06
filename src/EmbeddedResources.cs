using System.IO;
using System.Reflection;

namespace WeTypeSkinStudio
{
    internal static class EmbeddedResources
    {
        internal static byte[] Read(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WeTypeSkinStudio." + name))
            {
                if (stream == null) throw new FileNotFoundException("缺少内嵌资源：" + name);
                using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }
    }
}
