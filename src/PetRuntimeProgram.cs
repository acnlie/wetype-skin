using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    internal static class PetRuntimeProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                bool first;
                using (var mutex = new Mutex(true, PetRuntimeProtocol.MutexName, out first))
                {
                    if (!first) return 0;
                    if (!File.Exists(ThemeStore.CurrentPath)) return 0;
                    DateTime settingsTime = File.GetLastWriteTimeUtc(ThemeStore.CurrentPath);
                    SkinTheme theme = ThemeStore.Load(ThemeStore.CurrentPath);
                    if (!theme.PetEnabled || !theme.PetCandidate && !theme.PetToolbar) return 0;
                    int index = Array.IndexOf(args, "--pet-diagnostics");
                    string diagnostics = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
                    using (var host = new PetRuntimeContext(theme, settingsTime, diagnostics)) Application.Run(host);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { Directory.CreateDirectory(ThemeStore.DirectoryPath); File.WriteAllText(Path.Combine(ThemeStore.DirectoryPath, "last-pet-runtime-error.txt"), ex.ToString()); }
                catch { }
                return 1;
            }
        }
    }
    // The dedicated executable has no studio form, tray, or WebView2 reference.
    internal sealed class PetRuntimeContext : ApplicationContext
    {
        private readonly EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, PetRuntimeProtocol.StopName);
        private readonly EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, PetRuntimeProtocol.ReadyName);
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        private readonly WhalePetController pet;
        private readonly int processId;
        private DateTime settingsTime, controlTime;
        private Task<SkinTheme> loading;
        private Task writing;
        private PetRuntimeControl control;
        private bool suspended, stopped;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long nextStatus;
        internal PetRuntimeContext(SkinTheme theme, DateTime loadedSettingsTime, string diagnostics)
        {
            using (Process process = Process.GetCurrentProcess()) processId = process.Id;
            stop.Reset(); ready.Reset();
            pet = new WhalePetController(!string.IsNullOrEmpty(diagnostics), diagnostics);
            pet.Configure(theme);
            settingsTime = loadedSettingsTime;
            JsonFile.Write(PetRuntimeProtocol.StatusPath, Snapshot());
            timer.Tick += delegate { Tick(); };
            timer.Start(); ready.Set();
        }
        private void Tick()
        {
            if (stop.WaitOne(0)) { ExitThread(); return; }
            try
            {
                DateTime changed = File.GetLastWriteTimeUtc(ThemeStore.CurrentPath);
                if (loading == null && changed != settingsTime)
                {
                    settingsTime = changed;
                    loading = Task.Run(delegate { return ThemeStore.Load(ThemeStore.CurrentPath); });
                }
                if (loading != null && loading.IsCompleted)
                {
                    Task<SkinTheme> completed = loading; loading = null;
                    SkinTheme theme = completed.GetAwaiter().GetResult();
                    if (!theme.PetEnabled || !theme.PetCandidate && !theme.PetToolbar) { ExitThread(); return; }
                    pet.Configure(theme);
                }
                DateTime commandChanged = File.GetLastWriteTimeUtc(PetRuntimeProtocol.ControlPath);
                if (commandChanged != controlTime && File.Exists(PetRuntimeProtocol.ControlPath))
                {
                    control = JsonFile.Read<PetRuntimeControl>(PetRuntimeProtocol.ControlPath);
                    controlTime = commandChanged; nextStatus = 0;
                }
                bool pause = control != null && control.Suspended && OwnerAlive(control.OwnerProcessId);
                if (pause != suspended) { suspended = pause; pet.Suspend(pause); nextStatus = 0; }
                if (clock.ElapsedMilliseconds >= nextStatus && (writing == null || writing.IsCompleted))
                {
                    if (writing != null && writing.IsFaulted) { var observed = writing.Exception; }
                    nextStatus = clock.ElapsedMilliseconds + 500;
                    PetRuntime status = Snapshot();
                    writing = Task.Run(delegate { JsonFile.Write(PetRuntimeProtocol.StatusPath, status); });
                }
            }
            catch (IOException) { settingsTime = DateTime.MinValue; }
            catch (SerializationException) { settingsTime = DateTime.MinValue; }
            catch (UnauthorizedAccessException) { settingsTime = DateTime.MinValue; }
        }
        private static bool OwnerAlive(int id)
        {
            if (id <= 0) return false;
            try { using (Process owner = Process.GetProcessById(id)) return !owner.HasExited && string.Equals(owner.ProcessName, "WeTypeSkinStudio", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        private PetRuntime Snapshot()
        {
            PetRuntime status = pet.Snapshot(); status.ProcessId = processId;
            status.Suspended = suspended; status.ControlRequestId = control == null ? null : control.RequestId;
            return status;
        }
        protected override void ExitThreadCore()
        {
            if (stopped) return; stopped = true;
            ready.Reset(); timer.Stop(); timer.Dispose(); pet.Dispose();
            if (writing != null) try { writing.GetAwaiter().GetResult(); } catch { }
            ready.Dispose(); stop.Dispose();
            base.ExitThreadCore();
        }
    }
}
