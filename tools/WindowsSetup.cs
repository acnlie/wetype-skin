using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class WindowsSetup
{
    private const string Product = "WeType Skin Studio";
    private const string RuntimeKey = @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WeTypeSkinStudio";
    private static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WeTypeSkinStudio");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length == 2 && args[0] == "--extract") { Extract(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--remove-installed")
            {
                int parentPID;
                if (Int32.TryParse(args[1], out parentPID))
                    try { using (Process parent = Process.GetProcessById(parentPID)) parent.WaitForExit(5000); }
                    catch (ArgumentException) { }
                RemoveInstalled(); return 0;
            }
            if (args.Length == 1 && args[0] == "--uninstall")
            {
                if (MessageBox.Show("卸载工作室？皮肤设置和备份会保留。已应用的输入法皮肤请先在工作室中还原。", Product, MessageBoxButtons.OKCancel) != DialogResult.OK) return 0;
                string helper = Path.Combine(Path.GetTempPath(), "WeTypeSkinUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, helper);
                Process.Start(new ProcessStartInfo(helper, "--remove-installed " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                return 0;
            }
            if (Environment.OSVersion.Version.Major < 10 || Environment.OSVersion.Version.Build < 19041 || !Environment.Is64BitOperatingSystem)
                throw new InvalidOperationException("需要 Windows 10 2004 及以上的 64 位系统。");
            if (args.Length == 1 && args[0] == "--silent")
            {
                return Install(delegate(int value, string message) { }) ? 3010 : 0;
            }
            Application.Run(new SetupWindow());
            return 0;
        }
        catch (Exception error) { MessageBox.Show(error.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }

    private static bool HasRuntime()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(RuntimeKey))
                {
                    string version = key == null ? null : key.GetValue("pv") as string;
                    if (!String.IsNullOrEmpty(version) && version != "0.0.0.0") return true;
                }
        return false;
    }

    private static void Extract(string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using (Stream bytes = Assembly.GetExecutingAssembly().GetManifestResourceStream("WeTypeSkinStudio.SetupPayload"))
        {
            if (bytes == null) throw new InvalidOperationException("安装包内容缺失。");
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Read))
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包路径无效。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream source = entry.Open())
                    using (FileStream destination = File.Create(target)) source.CopyTo(destination);
                }
        }
    }

    private static void Shortcut(string path, string executable, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        Type type = Type.GetTypeFromProgID("WScript.Shell", true);
        object shell = Activator.CreateInstance(type);
        object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
        Type linkType = link.GetType();
        linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { executable });
        linkType.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
        linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { InstallDirectory });
        linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    }

    private static bool Install(Action<int, string> progress)
    {
        string staging = Path.Combine(Path.GetTempPath(), "WeTypeSkinSetup-" + Guid.NewGuid().ToString("N"));
        bool restart = false;
        try
        {
            progress(10, "正在准备安装文件");
            Extract(staging);
            if (!HasRuntime())
            {
                progress(35, "正在安装随包提供的 WebView2，无需联网");
                using (Process runtime = Process.Start(new ProcessStartInfo(Path.Combine(staging, "runtime", "WebView2OfflineSetup.exe"), "/silent /install") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!runtime.WaitForExit(180000)) throw new InvalidOperationException("WebView2 安装超时，请稍后重新运行安装包。");
                    restart = runtime.ExitCode == 3010;
                    if (runtime.ExitCode != 0 && !restart) throw new InvalidOperationException("WebView2 安装失败：" + runtime.ExitCode);
                    if (!restart && !HasRuntime()) throw new InvalidOperationException("无法确认 WebView2 安装完成，请重新运行安装包。");
                }
            }
            progress(75, "正在安装皮肤工作室和独立桌宠");
            string source = Path.Combine(staging, "app");
            Directory.CreateDirectory(InstallDirectory);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(InstallDirectory, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            progress(90, "正在创建开始菜单入口");
            string program = Path.Combine(InstallDirectory, "WeTypeSkinStudio.exe");
            string uninstaller = Path.Combine(InstallDirectory, "Uninstall.exe");
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product);
            Shortcut(Path.Combine(menu, Product + ".lnk"), program, "");
            Shortcut(Path.Combine(menu, "卸载.lnk"), uninstaller, "--uninstall");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", Product);
                key.SetValue("DisplayVersion", "0.3.0");
                key.SetValue("Publisher", "Tokupup");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("DisplayIcon", program);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            progress(100, restart ? "安装完成，重启系统后即可使用" : "安装完成");
            return restart;
        }
        finally { if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch { } }
    }

    private static void RemoveInstalled()
    {
        // Only the fixed per-user installation is removable; configuration
        // and input-method backups live in a different directory.
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product);
        foreach (string name in new[] { Product + ".lnk", "卸载.lnk" }) File.Delete(Path.Combine(menu, name));
        if (Directory.Exists(menu) && Directory.GetFileSystemEntries(menu).Length == 0) Directory.Delete(menu);
        if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, true);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        MessageBox.Show("工作室已卸载，皮肤设置和恢复备份已保留。", Product);
    }

    private sealed class SetupWindow : Form
    {
        private readonly Label status = new Label { AutoSize = false, Left = 28, Top = 86, Width = 430, Height = 48, Text = "安装到当前用户，无需另行下载运行环境。" };
        private readonly ProgressBar progress = new ProgressBar { Left = 28, Top = 144, Width = 430, Height = 8 };
        private readonly Button install = new Button { Left = 354, Top = 190, Width = 104, Height = 34, Text = "安装" };
        private readonly CheckBox launch = new CheckBox { Left = 28, Top = 198, Width = 270, Text = "完成后打开皮肤工作室", Checked = true };
        private bool busy, complete, restart;
        internal SetupWindow()
        {
            Text = Product + " · 安装"; ClientSize = new Size(486, 248); FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.White;
            Controls.Add(new Label { Left = 28, Top = 28, Width = 430, Height = 38, Text = Product, Font = new Font("Segoe UI", 20, FontStyle.Bold) });
            Controls.AddRange(new Control[] { status, progress, install, launch });
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            install.Click += async delegate {
                if (complete) { if (launch.Checked && !restart) Process.Start(Path.Combine(InstallDirectory, "WeTypeSkinStudio.exe")); Close(); return; }
                busy = true; install.Enabled = false; launch.Enabled = false;
                try
                {
                    restart = await Task.Run(() => Install((value, message) => BeginInvoke((Action)(() => { progress.Value = value; status.Text = message; }))));
                    complete = true; install.Text = "完成";
                }
                catch (Exception error) { status.Text = "安装未完成，请关闭工作室后重试。"; MessageBox.Show(this, error.Message, Product, MessageBoxButtons.OK, MessageBoxIcon.Error); install.Text = "重试"; }
                finally { busy = false; install.Enabled = true; launch.Enabled = true; }
            };
        }
    }
}
