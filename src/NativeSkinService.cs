using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TimeoutException = System.TimeoutException;
using System.Threading;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    [DataContract]
    internal sealed class NativeOperation
    {
        [DataMember] public bool Restore;
        [DataMember] public SkinTheme Theme;
    }
    [DataContract]
    internal sealed class NativeOperationResult
    {
        [DataMember] public bool Success;
        [DataMember] public string Error;
        [DataMember] public string Phase;
        [DataMember] public bool RolledBack;
        [DataMember] public bool InputRecovered;
        [DataMember] public List<string> RestartPaths = new List<string>();
    }
    [DataContract]
    internal sealed class NativeInputRecoveryResult
    {
        [DataMember] public bool Success;
        [DataMember] public string Error;
    }
    internal sealed class NativeSkinTransaction
    {
        private readonly NativePatchStore candidate;
        private readonly NativeToolbarStore toolbar;
        private readonly SkinTheme previousCandidate, previousToolbar;
        public NativeSkinTransaction(NativePatchStore candidateStore, NativeToolbarStore toolbarStore)
        {
            candidate = candidateStore; toolbar = toolbarStore;
            previousCandidate = candidate.Inspect(); previousToolbar = toolbar.Inspect();
        }
        public void Apply(SkinTheme theme)
        {
            candidate.Change(theme); toolbar.Change(theme);
        }
        public void Rollback()
        {
            var errors = new List<Exception>();
            try { candidate.Change(previousCandidate); } catch (Exception ex) { errors.Add(ex); }
            try { toolbar.Change(previousToolbar); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new IOException("原生文件回滚未全部完成。", new AggregateException(errors));
        }
    }

    internal sealed class NativeSkinInspection
    {
        public SkinTheme CandidateTheme;
        public SkinTheme ToolbarTheme;

        public bool HasChanges { get { return CandidateTheme != null || ToolbarTheme != null; } }
        public bool IsConsistent { get { return SameTheme(CandidateTheme, ToolbarTheme); } }
        public SkinTheme Theme { get { return CandidateTheme ?? ToolbarTheme; } }

        internal static bool SameTheme(SkinTheme left, SkinTheme right)
        {
            if (left == null || right == null) return left == null && right == null;
            return left.FormatVersion == right.FormatVersion
                && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                && string.Equals(left.Background, right.Background, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Foreground, right.Foreground, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Accent, right.Accent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Border, right.Border, StringComparison.OrdinalIgnoreCase)
                && left.Opacity == right.Opacity
                && left.CornerRadius == right.CornerRadius
                && left.ImageTint == right.ImageTint
                && string.Equals(left.BackgroundImageData, right.BackgroundImageData, StringComparison.Ordinal)
                && string.Equals(left.BackgroundImageName, right.BackgroundImageName, StringComparison.Ordinal)
                && left.ImagePositionX == right.ImagePositionX
                && left.ImagePositionY == right.ImagePositionY
                && left.ImageZoom == right.ImageZoom
                && left.PetEnabled == right.PetEnabled
                && left.PetCandidate == right.PetCandidate
                && left.PetToolbar == right.PetToolbar
                && left.PetAnimate == right.PetAnimate
                && left.PetSize == right.PetSize
                && left.PetStartWithWindows == right.PetStartWithWindows;
        }
    }

    internal sealed class NativeSkinService
    {
        // A theme may contain an embedded image. The editor accepts up to 12 MB of
        // base64 data, so the hand-off file must be larger than the old 64 KiB
        // color-only request limit.
        private const int MaxOperationRequestBytes = 16 * 1024 * 1024;
        private static string LastOperationErrorPath { get { return Path.Combine(ThemeStore.DirectoryPath, "last-operation-error.txt"); } }
        public SkinTheme Inspect()
        {
            NativeSkinInspection inspection = InspectDetailed();
            if (!inspection.IsConsistent)
                throw new InvalidDataException("候选框和工具条的皮肤状态不一致，请先还原官方文件后再应用。\n候选框和工具条必须作为一个事务同时更新。 ");
            return inspection.Theme;
        }

        public NativeSkinInspection InspectDetailed()
        {
            return InspectStores(NativePatchStore.Installed(), NativeToolbarStore.Installed(), ThemeStore.CurrentPath);
        }

        internal static NativeSkinInspection InspectStores(NativePatchStore candidate, NativeToolbarStore toolbar, string savedPath)
        {
            try
            {
                return new NativeSkinInspection { CandidateTheme = candidate.Inspect(), ToolbarTheme = toolbar.Inspect() };
            }
            catch (InvalidDataException)
            {
                using (var gate = new Mutex(false, "Local\\WeTypeSkinStudio.NativeOperation"))
                {
                    bool acquired;
                    try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("输入法应用或还原正在进行，稍后重新检测。");
                    try
                    {
                        var themes = new List<SkinTheme>();
                        AddSavedTheme(themes, savedPath);
                        AddSavedTheme(themes, Path.Combine(Path.GetDirectoryName(savedPath), "applied.wtskin.json"));
                        try { var state = JsonFile.Read<PatchState>(candidate.StatePath); if (state != null) { themes.Add(state.CurrentTheme); if (state.Pending) themes.Add(state.NextTheme); } } catch { }
                        try { var state = JsonFile.Read<ToolbarPatchState>(toolbar.StatePath); if (state != null) { themes.Add(state.CurrentTheme); if (state.Pending) themes.Add(state.NextTheme); } } catch { }
                        themes.AddRange(SkinTheme.Presets());
                        foreach (SkinTheme theme in themes)
                        {
                            if (theme == null) continue;
                            try { theme.Validate(); } catch (InvalidDataException) { continue; }
                            // Drafts are hints, never proof of an applied skin.
                            // Both native components and the background must
                            // match regenerated bytes before metadata is saved.
                            if (!candidate.MatchesAppliedTheme(theme) || !toolbar.MatchesAppliedTheme(theme)) continue;
                            candidate.RecoverFromAppliedTheme(theme);
                            toolbar.RecoverFromAppliedTheme(theme);
                            return new NativeSkinInspection { CandidateTheme = candidate.Inspect(), ToolbarTheme = toolbar.Inspect() };
                        }
                        throw;
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
        }
        private static void AddSavedTheme(List<SkinTheme> themes, string path)
        {
            try { themes.Add(ThemeStore.Load(path)); } catch (IOException) { } catch (SerializationException) { }
        }
        public static void RecordLastOperationError(Exception error)
        {
            try
            {
                Directory.CreateDirectory(ThemeStore.DirectoryPath);
                File.WriteAllText(LastOperationErrorPath, error == null ? "未知安装操作错误。" : error.ToString());
            }
            catch { }
        }
        internal static void RecordInspectionFailure(Exception error)
        {
            try
            {
                string statePath = NativePatchStore.Installed().StatePath;
                var report = new System.Text.StringBuilder();
                report.AppendLine(DateTime.UtcNow.ToString("o"));
                report.AppendLine("Executable: " + Application.ExecutablePath);
                report.AppendLine("User: " + WindowsIdentity.GetCurrent().Name);
                report.AppendLine("Elevated: " + Native.IsElevated());
                report.AppendLine("User data: " + ThemeStore.DirectoryPath);
                report.AppendLine(error.ToString());
                foreach (string path in new[] { NativeSkinPatch.TargetPath, NativeSkinPatch.AssetPath,
                    statePath, NativeToolbarStore.Installed().StatePath, ThemeStore.CurrentPath })
                {
                    report.AppendLine("Path: " + path);
                    report.AppendLine("Exists: " + File.Exists(path));
                    try
                    {
                        report.AppendLine("Resolved: " + Native.FinalFilePath(path));
                        report.AppendLine("SHA256: " + NativeSkinPatch.Hash(File.ReadAllBytes(path)));
                        if (path == statePath) report.AppendLine(File.ReadAllText(path));
                    }
                    catch (Exception readError) { report.AppendLine("Read error: " + readError); }
                }
                Directory.CreateDirectory(ThemeStore.DirectoryPath);
                File.WriteAllText(Path.Combine(ThemeStore.DirectoryPath, "native-inspection-error.txt"), report.ToString());
            }
            catch { }
        }
        public static void ClearLastOperationError()
        {
            try { if (File.Exists(LastOperationErrorPath)) File.Delete(LastOperationErrorPath); } catch { }
        }
        private static bool InstalledExecutable(string path)
        {
            foreach (string name in new string[] { "wetype_server", "wetype_update", "wetype_renderer" })
                if (string.Equals(path, Path.Combine(NativeSkinPatch.InstallDirectory, name + ".exe"), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static List<Process> InstalledProcesses()
        {
            var result = new List<Process>();
            try
            {
                // Stop the coordinator before its children. If update stays alive while
                // the files are replaced, it can respawn a renderer with a half-built
                // IPC graph and leave a visible but unusable candidate window behind.
                string[] names = new string[] { "wetype_update", "wetype_server", "wetype_renderer" };
                foreach (string name in names)
                    foreach (Process process in Process.GetProcessesByName(name))
                    {
                        bool keep = false;
                        try
                        {
                            keep = InstalledProcessPath(process) != null;
                            if (keep) result.Add(process);
                        }
                        catch (InvalidOperationException) { }
                        finally { if (!keep) process.Dispose(); }
                    }
                return result;
            }
            catch { foreach (Process process in result) process.Dispose(); throw; }
        }
        private static string InstalledProcessPath(Process process)
        {
            string expected = Path.Combine(NativeSkinPatch.InstallDirectory, process.ProcessName + ".exe");
            if (!InstalledExecutable(expected)) return null;
            string observed = Native.ProcessPath(process.Id);
            if (string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase)) return expected;
            // ReplaceFile can rename a still-mapped old executable to ~RF*.TMP.
            // Require BOTH the exact known temporary naming pattern and the
            // module's original installed path before treating it as ours.
            if (Regex.IsMatch(observed, "^" + Regex.Escape(expected) + @"~RF[0-9a-f]+\.TMP$", RegexOptions.IgnoreCase)
                && string.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase)) return expected;
            return null;
        }
        private static ServiceController StopManagementService()
        {
            const string name = "WeType Management Service";
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                if (key == null) return null;
                string configured = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ImagePath"))).Trim().Trim('"');
                if (!string.Equals(configured, Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_service.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("微信管理服务路径与已核对的安装不一致。");
            }
            var service = new ServiceController(name);
            try
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Stopped) { service.Dispose(); return null; }
                if (service.Status == ServiceControllerStatus.StartPending) service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                if (service.Status != ServiceControllerStatus.Running) throw new IOException("微信管理服务正在切换状态，请稍后重试。");
                service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                return service;
            }
            catch
            {
                // If stop reached the service before a timeout, restore it here
                // because the caller has not yet received ownership of the handle.
                try { service.Refresh(); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); } catch { }
                service.Dispose(); throw;
            }
        }
        private static bool IsAdministrator() { return Native.IsElevated(); }
        private static void ResumeManagementService(ref ServiceController service)
        {
            if (service == null) return;
            service.Refresh();
            if (service.Status == ServiceControllerStatus.StopPending)
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            service.Refresh();
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            service.Dispose(); service = null;
        }
        private static void StopInputProcesses(NativeOperationResult result, string resultPath)
        {
            // Record every normal startup component before stopping anything,
            // including an operation while the input method was not running.
            foreach (string name in new[] { "wetype_server", "wetype_renderer", "wetype_update" })
            {
                string path = Path.Combine(NativeSkinPatch.InstallDirectory, name + ".exe");
                if (!result.RestartPaths.Contains(path)) result.RestartPaths.Add(path);
            }
            JsonFile.Write(resultPath, result);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var processes = InstalledProcesses();
                try
                {
                    if (processes.Count == 0) return;
                    foreach (Process process in processes)
                    {
                        if (process.HasExited) continue;
                        if (InstalledProcessPath(process) == null) throw new IOException("输入法进程路径发生变化。");
                        process.Kill();
                        if (!process.WaitForExit(5000)) throw new IOException("微信输入法进程未能停止。");
                    }
                }
                finally { foreach (Process process in processes) process.Dispose(); }
                Thread.Sleep(100);
            }
            throw new IOException("微信输入法进程持续重新启动，已取消写入。");
        }
        private static string OperationPath(string id, string suffix)
        {
            Guid guid;
            if (!Guid.TryParseExact(id, "N", out guid)) throw new InvalidDataException("操作编号无效。");
            return Path.Combine(ThemeStore.DirectoryPath, "operations", guid.ToString("N") + suffix);
        }
        public static int RunInputRecovery(string id)
        {
            string recoveryPath = OperationPath(id, ".recovery.json");
            var result = new NativeInputRecoveryResult();
            try
            {
                if (IsAdministrator()) throw new InvalidOperationException("输入法恢复助手需要普通权限。");
                var restart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ReadRestartPaths(OperationPath(id, ".result.json"), restart);
                if (restart.Count == 0) throw new InvalidDataException("没有已核对的输入法恢复路径。");
                RestartInputMethod(restart);
                if (!WaitForInputReady(restart, DateTime.UtcNow.AddSeconds(15)))
                    throw new IOException("回滚后未能恢复候选窗口和输入连接。");
                result.Success = true;
            }
            catch (Exception ex) { result.Error = ex.Message; }
            JsonFile.Write(recoveryPath, result);
            return result.Success ? 0 : 1;
        }
        public static int RunElevatedOperation(string id)
        {
            string requestPath = OperationPath(id, ".request.json"), resultPath = OperationPath(id, ".result.json");
            string readyPath = OperationPath(id, ".ready"), commitPath = OperationPath(id, ".commit"), cancelPath = OperationPath(id, ".cancel");
            string writtenPath = OperationPath(id, ".written"), verifiedPath = OperationPath(id, ".verified");
            foreach (string path in new string[] { requestPath, resultPath, readyPath, commitPath, cancelPath, writtenPath, verifiedPath }) JsonFile.RejectReparsePath(path);
            var result = new NativeOperationResult();
            ServiceController managementService = null;
            NativeSkinTransaction transaction = null;
            bool writeStarted = false;
            try
            {
                if (!IsAdministrator()) throw new UnauthorizedAccessException("写入微信安装目录需要 Windows 管理员权限。");
                if (new FileInfo(requestPath).Length > MaxOperationRequestBytes) throw new InvalidDataException("操作请求过大。");
                NativeOperation request = JsonFile.Read<NativeOperation>(requestPath);
                if (request == null || (!request.Restore && request.Theme == null)) throw new InvalidDataException("操作请求无效。");
                NativePatchStore appStore = NativePatchStore.Installed();
                NativeToolbarStore toolbarStore = NativeToolbarStore.Installed();
                transaction = new NativeSkinTransaction(appStore, toolbarStore);
                appStore.Prepare(request.Restore ? null : request.Theme);
                toolbarStore.Prepare(request.Restore ? null : request.Theme);
                JsonFile.AtomicWrite(readyPath, new byte[] { 1 });
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(commitPath))
                {
                    if (File.Exists(cancelPath)) throw new OperationCanceledException("安装操作已取消。");
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("等待应用确认超时，安装文件保持原状态。");
                    Thread.Sleep(50);
                }
                JsonFile.RejectReparsePath(commitPath);
                if (File.Exists(cancelPath)) throw new OperationCanceledException("安装操作已取消。");
                managementService = StopManagementService();
                StopInputProcesses(result, resultPath);
                SkinTheme nextTheme = request.Restore ? null : request.Theme;
                writeStarted = true;
                transaction.Apply(nextTheme);
                ResumeManagementService(ref managementService);
                result.Phase = "awaiting-runtime"; JsonFile.Write(resultPath, result);
                JsonFile.AtomicWrite(writtenPath, new byte[] { 1 });
                // Keep the elevated helper available until the ordinary-token
                // editor verifies startup. A dead editor or failed startup rolls
                // both stores back without requiring a second UAC prompt.
                deadline = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(verifiedPath))
                {
                    if (File.Exists(cancelPath)) throw new OperationCanceledException("运行验证失败或操作已取消。");
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("未收到输入连接验证结果。");
                    Thread.Sleep(50);
                }
                JsonFile.RejectReparsePath(verifiedPath);
                if (File.Exists(cancelPath)) throw new OperationCanceledException("运行验证已取消。");
                appStore.Inspect(); toolbarStore.Inspect();
                result.Success = true; result.Phase = "committed";
            }
            catch (Exception ex)
            {
                result.Error = ex.Message; result.Phase = "failed";
                if (writeStarted && transaction != null)
                {
                    try
                    {
                        if (managementService == null) managementService = StopManagementService();
                        StopInputProcesses(result, resultPath);
                        transaction.Rollback(); result.RolledBack = true; result.Phase = "rolled-back";
                        result.Error += " 已回滚到操作前的皮肤文件。";
                    }
                    catch (Exception rollbackError)
                    {
                        result.Phase = "recovery-required";
                        result.Error += " 回滚失败：" + rollbackError.Message + " 请使用还原官方重试。";
                    }
                }
            }
            finally
            {
                if (managementService != null)
                {
                    try { ResumeManagementService(ref managementService); }
                    catch (Exception ex) { result.Success = false; result.Error = (result.Error ?? "") + " 微信管理服务恢复失败：" + ex.Message; }
                    finally { if (managementService != null) managementService.Dispose(); }
                }
            }
            if (!result.Success && result.RestartPaths.Count > 0)
            {
                try
                {
                    // uiAccess executables must be ShellExecuted by a normal-token
                    // process; direct CreateProcessAsUser can reject the renderer.
                    JsonFile.Write(resultPath, result);
                    string recoveryPath = OperationPath(id, ".recovery.json");
                    JsonFile.RejectReparsePath(recoveryPath);
                    int exitCode = Native.StartUnelevatedAndWait(Application.ExecutablePath, "--recover-native-input " + id,
                        NativeSkinPatch.InstallDirectory, 25000);
                    var recovery = File.Exists(recoveryPath) ? JsonFile.Read<NativeInputRecoveryResult>(recoveryPath) : null;
                    if (exitCode != 0 || recovery == null || !recovery.Success)
                        throw new IOException(recovery == null ? "未收到普通权限恢复结果。" : recovery.Error);
                    result.InputRecovered = true;
                }
                catch (Exception ex) { result.Error += " 输入法恢复启动失败：" + ex.Message; }
            }
            JsonFile.Write(resultPath, result); return result.Success ? 0 : 1;
        }
        private static void Report(Action<int, int, string> progress, int step, string message)
        {
            if (progress == null) return;
            try { progress(step, 5, message); } catch { }
        }
        private void Commit(SkinTheme theme, HashSet<string> restart, Action<int, int, string> progress)
        {
            string id = Guid.NewGuid().ToString("N");
            string requestPath = OperationPath(id, ".request.json"), resultPath = OperationPath(id, ".result.json");
            string readyPath = OperationPath(id, ".ready"), commitPath = OperationPath(id, ".commit"), cancelPath = OperationPath(id, ".cancel");
            string writtenPath = OperationPath(id, ".written"), verifiedPath = OperationPath(id, ".verified");
            Process helper = null;
            bool helperFinished = true;
            JsonFile.Write(requestPath, new NativeOperation { Restore = theme == null, Theme = theme == null ? null : NativeSkinPatch.NativeTheme(theme) });
            try
            {
                Report(progress, 2, "等待 Windows 管理员授权");
                try
                {
                    var info = new ProcessStartInfo(Application.ExecutablePath, "--native-operation " + id) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                    helper = Process.Start(info);
                    if (helper == null) throw new IOException("管理员写入进程未能启动。");
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223) throw new OperationCanceledException("已取消 Windows 管理员授权，输入法与安装文件保持原状态。");
                    throw;
                }
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!File.Exists(readyPath) && !helper.HasExited)
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("管理员写入助手准备超时。");
                    Thread.Sleep(50);
                }
                if (!helper.HasExited && File.Exists(readyPath))
                {
                    // The editor deliberately runs unelevated. Querying the uiAccess
                    // renderer from this token can fail with ERROR_ACCESS_DENIED.
                    // The elevated helper performs the same path validation and
                    // persists the verified restart list in NativeOperationResult.
                    Report(progress, 3, "正在停止输入法并写入原生文件");
                    JsonFile.AtomicWrite(commitPath, new byte[] { 1 });
                    deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!File.Exists(writtenPath) && !helper.HasExited)
                    {
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("管理员写入未及时完成。");
                        Thread.Sleep(50);
                    }
                    if (File.Exists(writtenPath) && !helper.HasExited)
                    {
                        ReadRestartPaths(resultPath, restart);
                        Report(progress, 4, "正在按官方顺序重启输入法");
                        RestartInputMethod(restart);
                        Report(progress, 5, theme == null ? "正在验证官方候选输入框" : "正在验证候选输入框和输入连接");
                        if (!WaitForInputReady(restart, DateTime.UtcNow.AddSeconds(15)))
                            throw new IOException("输入法启动后未能建立候选窗口和输入连接。");
                        JsonFile.AtomicWrite(verifiedPath, new byte[] { 1 });
                        if (!helper.WaitForExit(15000)) throw new TimeoutException("输入连接已建立，但文件事务未完成确认。");
                    }
                }
                if (!File.Exists(resultPath)) throw new IOException("未收到管理员写入结果，请重新检查安装文件状态。");
                var result = JsonFile.Read<NativeOperationResult>(resultPath);
                if (result == null || !result.Success) throw new IOException(result == null ? "写入结果无效。" : result.Error);
            }
            catch (Exception ex)
            {
                if (helper != null && !helper.HasExited)
                {
                    Report(progress, 3, "操作未完成，正在回滚原生文件");
                    JsonFile.AtomicWrite(cancelPath, new byte[] { 1 });
                    helperFinished = helper.WaitForExit(45000);
                    if (helperFinished && File.Exists(resultPath))
                    {
                        var result = JsonFile.Read<NativeOperationResult>(resultPath);
                        if (result != null && !string.IsNullOrEmpty(result.Error))
                            throw new IOException(ex.Message + " " + result.Error, ex);
                    }
                }
                throw;
            }
            finally
            {
                if (helper != null)
                {
                    try
                    {
                        if (!helper.HasExited)
                        {
                            JsonFile.AtomicWrite(cancelPath, new byte[] { 1 });
                            helperFinished = helper.WaitForExit(15000);
                        }
                    }
                    finally { helper.Dispose(); }
                }
                if (helperFinished)
                {
                    ReadRestartPaths(resultPath, restart);
                    foreach (string path in new string[] { requestPath, resultPath, readyPath, commitPath, cancelPath, writtenPath, verifiedPath, OperationPath(id, ".recovery.json") })
                        if (File.Exists(path)) File.Delete(path);
                }
                else restart.Clear();
            }
        }
        private static void ReadRestartPaths(string resultPath, HashSet<string> restart)
        {
            if (!File.Exists(resultPath)) return;
            var result = JsonFile.Read<NativeOperationResult>(resultPath);
            if (result == null || result.RestartPaths == null) return;
            foreach (string path in result.RestartPaths)
            {
                if (!InstalledExecutable(path)) throw new InvalidDataException("重启路径不属于已核对的输入法安装。");
                restart.Add(path);
            }
        }
        // Only stopping and writing are elevated; the editor restarts the IME using its ordinary token.
        public void ChangeInstallation(SkinTheme theme, Action<int, int, string> progress = null)
        {
            if (IsAdministrator()) throw new InvalidOperationException("请正常启动皮肤工作室；只有文件写入助手需要管理员权限，避免输入法以高权限运行。");
            using (var mutex = new Mutex(false, "Local\\WeTypeSkinStudio.NativeOperation"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("另一个应用或还原操作正在进行。");
                try { ChangeInstallationLocked(theme, progress); } finally { mutex.ReleaseMutex(); }
            }
        }
        private void ChangeInstallationLocked(SkinTheme theme, Action<int, int, string> progress)
        {
            Report(progress, 1, theme == null ? "正在准备还原官方文件" : "正在准备皮肤文件");
            NativePatchStore.Installed().Prepare(theme);
            NativeToolbarStore.Installed().Prepare(theme);
            var restart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Exception failure = null;
            try
            {
                Commit(theme, restart, progress);
            }
            catch (Exception ex) { failure = ex; }
            if (failure != null && restart.Count > 0)
            {
                Report(progress, 4, "正在恢复操作前的输入法运行状态");
                try { RestartInputMethod(restart); }
                catch (Exception ex) { failure = new IOException("安装操作已结束，但微信输入法重启失败，请手动启动。" + ex.Message, failure); }
                Report(progress, 5, "正在检查回滚后的输入连接");
                if (!WaitForInputReady(restart, DateTime.UtcNow.AddSeconds(15)))
                    failure = new IOException(failure.Message + " 输入连接尚未恢复，请重新启动微信输入法。", failure);
            }
            if (failure != null) throw failure;
            ClearLastOperationError();
        }

        private static bool IsRunning(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            foreach (Process process in Process.GetProcessesByName(name))
            using (process)
            {
                try
                {
                    if (string.Equals(Native.ProcessPath(process.Id), path, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Win32Exception)
                {
                    // uiAccess renderer processes can reject a normal-token path query.
                    // The candidate-window ownership check below verifies that process.
                    if (name.Equals("wetype_renderer", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
            }
            return false;
        }

        private static void Launch(string path)
        {
            var info = new ProcessStartInfo(path)
            {
                WorkingDirectory = NativeSkinPatch.InstallDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process launched = Process.Start(info);
            if (launched != null) launched.Dispose();
        }

        private static bool HasPipe(string suffix)
        {
            try
            {
                foreach (string pipe in Directory.GetFiles("\\\\.\\pipe\\"))
                    if (Path.GetFileName(pipe).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        private static bool InputPipesReady(bool expectUpdate)
        {
            if (!HasPipe(".server_for_renderer") || !HasPipe(".renderer_for_tip") || !HasPipe(".session_v2")) return false;
            return !expectUpdate || (HasPipe(".update_for_renderer") && HasPipe(".update_session_v2"));
        }

        private static bool CandidateBelongsToRenderer()
        {
            IntPtr candidate = Native.FindWindow("wetype.flutter.setting", "wetype_candidate");
            uint pid;
            if (candidate == IntPtr.Zero || Native.GetWindowThreadProcessId(candidate, out pid) == 0 || pid == 0) return false;
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                    return process.ProcessName.Equals("wetype_renderer", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static bool WaitForInputReady(HashSet<string> restart, DateTime deadline)
        {
            string server = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_server.exe");
            string renderer = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_renderer.exe");
            string update = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_update.exe");
            bool expectUpdate = restart.Contains(update);
            while (DateTime.UtcNow < deadline)
            {
                bool processes = IsRunning(server) && IsRunning(renderer) && (!expectUpdate || IsRunning(update));
                if (processes && CandidateBelongsToRenderer() && InputPipesReady(expectUpdate)) return true;
                Thread.Sleep(150);
            }
            return false;
        }

        private static void RestartInputMethod(HashSet<string> restart)
        {
            string server = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_server.exe");
            string renderer = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_renderer.exe");
            string update = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_update.exe");
            // WeType's per-user startup path initializes the engine, then the Flutter
            // renderer, and only then the update/status process. Starting update first
            // leaves server pipes behind while renderer startup is skipped on this build.
            if (restart.Contains(server) && !IsRunning(server)) Launch(server);
            if (restart.Contains(renderer) && !IsRunning(renderer)) Launch(renderer);
            if (restart.Contains(update) && !IsRunning(update)) Launch(update);
        }
    }
}
