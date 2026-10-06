using System;
using System.IO;

namespace WeTypeSkinStudio
{
    internal static class WeTypeInstallation
    {
        internal const string Version = "2.1.4.6";
        internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeType", Version);
    }
}
