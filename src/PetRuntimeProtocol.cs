using System.IO;
using System.Runtime.Serialization;

namespace WeTypeSkinStudio
{
    internal static class PetRuntimeProtocol
    {
        internal const string MutexName = "Local\\DeepSeekChan.InputPet";
        internal const string StopName = "Local\\DeepSeekChan.InputPet.Stop";
        internal const string ReadyName = "Local\\DeepSeekChan.InputPet.Ready";
        internal const string StartName = "Local\\DeepSeekChan.InputPet.Start";
        internal static readonly string RuntimePath = Path.Combine(ThemeStore.DirectoryPath, "runtime", "DeepSeekChan.InputPet.exe");
        internal static readonly string StatusPath = Path.Combine(ThemeStore.DirectoryPath, "pet-runtime-status.json");
        internal static readonly string ControlPath = Path.Combine(ThemeStore.DirectoryPath, "pet-runtime-control.json");
    }
    [DataContract]
    internal sealed class PetRuntimeControl
    {
        [DataMember] public int OwnerProcessId { get; set; }
        [DataMember] public bool Suspended { get; set; }
        [DataMember] public string RequestId { get; set; }
    }
}
