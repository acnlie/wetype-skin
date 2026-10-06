using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WeTypeSkinStudio
{
    internal static class PetHost
    {
        private static bool runtimeInstalled, legacyStopped;
        internal static ContextMenuStrip CreateTrayMenu(Action openEditor, Action closeEditor, Action closePet)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("皮肤工作室运行中") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示皮肤工作室", null, delegate { openEditor(); });
            menu.Items.Add("退出皮肤工作室", null, delegate { closeEditor(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关闭桌宠", null, delegate { closePet(); });
            return menu;
        }
        internal static PetRuntime ReadStatus()
        {
            try
            {
                using (var ready = EventWaitHandle.OpenExisting(PetRuntimeProtocol.ReadyName))
                    if (!ready.WaitOne(0)) return null;
                PetRuntime value = JsonFile.Read<PetRuntime>(PetRuntimeProtocol.StatusPath);
                return value.ProcessId > 0 && string.Equals(Native.ProcessPath(value.ProcessId), Native.FinalFilePath(PetRuntimeProtocol.RuntimePath), StringComparison.OrdinalIgnoreCase) ? value : null;
            }
            catch (WaitHandleCannotBeOpenedException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SerializationException) { return null; }
            catch (Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        internal static void Stop()
        {
            PetRuntime current = ReadStatus();
            StopSignal(PetRuntimeProtocol.StopName, PetRuntimeProtocol.MutexName);
            if (current != null)
            {
                try
                {
                    using (Process process = Process.GetProcessById(current.ProcessId))
                        if (!process.WaitForExit(4000)) throw new IOException("桌宠组件尚未退出。");
                }
                catch (ArgumentException) { }
            }
        }
        internal static void StopLegacy()
        {
            if (legacyStopped) return;
            int currentId; using (Process current = Process.GetCurrentProcess()) currentId = current.Id;
            var legacy = new System.Collections.Generic.List<Process>();
            foreach (Process process in Process.GetProcessesByName("WeTypeSkinStudio"))
            {
                if (process.Id == currentId) { process.Dispose(); continue; }
                bool match = false;
                try { match = string.Equals(Native.ProcessPath(process.Id), Path.Combine(ThemeStore.DirectoryPath, "runtime", "WeTypeSkinStudio.exe"), StringComparison.OrdinalIgnoreCase); }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                if (match) legacy.Add(process); else process.Dispose();
            }
            try
            {
                StopSignal("Local\\WeTypeSkinStudio.PetHost.Stop", "Local\\WeTypeSkinStudio.PetHost");
                foreach (Process process in legacy)
                    if (!process.WaitForExit(4000)) throw new IOException("旧版工作室后台尚未退出。");
                legacyStopped = true;
            }
            finally { foreach (Process process in legacy) process.Dispose(); }
        }
        private static void StopSignal(string eventName, string mutexName)
        {
            try { using (var signal = EventWaitHandle.OpenExisting(eventName)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return; }
            using (var mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try { try { acquired = mutex.WaitOne(3000); } catch (AbandonedMutexException) { acquired = true; } }
                finally { if (acquired) mutex.ReleaseMutex(); }
                if (!acquired) throw new IOException("桌宠组件尚未退出，请稍后重试。");
            }
        }
        internal static void Start(SkinTheme theme, string diagnostics = null)
        {
            using (var gate = new Mutex(false, PetRuntimeProtocol.StartName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = gate.WaitOne(7000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("桌宠组件正在更新，请稍后重试。");
                    StopLegacy();
                    if (!theme.PetEnabled || !theme.PetCandidate && !theme.PetToolbar) { Stop(); ConfigureStartup(theme); return; }
                    string engine = InstallRuntime();
                    ConfigureStartup(theme);
                    if (ReadStatus() != null) return;
                    string args = string.IsNullOrEmpty(diagnostics) ? "" : "--pet-diagnostics \"" + diagnostics + "\"";
                    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, PetRuntimeProtocol.ReadyName))
                    {
                        ready.Reset();
                        using (Process process = Process.Start(new ProcessStartInfo(engine, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(engine) }))
                        {
                            if (process != null && ready.WaitOne(5000) && !process.HasExited && ReadStatus() != null) return;
                            if (process != null)
                                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                                catch (Win32Exception) { }
                                catch (InvalidOperationException) { }
                            throw new IOException("独立桌宠组件未完成启动，请查看本地错误日志。");
                        }
                    }
                }
                finally { if (acquired) gate.ReleaseMutex(); }
            }
        }
        private static string InstallRuntime()
        {
            string engine = PetRuntimeProtocol.RuntimePath;
            if (runtimeInstalled && File.Exists(engine)) return engine;
            byte[] bytes = EmbeddedResources.Read("Dependencies.DeepSeekChan.InputPet.exe");
            if (!File.Exists(engine) || !File.ReadAllBytes(engine).SequenceEqual(bytes))
            {
                Stop(); JsonFile.AtomicWrite(engine, bytes);
            }
            runtimeInstalled = true;
            return engine;
        }
        internal static void ConfigureStartup(SkinTheme theme)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                key.DeleteValue("WeTypeSkinStudioPet", false);
                if (theme.PetStartWithWindows && theme.PetEnabled && (theme.PetCandidate || theme.PetToolbar))
                    key.SetValue("DeepSeekChanInputPet", "\"" + Native.FinalFilePath(InstallRuntime()) + "\"");
                else key.DeleteValue("DeepSeekChanInputPet", false);
            }
        }
        internal static void Suspend(bool value)
        {
            if (ReadStatus() == null) return;
            int owner; using (Process process = Process.GetCurrentProcess()) owner = process.Id;
            var control = new PetRuntimeControl { OwnerProcessId = owner, Suspended = value, RequestId = Guid.NewGuid().ToString("N") };
            JsonFile.Write(PetRuntimeProtocol.ControlPath, control);
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 2000)
            {
                PetRuntime state = ReadStatus();
                if (state == null || state.ControlRequestId == control.RequestId && state.Suspended == value) return;
                Thread.Sleep(30);
            }
            throw new IOException("桌宠组件未确认暂停状态。");
        }
        internal static string LaunchEditor(string saved, string fallback, Action<ProcessStartInfo> launch)
        {
            Exception failure = null;
            string first = string.IsNullOrWhiteSpace(saved) ? null : saved.Trim().Trim('"').Trim();
            foreach (string candidate in new[] { first, fallback })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string executable = Path.GetFullPath(candidate);
                    if (!File.Exists(executable)) continue;
                    launch(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) });
                    return executable;
                }
                catch (Win32Exception ex) { failure = ex; }
                catch (IOException ex) { failure = ex; }
                catch (UnauthorizedAccessException ex) { failure = ex; }
                catch (ArgumentException ex) { failure = ex; }
                catch (NotSupportedException ex) { failure = ex; }
            }
            throw new IOException("找不到可启动的皮肤工作室程序。", failure);
        }
    }
}
