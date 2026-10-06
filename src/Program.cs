using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                // The elevated helper never creates a WebView2 instance. Keeping this
                // path independent also prevents a missing runtime from making the
                // normal editor wait until the UAC operation times out.
                if (args.Length == 2 && (args[0] == "--native-operation" || args[0] == "--recover-native-input")) return Run(args);
                FrontendAssets.RegisterDependencies();
                return Run(args);
            }
            catch (Exception ex)
            {
                try
                {
                    Directory.CreateDirectory(ThemeStore.DirectoryPath);
                    File.WriteAllText(Path.Combine(ThemeStore.DirectoryPath, "last-operation-error.txt"), ex.ToString());
                }
                catch { }
                MessageBox.Show("皮肤工作室启动失败：\n\n" + ex.Message + "\n\n详细信息已保存到：" +
                    Path.Combine(ThemeStore.DirectoryPath, "last-operation-error.txt"),
                    "微信输入法皮肤工作室", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            if (args.Length == 2 && args[0] == "--native-operation")
            {
                try { return NativeSkinService.RunElevatedOperation(args[1]); }
                catch { return 1; }
            }
            if (args.Length == 2 && args[0] == "--recover-native-input")
            {
                try { return NativeSkinService.RunInputRecovery(args[1]); }
                catch { return 1; }
            }
            // The editor must stay unelevated so the restarted input method keeps
            // its normal uiAccess token. A shortcut or UAC launch can otherwise
            // make the editor itself elevated and leave the user at a dead-end.
            try
            {
                if (Native.IsElevated())
                {
                    Native.StartUnelevated(Application.ExecutablePath, JoinArguments(args), Environment.CurrentDirectory);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("皮肤工作室需要以普通权限运行。请关闭此窗口后直接双击程序；\n\n" + ex.Message,
                    "无法切换启动权限", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--pet-host")
            {
                int diagnostics = Array.IndexOf(args, "--pet-diagnostics");
                PetHost.Start(ThemeStore.Load(ThemeStore.CurrentPath), diagnostics >= 0 && diagnostics + 1 < args.Length ? args[diagnostics + 1] : null);
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--native-check")
            {
                string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
                try
                {
                    var service = new NativeSkinService(); var inspection = service.InspectDetailed();
                    var store = NativePatchStore.Installed(); var toolbar = NativeToolbarStore.Installed();
                    foreach (SkinTheme theme in SkinTheme.Presets())
                    {
                        var plan = store.Prepare(theme); toolbar.Prepare(theme);
                        JsonFile.Write(Path.Combine(output, theme.Name + ".patch-plan.json"), new PatchAudit { Time = DateTime.UtcNow.ToString("o"),
                            Operation = "offline-plan", Phase = "verified", Version = NativeSkinPatch.Version, OriginalHash = NativeSkinPatch.OriginalHash,
                            NewHash = plan.Hash, Edits = plan.Edits });
                    }
                    File.WriteAllText(Path.Combine(output, "native-check.txt"), "Supported version: " + NativeSkinPatch.Version + "\r\nInstallation: " + NativeSkinPatch.TargetPath +
                        "\r\nCurrent: " + (!inspection.HasChanges ? "official" : inspection.IsConsistent ? inspection.Theme.Name : "partial / inconsistent") +
                        "\r\nPASS: six candidate and toolbar patch plans; installation unchanged\r\n"); return 0;
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(output, "native-check-failure.txt"), ex.ToString()); return 1; }
            }
            if ((args.Length == 2 && args[0] == "--apply-native") || (args.Length == 1 && args[0] == "--restore-native"))
            {
                try
                {
                    new NativeSkinService().ChangeInstallation(args[0] == "--restore-native" ? null : ThemeStore.Load(args[1]));
                    return 0;
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(ThemeStore.DirectoryPath);
                    File.WriteAllText(Path.Combine(ThemeStore.DirectoryPath, "last-operation-error.txt"), ex.ToString()); return 1;
                }
            }
            if (args.Length >= 2 && args[0] == "--self-test")
            {
                try { SelfTests.Run(args[1]); return 0; }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "test-failure.txt"), ex.ToString()); return 1; }
            }
            if (args.Length == 2 && args[0] == "--verify-native-state")
            {
                try { NativeStateChecks.Run(Path.GetFullPath(args[1])); return 0; }
                catch (Exception error) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), error.ToString()); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--verify-update")
            {
                try { UpdateChecks.Run(args[1]); return 0; }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--verify-pet-render")
            {
                try { PetRenderChecks.Run(args[1]); return 0; }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--verify-pet-visibility")
            {
                try { PetRenderChecks.Visibility(args[1]); return 0; }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--verify-tray-punctuation")
            {
                try { TrayPunctuationChecks.Run(args[1]); return 0; }
                catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "failure.txt"), ex.ToString()); return 1; }
            }
            if (args.Length >= 2 && args[0] == "--render-editor")
            {
                using (var editor = new EditorForm())
                {
                    editor.NonActivatingPreview = true; editor.ScreenshotPath = args[1]; Application.Run(editor);
                }
                return 0;
            }
            bool first;
            using (var mutex = new Mutex(true, "Local\\WeTypeSkinStudio.Editor", out first))
            {
                if (!first) { MessageBox.Show("皮肤工作室已经运行，请切换到已有窗口。", "微信输入法皮肤工作室"); return 0; }
                PetHost.StopLegacy();
                using (var editor = new EditorForm())
                {
                    editor.EnableOnStart = Array.IndexOf(args, "--enable") >= 0;
                    int debug = Array.IndexOf(args, "--web-debug");
                    if (debug >= 0 && debug + 1 < args.Length)
                    {
                        int port; if (int.TryParse(args[debug + 1], out port) && port >= 1024 && port <= 65535) editor.DebugPort = port;
                    }
                    int petDiagnostics = Array.IndexOf(args, "--pet-diagnostics");
                    if (editor.DebugPort > 0 && petDiagnostics >= 0 && petDiagnostics + 1 < args.Length)
                        editor.PetDiagnosticsPath = Path.GetFullPath(args[petDiagnostics + 1]);
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        MessageBox.Show(editor, "操作未能完成：" + e.Exception.Message, "皮肤工作室", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    };
                    Application.Run(editor);
                }
            }
            return 0;
        }

        private static string JoinArguments(string[] args)
        {
            var result = new System.Text.StringBuilder();
            foreach (string value in args)
            {
                if (result.Length > 0) result.Append(' ');
                if (value == null) { result.Append("\"\""); continue; }
                result.Append('"');
                int slashes = 0;
                foreach (char c in value)
                {
                    if (c == '\\') { slashes++; continue; }
                    if (c == '"') { result.Append(new string('\\', slashes * 2 + 1)); result.Append('"'); slashes = 0; continue; }
                    if (slashes > 0) { result.Append(new string('\\', slashes)); slashes = 0; }
                    result.Append(c);
                }
                if (slashes > 0) result.Append(new string('\\', slashes * 2));
                result.Append('"');
            }
            return result.ToString();
        }
    }
}
