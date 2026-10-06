using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WeTypeSkinStudio
{
    internal static class FrontendAssets
    {
        internal static readonly string[] Names = { "index.html", "studio.css", "studio.js", "lucide.min.js" };
        public static void RegisterDependencies()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                string name = new AssemblyName(args.Name).Name;
                if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms") return null;
                return Assembly.Load(Read("Dependencies." + name + ".dll"));
            };
        }
        public static byte[] Read(string name)
        {
            return EmbeddedResources.Read(name);
        }
        public static string Extract()
        {
            string version = NativeSkinPatch.Hash(Read("Frontend.studio.js")).Substring(0, 16);
            string folder = Path.Combine(ThemeStore.DirectoryPath, "frontend", "v0.3.0-" + version);
            foreach (string name in Names) JsonFile.AtomicWrite(Path.Combine(folder, name), Read("Frontend." + name));
            using (var stream = new MemoryStream(Read("Pet.clips.json")))
            {
                var manifest = (PetClipManifest)new DataContractJsonSerializer(typeof(PetClipManifest)).ReadObject(stream);
                foreach (PetClip clip in manifest.Clips)
                    JsonFile.AtomicWrite(Path.Combine(folder, "pet", clip.File), Read("Pet." + clip.File));
            }
            JsonFile.AtomicWrite(Path.Combine(folder, "WebView2Loader.dll"), Read("Dependencies.WebView2Loader.dll"));
            return folder;
        }
        public static string InlineHtml()
        {
            string html = Encoding.UTF8.GetString(Read("Frontend.index.html"));
            string css = Encoding.UTF8.GetString(Read("Frontend.studio.css"));
            string icons = Encoding.UTF8.GetString(Read("Frontend.lucide.min.js"));
            string script = Encoding.UTF8.GetString(Read("Frontend.studio.js"));
            script = "window.petAssets=" + PetAssets.BrowserJson() + ";" + script;
            html = html.Replace("<link rel=\"stylesheet\" href=\"studio.css\">", "<style>" + css + "</style>");
            html = html.Replace("<script defer src=\"lucide.min.js\"></script>", "<script>" + icons + "</script>");
            html = html.Replace("<script defer src=\"studio.js\"></script>", "<script>document.addEventListener('DOMContentLoaded', function () {" + script + "});</script>");
            html = html.Replace("script-src 'self'", "script-src 'self' 'unsafe-inline'");
            return html;
        }
    }
    [DataContract]
    internal sealed class FrontendRequest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "action")] public string Action { get; set; }
        [DataMember(Name = "theme")] public SkinTheme Theme { get; set; }
    }
    [DataContract]
    internal sealed class FrontendState
    {
        [DataMember(Name = "current")] public SkinTheme Current;
        [DataMember(Name = "presets")] public SkinTheme[] Presets;
        [DataMember(Name = "applied")] public SkinTheme Applied;
        [DataMember(Name = "compatible")] public bool Compatible;
        [DataMember(Name = "version")] public string Version = NativeSkinPatch.Version;
        [DataMember(Name = "message")] public string Message;
        [DataMember(Name = "cancelled")] public bool Cancelled;
        [DataMember(Name = "operationError")] public string OperationError;
        [DataMember(Name = "recoveryRequired")] public bool RecoveryRequired;
        [DataMember(Name = "inspectionError")] public string InspectionError;
        [DataMember(Name = "pet")] public PetRuntime Pet;
    }
    [DataContract]
    internal sealed class FrontendResponse
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "ok")] public bool Ok;
        [DataMember(Name = "result")] public FrontendState Result;
        [DataMember(Name = "error")] public string Error;
        [DataMember(Name = "progress")] public bool Progress;
        [DataMember(Name = "action")] public string Action;
        [DataMember(Name = "step")] public int Step;
        [DataMember(Name = "total")] public int Total;
        [DataMember(Name = "progressMessage")] public string ProgressMessage;
        [DataMember(Name = "pet")] public PetRuntime Pet;
    }
    internal sealed class EditorForm : Form
    {
        private const string Origin = "https://studio.wetype.local";
        private WebView2 view;
        private readonly NativeSkinService service = new NativeSkinService();
        private readonly Func<NativeSkinInspection> inspectNative;
        private PetWorker pet;
        private NotifyIcon tray;
        private string petError;
        private SkinTheme current = new SkinTheme(), applied;
        private bool compatible, recoveryRequired, busy, closing, ready, inlineNavigation;
        private string message, operationError;
        private string inspectionError;
        private bool refreshingInspection;
        internal int DebugPort;
        internal string PetDiagnosticsPath;
        internal bool NonActivatingPreview, EnableOnStart;
        internal string ScreenshotPath;
        protected override bool ShowWithoutActivation { get { return NonActivatingPreview; } }

        public EditorForm() : this(null) { }
        internal EditorForm(Func<NativeSkinInspection> inspector)
        {
            inspectNative = inspector ?? service.InspectDetailed;
            Text = "微信输入法皮肤工作室"; ClientSize = new Size(1280, 800); MinimumSize = new Size(860, 620);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(247, 248, 250);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { if (File.Exists(ThemeStore.CurrentPath)) current = ThemeStore.Load(ThemeStore.CurrentPath); }
            catch (Exception ex) { message = "上次的皮肤无法读取：" + ex.Message; }
        }
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                RefreshNativeInspection();
                if (!NonActivatingPreview)
                {
                    tray = new NotifyIcon
                    {
                        Icon = Icon ?? SystemIcons.Application, Text = "皮肤工作室 · 编辑器运行中",
                        ContextMenuStrip = PetHost.CreateTrayMenu(ShowEditor, CloseEditor, DisablePet), Visible = true
                    };
                    tray.DoubleClick += delegate { ShowEditor(); };
                    tray.ContextMenuStrip.Opening += delegate
                    {
                        tray.ContextMenuStrip.Items[3].Enabled = !busy;
                        tray.ContextMenuStrip.Items[5].Enabled = !busy && ready && current.PetEnabled;
                    };
                    try
                    {
                        pet = new PetWorker(DebugPort > 0, PetDiagnosticsPath);
                        pet.Changed += delegate(PetRuntime runtime)
                        {
                            if (IsDisposed || closing) return;
                            try { BeginInvoke((Action)delegate { Reply(new FrontendResponse { Id = "pet-event", Ok = true, Pet = runtime }); }); }
                            catch (InvalidOperationException) { }
                        };
                        pet.Configure(current);
                    }
                    catch (Exception ex) { petError = "桌宠无法启动：" + ex.Message; if (pet != null) { pet.Dispose(); pet = null; } }
                }
                string lastError = Path.Combine(ThemeStore.DirectoryPath, "last-operation-error.txt");
                if (File.Exists(lastError)) operationError = "上次操作未完成，请重新应用或还原；详情见本地错误日志。";
                string assets = FrontendAssets.Extract();
                CoreWebView2Environment.SetLoaderDllFolderPath(assets);
                var options = new CoreWebView2EnvironmentOptions();
                if (DebugPort > 0) options.AdditionalBrowserArguments = "--remote-debugging-port=" + DebugPort;
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ThemeStore.DirectoryPath, "webview2"), options);
                view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
                Controls.Add(view); await view.EnsureCoreWebView2Async(environment);
                view.CoreWebView2.Settings.AreDevToolsEnabled = DebugPort > 0;
                view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                 // Keep the extracted mapping for diagnostics and cached assets.
                 // The editor itself loads the embedded page inline so WebView2
                 // runtime differences in virtual-host permissions cannot blank it.
                 view.CoreWebView2.SetVirtualHostNameToFolderMapping("studio.wetype.local", assets, CoreWebView2HostResourceAccessKind.Allow);
                 view.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs args)
                 {
                     if (inlineNavigation && (args.Uri == "about:blank" || args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))) return;
                     Uri uri;
                     if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out uri) || uri.GetLeftPart(UriPartial.Authority) != Origin) args.Cancel = true;
                 };
                 view.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args) { args.Handled = true; };
                 view.CoreWebView2.WebMessageReceived += Receive;
                 inlineNavigation = true;
                 view.CoreWebView2.NavigateToString(FrontendAssets.InlineHtml());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "前端启动失败：" + ex.Message + "\n需要 Microsoft Edge WebView2 Runtime。", "无法启动编辑器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                closing = true; Close();
            }
        }
        internal FrontendState State(bool cancelled)
        {
            return new FrontendState { Current = current, Presets = SkinTheme.Presets(), Applied = applied, Compatible = compatible, RecoveryRequired = recoveryRequired, Message = message, Cancelled = cancelled, OperationError = operationError, InspectionError = inspectionError,
                Pet = pet != null ? pet.Snapshot() : new PetRuntime { State = "idle", Error = petError } };
        }

        internal void UpdateNativeInspection()
        {
            ApplyNativeInspection(inspectNative());
        }
        private void ApplyNativeInspection(NativeSkinInspection inspection)
        {
            applied = inspection.Theme;
            recoveryRequired = inspection.HasChanges && !inspection.IsConsistent;
            compatible = inspection.IsConsistent;
            inspectionError = null;
            message = applied != null ? "检测到已应用皮肤：" + applied.Name : "当前为官方皮肤。";
            if (recoveryRequired)
                message = "候选框和工具条当前不是同一套皮肤，请先还原官方文件。";
        }
        internal void PrepareBootstrap()
        {
            // Startup can overlap a pending file replacement. The page's
            // initialization handshake must inspect again, not reuse defaults
            // left by a failed OnShown inspection.
            RefreshNativeInspection();
            ready = true;
        }

        private void RefreshNativeInspection()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { UpdateNativeInspection(); return; }
                catch (IOException error)
                {
                    if (attempt < 2) { System.Threading.Thread.Sleep(80); continue; }
                    MarkInspectionFailure(error);
                }
                catch (Exception error) { MarkInspectionFailure(error); return; }
            }
        }

        private void MarkInspectionFailure(Exception error)
        {
            compatible = false;
            applied = null;
            recoveryRequired = false;
            inspectionError = error.Message;
            message = error.Message;
            NativeSkinService.RecordInspectionFailure(error);
        }
        protected override async void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!ready || busy || closing || refreshingInspection) return;
            refreshingInspection = true;
            try
            {
                NativeSkinInspection inspection = await Task.Run(inspectNative);
                if (IsDisposed || closing || busy) return;
                ApplyNativeInspection(inspection);
                Reply(new FrontendResponse { Id = "installation-event", Ok = true, Result = State(false) });
            }
            catch (Exception error)
            {
                if (!IsDisposed && !closing && !busy)
                {
                    MarkInspectionFailure(error);
                    Reply(new FrontendResponse { Id = "installation-event", Ok = false, Result = State(false) });
                }
            }
            finally { refreshingInspection = false; }
        }
        private void ShowEditor()
        {
            if (IsDisposed) return;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate();
        }
        private void CloseEditor()
        {
            if (busy || closing) return;
            Close();
        }
        private async void DisablePet()
        {
            if (busy || closing || !ready) return;
            busy = true;
            try
            {
                string json = await view.CoreWebView2.ExecuteScriptAsync("window.studioDisablePet()");
                if (json == "null") throw new InvalidDataException("请先填写有效的皮肤名称和颜色。");
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    current = (SkinTheme)new DataContractJsonSerializer(typeof(SkinTheme)).ReadObject(stream);
                ThemeStore.Save(current, ThemeStore.CurrentPath);
                if (pet != null) pet.Configure(current);
                PetHost.ConfigureStartup(current);
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "无法关闭桌宠", ex.Message, ToolTipIcon.Error);
            }
            finally { busy = false; }
        }
        internal static FrontendRequest ParseRequest(string json)
        {
            if (json == null || json.Length > 13 * 1024 * 1024) throw new InvalidDataException("界面请求过大。");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var request = (FrontendRequest)new DataContractJsonSerializer(typeof(FrontendRequest)).ReadObject(stream);
                if (request == null || string.IsNullOrEmpty(request.Id) || request.Id.Length > 80 || string.IsNullOrEmpty(request.Action)) throw new InvalidDataException("界面请求无效。");
                return request;
            }
        }
        private void Reply(FrontendResponse response)
        {
            if (IsDisposed || view == null || view.CoreWebView2 == null) return;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(FrontendResponse)).WriteObject(stream, response);
                view.CoreWebView2.PostWebMessageAsJson(Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
        private async void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != Origin + "/index.html" && !(inlineNavigation && (e.Source == "about:blank" || e.Source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)))) return;
            FrontendRequest request = null;
            try
            {
                request = ParseRequest(e.WebMessageAsJson); bool cancelled = false;
                if (busy && request.Action != "bootstrap") throw new InvalidOperationException("应用或还原正在进行，请等待完成。");
                switch (request.Action)
                {
                    case "open-github":
                    case "open-x":
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                            FileName = request.Action == "open-github" ? "https://github.com/acnlie/wetype-skin" : "https://x.com/Tokupup",
                            UseShellExecute = true
                        });
                        break;
                    case "bootstrap": PrepareBootstrap(); break;
                    case "save":
                        if (request.Theme == null) throw new InvalidDataException("皮肤内容为空。");
                        request.Theme.Validate();
                        if (!NonActivatingPreview) ThemeStore.Save(request.Theme, ThemeStore.CurrentPath);
                        current = request.Theme; if (pet != null) pet.Configure(current); break;
                    case "pet-state": break;
                    case "apply":
                        if (NonActivatingPreview) throw new InvalidOperationException("截图模式不能应用补丁。");
                        if (request.Theme == null) throw new InvalidDataException("皮肤内容为空。");
                        request.Theme.Validate(); ThemeStore.Save(request.Theme, ThemeStore.CurrentPath); current = request.Theme;
                        await Change(current.Clone(), request.Id, "apply"); break;
                    case "restore":
                        if (NonActivatingPreview) throw new InvalidOperationException("截图模式不能修改安装。");
                        await Change(null, request.Id, "restore"); break;
                    case "import":
                        using (var dialog = new OpenFileDialog { Title = "导入皮肤", Filter = "皮肤文件|*.wtskin.json;*.json" })
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) { cancelled = true; break; }
                            current = ThemeStore.Load(dialog.FileName);
                            if (!NonActivatingPreview) ThemeStore.Save(current, ThemeStore.CurrentPath);
                            if (pet != null) pet.Configure(current);
                        }
                        break;
                    case "image":
                        using (var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" })
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) { cancelled = true; break; }
                            var theme = request.Theme ?? current.Clone(); theme.SetImage(dialog.FileName); theme.Validate(); current = theme;
                            if (!NonActivatingPreview) ThemeStore.Save(current, ThemeStore.CurrentPath);
                        }
                        break;
                    case "export":
                        if (request.Theme == null) throw new InvalidDataException("皮肤内容为空。");
                        request.Theme.Validate();
                        string name = request.Theme.Name;
                        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                        using (var dialog = new SaveFileDialog { Title = "导出皮肤", Filter = "皮肤文件|*.wtskin.json", FileName = name + ".wtskin.json", AddExtension = true })
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) { cancelled = true; break; }
                            ThemeStore.Save(request.Theme, dialog.FileName);
                        }
                        break;
                    default: throw new InvalidDataException("未知界面操作。");
                }
                if (request.Action == "bootstrap" && EnableOnStart && compatible)
                {
                    EnableOnStart = false; await Change(current.Clone(), request.Id, "apply");
                }
                Reply(new FrontendResponse { Id = request.Id, Ok = true, Result = State(cancelled) });
                if (request.Action == "bootstrap")
                {
                    if (!string.IsNullOrEmpty(ScreenshotPath))
                    {
                        await Task.Delay(600); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ScreenshotPath)));
                        using (var file = File.Create(ScreenshotPath)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                        closing = true; Close();
                    }
                }
            }
            catch (Exception ex) { Reply(new FrontendResponse { Id = request == null ? null : request.Id, Ok = false, Error = ex.Message, Result = State(false) }); }
        }
        private void ReportProgress(string requestId, string action, int step, int total, string message)
        {
            if (string.IsNullOrEmpty(requestId) || IsDisposed || view == null) return;
            Action send = delegate
            {
                if (IsDisposed || view == null || view.CoreWebView2 == null) return;
                Reply(new FrontendResponse { Id = requestId, Progress = true, Action = action, Step = step, Total = total, ProgressMessage = message });
            };
            try
            {
                if (InvokeRequired) BeginInvoke(send);
                else send();
            }
            catch (InvalidOperationException) { }
        }
        private async Task Change(SkinTheme theme, string requestId = null, string action = null)
        {
            busy = true;
            try
            {
                if (pet != null) pet.Suspend(true);
                await Task.Run(delegate
                {
                    service.ChangeInstallation(theme, delegate(int step, int total, string progressMessage)
                    {
                        ReportProgress(requestId, action, step, total, progressMessage);
                    });
                });
                operationError = null; message = null;
            }
            catch (Exception ex) { operationError = ex.Message; NativeSkinService.RecordLastOperationError(ex); throw; }
            finally
            {
                try { UpdateNativeInspection(); }
                catch (Exception ex) { MarkInspectionFailure(ex); }
                busy = false;
                if (pet != null) { pet.Configure(current); pet.Suspend(false); }
            }
        }
        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (!closing && !NonActivatingPreview)
            {
                if (busy) { e.Cancel = true; return; }
                e.Cancel = true; busy = true;
                try
                {
                    if (ready)
                    {
                        string json = await view.CoreWebView2.ExecuteScriptAsync("window.studioSnapshot()");
                        if (json != "null")
                        {
                            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                current = (SkinTheme)new DataContractJsonSerializer(typeof(SkinTheme)).ReadObject(stream);
                            current.Validate();
                        }
                    }
                    ThemeStore.Save(current, ThemeStore.CurrentPath);
                    PetHost.Start(current, PetDiagnosticsPath);
                    closing = true; Close();
                }
                catch (Exception ex)
                {
                    if (pet == null && current.PetEnabled)
                    {
                        try { pet = new PetWorker(DebugPort > 0, PetDiagnosticsPath); pet.Configure(current); }
                        catch { }
                    }
                    if (ready) Reply(new FrontendResponse { Id = "host-close", Ok = false, Error = "保存或后台启动失败，编辑器保持打开：" + ex.Message, Result = State(false) });
                    else MessageBox.Show(this, ex.Message, "关闭失败");
                }
                finally { busy = false; }
                return;
            }
            closing = true; base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (tray != null) { tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            if (pet != null) pet.Dispose(); base.OnFormClosed(e);
        }
    }
}
