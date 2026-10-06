using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    [DataContract]
    internal sealed class PetClip
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "file")] public string File { get; set; }
        [DataMember(Name = "frameSize")] public int FrameSize { get; set; }
        [DataMember(Name = "columns")] public int Columns { get; set; }
        [DataMember(Name = "frames")] public int Frames { get; set; }
        [DataMember(Name = "fps")] public int Fps { get; set; }
        private Bitmap sheet;
        internal bool Loaded { get { return sheet != null; } }
        internal Bitmap Sheet
        {
            get
            {
                if (sheet == null)
                {
                    SetSheet(DecodeSheet());
                }
                return sheet;
            }
        }
        internal Bitmap DecodeSheet()
        {
            using (var stream = new MemoryStream(EmbeddedResources.Read("Pet." + File)))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }
        internal void SetSheet(Bitmap bitmap)
        {
            if (Frame(Frames - 1).Bottom > bitmap.Height || Columns * FrameSize > bitmap.Width)
            { bitmap.Dispose(); throw new InvalidDataException("桌宠动画帧图不完整。"); }
            sheet = bitmap;
        }
        private Dictionary<int, Bitmap> cachedFrames;
        internal Rectangle Frame(int index) { return new Rectangle(index % Columns * FrameSize, index / Columns * FrameSize, FrameSize, FrameSize); }
        internal Bitmap ImageAt(int index)
        {
            Bitmap image;
            if (cachedFrames == null) cachedFrames = new Dictionary<int, Bitmap>();
            if (!cachedFrames.TryGetValue(index, out image))
            {
                image = Sheet.Clone(Frame(index), PixelFormat.Format32bppPArgb);
                if (cachedFrames.Count >= 48)
                {
                    int oldest = cachedFrames.Keys.First(); cachedFrames[oldest].Dispose(); cachedFrames.Remove(oldest);
                }
                cachedFrames.Add(index, image);
            }
            return image;
        }
        internal void DisposeImages()
        {
            if (cachedFrames != null) { foreach (Bitmap image in cachedFrames.Values) image.Dispose(); cachedFrames.Clear(); }
            if (sheet != null) { sheet.Dispose(); sheet = null; }
        }
    }
    [DataContract]
    internal sealed class PetClipManifest { [DataMember(Name = "clips")] public PetClip[] Clips { get; set; } }
    internal sealed class PetAssets : IDisposable
    {
        internal readonly Dictionary<string, PetClip> Clips = new Dictionary<string, PetClip>();
        private Task<Bitmap> pending;
        private string pendingName;
        internal PetAssets()
        {
            using (var stream = new MemoryStream(EmbeddedResources.Read("Pet.clips.json")))
            {
                var manifest = (PetClipManifest)new DataContractJsonSerializer(typeof(PetClipManifest)).ReadObject(stream);
                try
                {
                    foreach (PetClip clip in manifest.Clips)
                    {
                        if (clip.FrameSize != 128 || clip.Columns < 1 || clip.Fps < 1 || clip.Frames < 2)
                            throw new InvalidDataException("桌宠动画帧图无效。");
                        Clips.Add(clip.Name, clip);
                    }
                }
                catch { Dispose(); throw; }
            }
        }
        internal PetClip Prepare(string name)
        {
            PetClip clip = Clips[name];
            if (!clip.Loaded && Clips.Values.Count(c => c.Loaded) >= 3)
                foreach (PetClip other in Clips.Values.Where(c => c.Loaded && c != clip && c.Name != "typing").Take(1)) other.DisposeImages();
            clip.ImageAt(0); return clip;
        }
        internal PetClip TryPrepare(string name, PetClip displayed)
        {
            if (pending != null && pending.IsCompleted)
            {
                Bitmap bitmap = pending.GetAwaiter().GetResult();
                PetClip loaded = Clips[pendingName]; pending = null; pendingName = null;
                if (loaded.Loaded) bitmap.Dispose(); else loaded.SetSheet(bitmap);
            }
            PetClip clip = Clips[name];
            while (Clips.Values.Count(c => c.Loaded) > 3)
            {
                PetClip oldest = Clips.Values.FirstOrDefault(c => c.Loaded && c != clip && c != displayed && c.Name != "typing");
                if (oldest == null) break;
                oldest.DisposeImages();
            }
            if (clip.Loaded) return clip;
            if (pending == null) { pendingName = name; pending = Task.Run(delegate { return clip.DecodeSheet(); }); }
            return null;
        }
        internal static string BrowserJson()
        {
            var json = new StringBuilder("{");
            using (var stream = new MemoryStream(EmbeddedResources.Read("Pet.clips.json")))
            {
                var manifest = (PetClipManifest)new DataContractJsonSerializer(typeof(PetClipManifest)).ReadObject(stream);
                foreach (PetClip clip in manifest.Clips)
                {
                    if (json.Length > 1) json.Append(',');
                    json.Append('"').Append(clip.Name).Append("\":{\"frames\":").Append(clip.Frames)
                        .Append(",\"columns\":").Append(clip.Columns).Append(",\"size\":").Append(clip.FrameSize)
                        .Append(",\"fps\":").Append(clip.Fps).Append(",\"url\":\"https://studio.wetype.local/pet/")
                        .Append(clip.File).Append("\"}");
                }
            }
            return json.Append('}').ToString();
        }
        public void Dispose()
        {
            if (pending != null)
                pending.ContinueWith(delegate(Task<Bitmap> task)
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else if (task.IsFaulted) { var observed = task.Exception; }
                });
            foreach (PetClip clip in Clips.Values) clip.DisposeImages(); Clips.Clear();
        }
    }
    internal sealed class PetActivity
    {
        internal string State = "idle";
        internal long Generation;
        internal double LastActivity, StateUntil;
        private string pending;
        private double pendingUntil;
        internal void Input(double now) { LastActivity = now; pending = null; State = "typing"; StateUntil = now + 0.8; }
        internal void Navigate(double now) { LastActivity = now; State = "navigating"; StateUntil = now + 1.2; }
        internal void Mode(double now) { LastActivity = now; pending = null; State = "mode"; StateUntil = now + 4; Generation++; }
        internal void ExpectEnd(string state, double now) { pending = state; pendingUntil = now + 0.7; LastActivity = now; }
        internal void Visibility(bool visible, double now)
        {
            if (visible) { Input(now); return; }
            if (pending != null && now <= pendingUntil) { State = pending; StateUntil = now + 2.4; }
            else if (State != "mode") { State = "idle"; StateUntil = 0; }
            pending = null;
        }
        internal void Tick(bool visible, double now)
        {
            if (pending != null && now > pendingUntil) pending = null;
            if (now < StateUntil) return;
            State = visible ? now - LastActivity > 2 ? "waiting" : "typing" : now - LastActivity > 120 ? "sleeping" : "idle";
        }
    }
    internal sealed class PetMotion
    {
        private readonly Random random = new Random();
        private string state;
        private double until;
        private int navigationVisits;
        internal string Clip = "idle";
        internal bool Select(string nextState, string modeClip, double now, bool animate, IDictionary<string, PetClip> clips)
        {
            string next = Clip;
            bool changed = state != nextState;
            if (!changed && (!animate || now < until) && !(nextState == "mode" && modeClip != Clip)) return false;
            if (nextState == "mode") next = modeClip;
            else if (nextState == "committed" || nextState == "cancelled") next = nextState;
            else
            {
                string[] pool = nextState == "typing" ? new[] { "typing", "thinking", "watch" }
                    : nextState == "waiting" ? new[] { "thinking", "watch", "tapping", "cube" }
                    : nextState == "navigating" ? new[] { "navigating", "thinking", "tapping" }
                    : nextState == "sleeping" ? new[] { "sleeping", "sleepy", "yawn" }
                    : new[] { "idle", "rice", "tea", "stretch", "humming", "bubbles", "cube", "yawn" };
                string[] choices = pool.Where(c => c != Clip && clips.ContainsKey(c)).ToArray();
                next = changed && nextState == "typing" ? "typing" : changed && nextState == "navigating" ? pool[navigationVisits++ % pool.Length]
                    : choices.Length > 0 ? choices[random.Next(choices.Length)] : pool[0];
            }
            if (!clips.ContainsKey(next)) next = "mode";
            state = nextState; until = now + clips[next].Frames / (double)clips[next].Fps;
            bool result = Clip != next || changed; Clip = next; return result;
        }
    }
    internal sealed class PetMouseState
    {
        internal bool Down;
        internal bool Change(bool down) { if (down == Down) return false; Down = down; return true; }
    }
    internal sealed class PetToolbarGesture
    {
        private Rectangle anchor;
        private Point press;
        private bool pressed;
        private uint dpi;
        internal string Region;
        private string RegionAt(Rectangle bounds, Point location)
        {
            if (!bounds.Contains(location)) return null;
            double x = (location.X - bounds.Left) * 96.0 / dpi;
            return x >= 30 && x < 58 ? "language" : x >= 58 && x < 84 ? "punctuation" : x >= 84 && x < 112 ? "angle" : null;
        }
        private bool Hit(Rectangle bounds, Point location)
        {
            // In the supported native v2 layout the three mode controls occupy
            // 30..112 DIP. Extra voice/AI controls change width, not these offsets.
            int left = bounds.Left + (int)Math.Round(30 * dpi / 96.0);
            int right = bounds.Left + (int)Math.Round(112 * dpi / 96.0);
            return !bounds.IsEmpty && bounds.Contains(location)
                && location.X >= left && location.X < right;
        }
        internal bool Begin(Rectangle bounds, Point location, uint windowDpi)
        {
            dpi = windowDpi > 0 ? windowDpi : 96;
            anchor = bounds; press = location; Region = RegionAt(bounds, location); pressed = Hit(bounds, location); return pressed;
        }
        internal bool End(Rectangle bounds, Point location)
        {
            int tolerance = Math.Max(4, anchor.Height / 5);
            bool clicked = pressed && bounds == anchor && Hit(bounds, location) && RegionAt(bounds, location) == Region
                && Math.Abs(location.X - press.X) <= tolerance && Math.Abs(location.Y - press.Y) <= tolerance;
            pressed = false; return clicked;
        }
    }
    internal struct PetInputMode
    {
        internal long Target;
        internal bool Open;
        internal int Conversion;
        internal bool Differs(PetInputMode other) { return Open != other.Open || Conversion != other.Conversion; }
    }
    internal sealed class PetModeState
    {
        internal PetInputMode? Current;
        private double validUntil;
        internal void Expire(double now) { if (now > validUntil) Current = null; }
        internal bool Accept(PetInputMode? value, double now)
        {
            Expire(now);
            if (!value.HasValue) return false;
            bool changed = Current.HasValue && Current.Value.Target == value.Value.Target && Current.Value.Differs(value.Value);
            Current = value; validUntil = now + 0.6;
            return changed;
        }
        internal bool NeedsOperationFeedback(bool toolbarClick) { return toolbarClick || !Current.HasValue; }
        internal static string FeedbackClip(PetInputMode before, PetInputMode current)
        {
            if (before.Open != current.Open) return current.Open ? "mode-chinese" : "mode-english";
            int changed = before.Conversion ^ current.Conversion;
            if ((changed & 0x400) != 0) return (current.Conversion & 0x400) != 0 ? "punctuation-chinese" : "punctuation-english";
            if ((changed & 8) != 0) return (current.Conversion & 8) != 0 ? "mode-full" : "mode-half";
            return "mode";
        }
    }
    internal static class PetModeProbe
    {
        internal static PetInputMode? Read()
        {
            IntPtr foreground = PetNative.GetForegroundWindow(); uint pid;
            uint thread = Native.GetWindowThreadProcessId(foreground, out pid);
            var info = new PetNative.GuiThreadInfo { Size = Marshal.SizeOf(typeof(PetNative.GuiThreadInfo)) };
            if (thread == 0 || !PetNative.GetGUIThreadInfo(thread, ref info)) return null;
            IntPtr target = info.Focus != IntPtr.Zero ? info.Focus : foreground;
            IntPtr ime = PetNative.ImmGetDefaultIMEWnd(target), open, conversion;
            if (ime == IntPtr.Zero || PetNative.SendMessageTimeout(ime, 0x283, new IntPtr(5), IntPtr.Zero, 3, 25, out open) == IntPtr.Zero
                || PetNative.SendMessageTimeout(ime, 0x283, new IntPtr(1), IntPtr.Zero, 3, 25, out conversion) == IntPtr.Zero) return null;
            return new PetInputMode { Target = target.ToInt64(), Open = open != IntPtr.Zero, Conversion = conversion.ToInt32() & 0x409 };
        }
    }
    internal static class PetPlacement
    {
        internal static Rectangle CandidatePanel(Rectangle window, uint dpi)
        {
            if (window.IsEmpty) return window;
            // WeType 2.1.4.6 reserves 28 DIP horizontally and 100 + 12 DIP
            // vertically for its transparent Flutter bubble/clip canvas.
            int horizontal = (int)Math.Round(28 * dpi / 96.0), vertical = (int)Math.Round(112 * dpi / 96.0);
            if (window.Width <= horizontal * 2 || window.Height <= vertical * 2) return window;
            window.Inflate(-horizontal, -vertical); return window;
        }
        internal static Rectangle Place(Rectangle anchor, int size, Rectangle area, Rectangle obstacle)
        {
            var choices = new[] {
                new Rectangle(anchor.Right - size, anchor.Top - size - 3, size, size),
                new Rectangle(anchor.Right + 3, anchor.Top - size + anchor.Height, size, size),
                new Rectangle(anchor.Left - size - 3, anchor.Top - size + anchor.Height, size, size),
                new Rectangle(anchor.Left, anchor.Top - size - 3, size, size),
                new Rectangle(anchor.Right - size, anchor.Bottom + 3, size, size)
            };
            foreach (Rectangle choice in choices)
                if (area.Contains(choice) && !choice.IntersectsWith(anchor) && (obstacle.IsEmpty || !choice.IntersectsWith(obstacle))) return choice;
            // A crowded screen may not have room. Hide instead of covering candidates.
            return Rectangle.Empty;
        }
    }
    internal sealed class PetWindow : Form
    {
        private int lastFrame = -1;
        private string lastClip;
        private Rectangle lastBounds;
        private IntPtr memory, nativeBitmap, oldBitmap, pixels;
        private Size surfaceSize;
        private byte[] previous, transitionFrom;
        private readonly Stopwatch transition = new Stopwatch();
        internal int RenderFailures;
        internal int RenderedFrames;
        internal byte[] Pixels { get { return previous == null ? null : (byte[])previous.Clone(); } }
        internal int WindowStyle { get { return PetNative.GetWindowLong(Handle, -20); } }
        internal bool NativeVisible { get { return IsHandleCreated && !IsDisposed && PetNative.IsWindowVisible(Handle); } }
        internal bool NativeTopMost { get { return IsHandleCreated && !IsDisposed && (WindowStyle & 8) != 0; } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return p; }
        }
        internal PetWindow(string name)
        {
            Text = name; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual; ClientSize = new Size(88, 88);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
            if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x14) { m.Result = new IntPtr(1); return; }
            if (m.Msg == 0x0F) { PetNative.ValidateRect(Handle, IntPtr.Zero); m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }
        private void Surface(Size size)
        {
            if (surfaceSize == size && nativeBitmap != IntPtr.Zero) return;
            ReleaseSurface();
            memory = PetNative.CreateCompatibleDC(IntPtr.Zero);
            var info = new PetNative.BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            nativeBitmap = PetNative.CreateDIBSection(memory, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || nativeBitmap == IntPtr.Zero) { ReleaseSurface(); throw new System.ComponentModel.Win32Exception(); }
            oldBitmap = PetNative.SelectObject(memory, nativeBitmap); surfaceSize = size;
        }
        private void ReleaseSurface()
        {
            if (oldBitmap != IntPtr.Zero) PetNative.SelectObject(memory, oldBitmap);
            if (nativeBitmap != IntPtr.Zero) PetNative.DeleteObject(nativeBitmap);
            if (memory != IntPtr.Zero) PetNative.DeleteDC(memory);
            memory = nativeBitmap = oldBitmap = pixels = IntPtr.Zero;
        }
        protected override void Dispose(bool disposing)
        {
            ReleaseSurface(); previous = transitionFrom = null;
            base.Dispose(disposing);
        }
        internal void Render(PetClip clip, int frame, Rectangle bounds, bool animate = true)
        {
            if (bounds.IsEmpty) { if (Visible) Hide(); return; }
            if (Visible && NativeVisible && NativeTopMost && lastClip == clip.Name && lastFrame == frame && lastBounds == bounds) return;
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    // Scaling a full sprite sheet for each small source rectangle can
                    // make GDI+ resample the entire sheet. Cache only the selected tile.
                    graphics.DrawImage(clip.ImageAt(frame), new Rectangle(Point.Empty, bounds.Size));
                }
                Surface(bounds.Size);
                // Explicit top-down BGRA keeps premultiplied alpha intact. GDI+
                // GetHbitmap can convert it through an opaque black background.
                BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                byte[] output = new byte[bounds.Width * bounds.Height * 4];
                try
                {
                    int rowSize = bounds.Width * 4;
                    for (int y = 0; y < bounds.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), output, y * rowSize, rowSize);
                }
                finally { bitmap.UnlockBits(data); }
                if (animate && Visible && lastClip != clip.Name && previous != null && previous.Length == output.Length)
                { transitionFrom = previous; transition.Restart(); }
                if (animate && transitionFrom != null && transitionFrom.Length == output.Length && transition.ElapsedMilliseconds < 120)
                {
                    int blend = (int)(transition.ElapsedMilliseconds * 255 / 120);
                    for (int i = 0; i < output.Length; i++) output[i] = (byte)((output[i] * blend + transitionFrom[i] * (255 - blend)) / 255);
                }
                else { transitionFrom = null; transition.Stop(); }
                Marshal.Copy(output, 0, pixels, output.Length); previous = output;
                IntPtr dc = PetNative.GetDC(IntPtr.Zero);
                try
                {
                    var destination = new PetNative.Point { X = bounds.X, Y = bounds.Y };
                    var source = new PetNative.Point(); var size = new PetNative.Size { Width = bounds.Width, Height = bounds.Height };
                    var blend = new PetNative.Blend { Alpha = 255, Format = 1 };
                    if (!PetNative.UpdateLayeredWindow(Handle, dc, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                    { RenderFailures++; return; }
                    bool showing = !Visible;
                    if (showing) Show();
                    // Show() applies WinForms' TopMost state and startup visibility.
                    // Reassert native visibility afterwards, including SW_HIDE hosts.
                    if (showing || lastBounds != bounds || !NativeVisible || !NativeTopMost)
                        if (!PetNative.SetWindowPos(Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10 | 0x40))
                        { RenderFailures++; return; }
                    lastClip = clip.Name; lastFrame = frame; lastBounds = bounds;
                    RenderedFrames++;
                }
                finally
                {
                    PetNative.ReleaseDC(IntPtr.Zero, dc);
                }
            }
        }
        internal double? ScreenMatch()
        {
            byte[] expected = previous; Rectangle bounds = lastBounds;
            if (!NativeVisible || expected == null || bounds.IsEmpty || expected.Length != bounds.Width * bounds.Height * 4) return null;
            try
            {
                using (var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(image))
                    {
                        IntPtr screen = PetNative.GetDC(IntPtr.Zero), destination = graphics.GetHdc();
                        try
                        {
                            if (!PetNative.BitBlt(destination, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020)) return null;
                        }
                        finally { graphics.ReleaseHdc(destination); PetNative.ReleaseDC(IntPtr.Zero, screen); }
                    }
                    BitmapData data = image.LockBits(new Rectangle(Point.Empty, bounds.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    byte[] actual = new byte[expected.Length];
                    try
                    {
                        for (int y = 0; y < bounds.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), actual, y * bounds.Width * 4, bounds.Width * 4);
                    }
                    finally { image.UnlockBits(data); }
                    int samples = 0, matched = 0;
                    // Compare only opaque character pixels; no image or text is saved.
                    for (int y = 2; y < bounds.Height; y += 5) for (int x = 2; x < bounds.Width; x += 5)
                    {
                        int i = (y * bounds.Width + x) * 4;
                        if (expected[i + 3] < 250) continue;
                        samples++;
                        if (Math.Abs(actual[i] - expected[i]) <= 24 && Math.Abs(actual[i + 1] - expected[i + 1]) <= 24
                            && Math.Abs(actual[i + 2] - expected[i + 2]) <= 24) matched++;
                    }
                    return samples >= 8 ? (double?)matched / samples : null;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (ExternalException) { return null; }
        }
    }
    [DataContract]
    internal sealed class PetRuntime
    {
        [DataMember(Name = "processId")] public int ProcessId { get; set; }
        [DataMember(Name = "suspended")] public bool Suspended { get; set; }
        [DataMember(Name = "controlRequestId")] public string ControlRequestId { get; set; }
        [DataMember(Name = "state")] public string State;
        [DataMember(Name = "enabled")] public bool Enabled;
        [DataMember(Name = "candidateVisible")] public bool CandidateVisible;
        [DataMember(Name = "toolbarVisible")] public bool ToolbarVisible;
        [DataMember(Name = "candidateBounds")] public int[] CandidateBounds;
        [DataMember(Name = "toolbarBounds")] public int[] ToolbarBounds;
        [DataMember(Name = "frame")] public int Frame;
        [DataMember(Name = "transitions")] public string[] Transitions;
        [DataMember(Name = "error")] public string Error;
        [DataMember(Name = "renderFailures")] public int RenderFailures;
        [DataMember(Name = "candidateAnchor")] public int[] CandidateAnchor;
        [DataMember(Name = "toolbarAnchor")] public int[] ToolbarAnchor;
        [DataMember(Name = "candidateHandle")] public long CandidateHandle;
        [DataMember(Name = "toolbarHandle")] public long ToolbarHandle;
        [DataMember(Name = "windows")] public string[] Windows;
        [DataMember(Name = "elapsedMs")] public long ElapsedMs;
        [DataMember(Name = "tickCount")] public long TickCount;
        [DataMember(Name = "maxTickMs")] public double MaxTickMs;
        [DataMember(Name = "lastTickMs")] public double LastTickMs;
        [DataMember(Name = "inputEvents")] public long InputEvents;
        [DataMember(Name = "candidateFrames")] public int CandidateFrames;
        [DataMember(Name = "toolbarFrames")] public int ToolbarFrames;
        [DataMember(Name = "mouseModeClicks")] public long MouseModeClicks;
        [DataMember(Name = "mouseEvents")] public long MouseEvents;
        [DataMember(Name = "lastMouseRegion")] public string LastMouseRegion;
        [DataMember(Name = "polledMouseEvents")] public long PolledMouseEvents;
        [DataMember(Name = "inputMode")] public string InputMode;
        [DataMember(Name = "modeFeedbacks")] public long ModeFeedbacks;
        [DataMember(Name = "lastModeFeedback")] public string LastModeFeedback;
        [DataMember(Name = "motion")] public string Motion;
        [DataMember(Name = "fullWidth")] public bool? FullWidth;
        [DataMember(Name = "chinesePunctuation")] public bool? ChinesePunctuation;
        [DataMember(Name = "candidateTopMost")] public bool CandidateTopMost;
        [DataMember(Name = "toolbarTopMost")] public bool ToolbarTopMost;
        [DataMember(Name = "candidateScreenMatch")] public double? CandidateScreenMatch;
        [DataMember(Name = "toolbarScreenMatch")] public double? ToolbarScreenMatch;
    }
    internal sealed class WhalePetController : IDisposable
    {
        private readonly PetAssets assets = new PetAssets();
        private readonly PetWindow candidatePet = new PetWindow("DeepSeek Chan · 候选框"), toolbarPet = new PetWindow("DeepSeek Chan · 工具条");
        // Poll above the animation rate so Windows timer quantization does not
        // skip 24 FPS frames. Unchanged frames return without rendering.
        private readonly Timer timer = new Timer { Interval = 15 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PetActivity activity = new PetActivity();
        private readonly PetMotion motion = new PetMotion();
        private readonly PetMouseState mouseState = new PetMouseState();
        private readonly PetToolbarGesture toolbarGesture = new PetToolbarGesture();
        private readonly PetModeState inputMode = new PetModeState();
        private readonly Queue<string> transitions = new Queue<string>();
        private readonly PetNative.WinEventCallback eventCallback;
        private readonly PetNative.HookCallback keyCallback, mouseCallback;
        private readonly int currentSession;
        private readonly int currentProcess;
        private readonly bool diagnostics;
        private readonly string diagnosticPath;
        private IntPtr events, foregroundEvents, keyboard, mouse, candidate, toolbar;
        private SkinTheme settings;
        private Rectangle candidateAnchor, toolbarAnchor;
        private bool wasCandidateVisible, suspended, shiftDown, shiftUsed;
        private bool updateQueued, updating, disposed;
        private bool toolbarFeedbackPending;
        private bool punctuationFeedbackPending;
        private volatile bool diagnosticWriting;
        private volatile bool modeReading;
        private readonly object modeLock = new object();
        private PetInputMode? pendingMode;
        private double nextModeRead;
        private long tickCount, inputEvents, mouseEvents, mouseModeClicks, clipGeneration, polledMouseEvents;
        private string lastMouseRegion;
        private string lastModeFeedback;
        private string modeClip = "mode";
        private bool? toolbarFull;
        private bool? toolbarPunctuation;
        private volatile bool toolbarReading;
        private readonly object toolbarReadLock = new object();
        private PetToolbarModes pendingToolbarModes;
        private double nextAngleRead;
        private double maxTickMs, lastTickMs, nextDiagnostic, nextPublish;
        private double nextDiscover, clipStart, modeProbeAt;
        private int lastFrame;
        private string clipName = "idle", error;
        private PetClip displayedClip;
        private volatile bool screenReading;
        private volatile double[] screenMatches;
        private double nextScreenRead;
        private string[] observedWindows = new string[0];
        internal event Action<PetRuntime> Changed;
        internal WhalePetController(bool enableDiagnostics = false, string diagnosticFile = null)
        {
            diagnostics = enableDiagnostics;
            diagnosticPath = enableDiagnostics ? diagnosticFile : null;
            using (Process process = Process.GetCurrentProcess()) { currentSession = process.SessionId; currentProcess = process.Id; }
            eventCallback = WindowEvent; keyCallback = KeyEvent; mouseCallback = MouseEvent;
            timer.Tick += delegate { Tick(); };
        }
        internal void Configure(SkinTheme theme)
        {
            settings = theme.Clone();
            bool enabled = settings.PetEnabled && (settings.PetCandidate || settings.PetToolbar);
            if (enabled && events == IntPtr.Zero)
            {
                displayedClip = assets.Prepare("typing");
                IntPtr dispatchWindow = candidatePet.Handle;
                events = PetNative.SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, eventCallback, 0, 0, 2);
                foregroundEvents = PetNative.SetWinEventHook(3, 3, IntPtr.Zero, eventCallback, 0, 0, 2);
                keyboard = PetNative.SetWindowsHookEx(13, keyCallback, PetNative.GetModuleHandle(null), 0);
                mouse = PetNative.SetWindowsHookEx(14, mouseCallback, PetNative.GetModuleHandle(null), 0);
                error = events == IntPtr.Zero || foregroundEvents == IntPtr.Zero || keyboard == IntPtr.Zero || mouse == IntPtr.Zero
                    ? "部分输入事件无法订阅，状态联动不可用。" : null;
                Discover(); timer.Start();
            }
            if (!enabled) { timer.Stop(); Unhook(); candidatePet.Hide(); toolbarPet.Hide(); }
            else Tick();
            Publish(); WriteDiagnostic(true);
        }
        internal void Suspend(bool value) { suspended = value; if (value) { candidatePet.Hide(); toolbarPet.Hide(); } else { Discover(); Tick(); } }
        private bool OwnedBy(IntPtr window, string processName)
        {
            uint pid, session; Native.GetWindowThreadProcessId(window, out pid);
            if (pid == 0 || !PetNative.ProcessIdToSessionId(pid, out session) || session != currentSession) return false;
            try
            {
                return string.Equals(Native.ProcessPath((int)pid), Path.Combine(WeTypeInstallation.DirectoryPath, processName + ".exe"), StringComparison.OrdinalIgnoreCase);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try { using (Process process = Process.GetProcessById((int)pid)) return process.ProcessName == processName; }
                catch (ArgumentException) { return false; }
                catch (InvalidOperationException) { return false; }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        private void Discover()
        {
            candidate = Native.FindWindow("wetype.flutter.setting", "wetype_candidate");
            if (!OwnedBy(candidate, "wetype_renderer")) candidate = IntPtr.Zero;
            toolbar = Native.FindWindow("StatusBarWnd", null);
            if (toolbar == IntPtr.Zero) toolbar = Native.FindWindow("wetype.statusbar.window", null);
            if (!OwnedBy(toolbar, "wetype_update")) toolbar = IntPtr.Zero;
            if (toolbar == IntPtr.Zero || !PetNative.IsWindowVisible(toolbar))
            {
                PetNative.EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    var name = new StringBuilder(256); PetNative.GetClassName(window, name, name.Capacity);
                    if ((name.ToString() == "StatusBarWnd" || name.ToString() == "wetype.statusbar.window") && PetNative.IsWindowVisible(window) && OwnedBy(window, "wetype_update")) { toolbar = window; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            nextDiscover = clock.Elapsed.TotalSeconds + 0.7;
            if (!diagnostics) return;
            var windows = new List<string>();
            var processes = new Dictionary<uint, string>();
            processes[(uint)currentProcess] = "WeTypeSkinStudio";
            uint candidatePid, toolbarPid;
            Native.GetWindowThreadProcessId(candidate, out candidatePid); Native.GetWindowThreadProcessId(toolbar, out toolbarPid);
            if (candidatePid != 0) processes[candidatePid] = "wetype_renderer";
            if (toolbarPid != 0) processes[toolbarPid] = "wetype_update";
            PetNative.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint pid; Native.GetWindowThreadProcessId(window, out pid);
                string processName;
                if (processes.TryGetValue(pid, out processName))
                {
                    var name = new StringBuilder(256); PetNative.GetClassName(window, name, name.Capacity);
                    PetNative.Rect rect; PetNative.GetWindowRect(window, out rect);
                    windows.Add(processName + ":" + window.ToInt64() + ":" + name + ":" + PetNative.IsWindowVisible(window) + ":" + rect.Left + "," + rect.Top + "," + (rect.Right - rect.Left) + "," + (rect.Bottom - rect.Top));
                    if (window == candidate)
                    {
                        var region = PetNative.CreateRectRgn(0, 0, 0, 0);
                        try { int result = PetNative.GetWindowRgn(window, region); PetNative.GetRgnBox(region, out rect); windows.Add("region:" + result + ":" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom); }
                        finally { PetNative.DeleteObject(region); }
                        PetNative.GetClientRect(window, out rect); var origin = new PetNative.Point(); PetNative.ClientToScreen(window, ref origin);
                        windows.Add("client:" + origin.X + "," + origin.Y + "," + rect.Right + "," + rect.Bottom);
                    }
                }
                return true;
            }, IntPtr.Zero);
            observedWindows = windows.ToArray();
        }
        private void WindowEvent(IntPtr hook, uint ev, IntPtr window, int objectId, int child, uint thread, uint time)
        {
            if (suspended || settings == null || !settings.PetEnabled) return;
            if (ev != 3 && (objectId != 0 || child != 0 || window != candidate && window != toolbar)) return;
            if (ev != 3 && ev != 0x8000 && ev != 0x8001 && ev != 0x8002 && ev != 0x8003 && ev != 0x800B) return;
            if (updateQueued || updating || disposed) return;
            // Native event callbacks may reenter during window positioning. Coalesce
            // them onto the UI queue; never draw inside a hook callback.
            updateQueued = true;
            try { candidatePet.BeginInvoke((Action)delegate { updateQueued = false; if (!disposed) Tick(); }); }
            catch (InvalidOperationException) { updateQueued = false; }
        }
        private IntPtr KeyEvent(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code >= 0 && !suspended && (candidatePet.Visible || toolbarPet.Visible))
                {
                    inputEvents++;
                    int key = Marshal.ReadInt32(data); long msg = message.ToInt64(); double now = clock.Elapsed.TotalSeconds;
                    bool down = msg == 0x100 || msg == 0x104;
                    if (key == 0xA0 || key == 0xA1 || key == 0x10)
                    {
                        if (down) { shiftDown = true; shiftUsed = false; }
                        else { if (shiftDown && !shiftUsed) modeProbeAt = now + 0.15; shiftDown = false; }
                    }
                    else if (down)
                    {
                        if (shiftDown) shiftUsed = true;
                        bool control = PetNative.GetAsyncKeyState(0x11) < 0, alt = PetNative.GetAsyncKeyState(0x12) < 0;
                        if (control && key == 0xBE) { punctuationFeedbackPending = true; modeProbeAt = now + 0.35; }
                        else if (key == 0x20 && (control || shiftDown)) modeProbeAt = now + 0.15;
                        else if (!control && !alt && wasCandidateVisible)
                        {
                            if (key == 0x1B) activity.ExpectEnd("cancelled", now);
                            else if (key == 0x20 || key == 0x0D || key >= 0x31 && key <= 0x39) activity.ExpectEnd("committed", now);
                            else if (key >= 0x21 && key <= 0x28 || key == 0xBB || key == 0xBD) activity.Navigate(now);
                            else if (key == 8 || key >= 0x41 && key <= 0x5A) activity.Input(now);
                        }
                        else if (!control && !alt && toolbarPet.Visible && key >= 0x41 && key <= 0x5A) activity.Input(now);
                    }
                }
            }
            catch { error = "输入状态订阅异常。"; }
            return PetNative.CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private IntPtr MouseEvent(int code, IntPtr message, IntPtr data)
        {
            try
            {
                long msg = message.ToInt64();
                if (code >= 0 && (msg == 0x201 || msg == 0x202) && !suspended && settings != null && settings.PetEnabled)
                {
                    var point = (PetNative.Point)Marshal.PtrToStructure(data, typeof(PetNative.Point));
                    MouseButton(msg == 0x201, new Point(point.X, point.Y), false);
                }
            }
            catch { error = "鼠标状态订阅异常。"; }
            return PetNative.CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private void MouseButton(bool down, Point location, bool polled)
        {
            if (!mouseState.Change(down)) return;
            mouseEvents++; if (polled) polledMouseEvents++;
            double now = clock.Elapsed.TotalSeconds;
            if (down)
            {
                bool toolbarModePressed = toolbarGesture.Begin(toolbarAnchor, location, Dpi(toolbar));
                lastMouseRegion = toolbarModePressed ? "toolbar-mode" : wasCandidateVisible && candidateAnchor.Contains(location) ? "candidate" : "other";
                if (lastMouseRegion == "candidate")
                {
                    if (location.X > candidateAnchor.Right - 60) activity.Navigate(now);
                    else activity.ExpectEnd("committed", now);
                }
            }
            else if (toolbarGesture.End(toolbarAnchor, location))
            {
                mouseModeClicks++; toolbarFeedbackPending = true;
                punctuationFeedbackPending = toolbarGesture.Region == "punctuation";
                modeProbeAt = now + 0.35; nextAngleRead = 0;
            }
        }
        private void PollMouse()
        {
            bool down = PetNative.GetAsyncKeyState(1) < 0;
            if (down == mouseState.Down) return;
            PetNative.Point point;
            if (PetNative.GetCursorPos(out point)) MouseButton(down, new Point(point.X, point.Y), true);
        }
        private void ReadMode(double now)
        {
            PetInputMode? value;
            lock (modeLock) { value = pendingMode; pendingMode = null; }
            PetInputMode? before = inputMode.Current;
            if (inputMode.Accept(value, now))
            {
                if (before.HasValue && value.HasValue)
                    modeClip = PetModeState.FeedbackClip(before.Value, value.Value);
                modeProbeAt = 0; toolbarFeedbackPending = punctuationFeedbackPending = false; lastModeFeedback = "native-state"; activity.Mode(now);
            }
            if (modeReading || now < nextModeRead || toolbarAnchor.IsEmpty && candidateAnchor.IsEmpty) return;
            nextModeRead = now + 0.1; modeReading = true;
            Task.Run(delegate
            {
                try { PetInputMode? result = PetModeProbe.Read(); lock (modeLock) pendingMode = result; }
                catch (Exception) { lock (modeLock) pendingMode = null; }
                finally { modeReading = false; }
            });
        }
        private static Rectangle BoundsOf(IntPtr window)
        {
            PetNative.Rect rect;
            int cloaked;
            if (window != IntPtr.Zero && PetNative.DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return Rectangle.Empty;
            return window != IntPtr.Zero && PetNative.IsWindowVisible(window) && PetNative.GetWindowRect(window, out rect) ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : Rectangle.Empty;
        }
        private void Tick()
        {
            if (updating || disposed) return;
            updating = true; long start = Stopwatch.GetTimestamp();
            try { Update(); }
            catch (Exception ex)
            {
                timer.Stop(); Unhook(); candidatePet.Hide(); toolbarPet.Hide();
                error = "桌宠已停止：" + ex.Message; Publish();
            }
            finally
            {
                lastTickMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                maxTickMs = Math.Max(maxTickMs, lastTickMs); tickCount++; updating = false;
                WriteDiagnostic();
            }
        }
        private void Update()
        {
            if (suspended || settings == null || !settings.PetEnabled) return;
            double now = clock.Elapsed.TotalSeconds;
            if (now >= nextDiscover) Discover();
            candidateAnchor = PetPlacement.CandidatePanel(BoundsOf(candidate), Dpi(candidate)); toolbarAnchor = BoundsOf(toolbar);
            if (candidateAnchor.IsEmpty) candidatePet.Hide();
            if (toolbarAnchor.IsEmpty) toolbarPet.Hide();
            PollMouse(); ReadMode(now);
            PetToolbarModes modes;
            lock (toolbarReadLock) { modes = pendingToolbarModes; pendingToolbarModes = null; }
            if (modes != null && modes.Target == toolbar && modes.Bounds == toolbarAnchor)
            {
                bool? full = modes.FullWidth, punctuation = modes.ChinesePunctuation;
                if (full.HasValue)
                {
                    if (toolbarFull.HasValue && full.Value != toolbarFull.Value)
                    {
                        modeClip = full.Value ? "mode-full" : "mode-half";
                        modeProbeAt = 0; toolbarFeedbackPending = punctuationFeedbackPending = false; lastModeFeedback = "toolbar-icon"; activity.Mode(now);
                    }
                    toolbarFull = full;
                }
                if (punctuation.HasValue)
                {
                    if (toolbarPunctuation.HasValue && punctuation.Value != toolbarPunctuation.Value)
                    {
                        modeClip = punctuation.Value ? "punctuation-chinese" : "punctuation-english";
                        modeProbeAt = 0; toolbarFeedbackPending = punctuationFeedbackPending = false;
                        lastModeFeedback = "toolbar-punctuation"; activity.Mode(now);
                    }
                    toolbarPunctuation = punctuation;
                }
            }
            if (!toolbarReading && now >= nextAngleRead && !toolbarAnchor.IsEmpty)
            {
                nextAngleRead = now + 0.25; toolbarReading = true;
                IntPtr target = toolbar; Rectangle bounds = toolbarAnchor; uint dpi = Dpi(toolbar);
                Task.Run(delegate
                {
                    try
                    {
                        PetToolbarModes result = PetToolbarProbe.ReadModes(target, bounds, dpi);
                        lock (toolbarReadLock) pendingToolbarModes = result;
                    }
                    finally { toolbarReading = false; }
                });
            }
            bool visible = !candidateAnchor.IsEmpty;
            if (visible != wasCandidateVisible) { activity.Visibility(visible, now); wasCandidateVisible = visible; }
            if (modeProbeAt > 0 && now >= modeProbeAt)
            {
                modeProbeAt = 0;
                // A completed toolbar click gets operation feedback even when TSF
                // stores the mode internally without changing its IMM conversion.
                if (punctuationFeedbackPending || inputMode.NeedsOperationFeedback(toolbarFeedbackPending))
                {
                    modeClip = punctuationFeedbackPending ? "punctuation" : "mode";
                    lastModeFeedback = punctuationFeedbackPending ? "punctuation-operation" : toolbarFeedbackPending ? "toolbar-click" : "shortcut-operation";
                    activity.Mode(now);
                }
                toolbarFeedbackPending = punctuationFeedbackPending = false;
            }
            activity.Tick(visible, now);
            bool selected = motion.Select(activity.State, modeClip, now, settings.PetAnimate, assets.Clips);
            if (selected || clipGeneration != activity.Generation)
            {
                clipName = activity.State == "mode" ? modeClip : motion.Clip; clipStart = now; clipGeneration = activity.Generation;
                transitions.Enqueue(clipName); while (transitions.Count > 40) transitions.Dequeue(); Publish();
            }
            PetClip prepared = assets.TryPrepare(clipName, displayedClip);
            if (prepared != null && prepared != displayedClip) { displayedClip = prepared; clipStart = now; }
            PetClip clip = displayedClip;
            lastFrame = settings.PetAnimate ? (int)((now - clipStart) * clip.Fps) % clip.Frames : 0;
            Rectangle first = Rectangle.Empty;
            if (settings.PetCandidate && !candidateAnchor.IsEmpty)
            {
                first = Place(candidateAnchor, candidate, toolbarAnchor);
                candidatePet.Render(clip, lastFrame, first, settings.PetAnimate);
            }
            else candidatePet.Hide();
            if (settings.PetToolbar && !toolbarAnchor.IsEmpty)
            {
                Rectangle obstacle = candidateAnchor.IsEmpty ? first : first.IsEmpty ? candidateAnchor : Rectangle.Union(first, candidateAnchor);
                Rectangle second = Place(toolbarAnchor, toolbar, obstacle);
                toolbarPet.Render(clip, lastFrame, second, settings.PetAnimate);
            }
            else toolbarPet.Hide();
            if (diagnostics && !screenReading && now >= nextScreenRead)
            {
                nextScreenRead = now + 1; screenReading = true;
                Task.Run(delegate
                {
                    try { screenMatches = new[] { candidatePet.ScreenMatch() ?? -1, toolbarPet.ScreenMatch() ?? -1 }; }
                    catch (Exception) { screenMatches = null; }
                    finally { screenReading = false; }
                });
            }
            if (now >= nextPublish) { nextPublish = now + 1; Publish(); }
        }
        private void WriteDiagnostic(bool force = false)
        {
            if (string.IsNullOrEmpty(diagnosticPath) || diagnosticWriting || !force && clock.Elapsed.TotalSeconds < nextDiagnostic) return;
            nextDiagnostic = clock.Elapsed.TotalSeconds + 0.25; diagnosticWriting = true;
            PetRuntime snapshot = Snapshot();
            Task.Run(delegate
            {
                try
                {
                    using (var stream = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(PetRuntime)).WriteObject(stream, snapshot);
                        string temporary = diagnosticPath + ".tmp";
                        File.WriteAllBytes(temporary, stream.ToArray());
                        if (File.Exists(diagnosticPath)) File.Replace(temporary, diagnosticPath, null);
                        else File.Move(temporary, diagnosticPath);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { diagnosticWriting = false; }
            });
        }
        private Rectangle Place(Rectangle anchor, IntPtr target, Rectangle obstacle)
        {
            uint dpi = Dpi(target);
            return PetPlacement.Place(anchor, Math.Max(48, (int)Math.Round(settings.PetSize * dpi / 96.0)), Screen.FromRectangle(anchor).WorkingArea, obstacle);
        }
        private static uint Dpi(IntPtr window) { try { uint value = PetNative.GetDpiForWindow(window); return value > 0 ? value : 96; } catch (EntryPointNotFoundException) { return 96; } }
        internal PetRuntime Snapshot()
        {
            double[] matches = screenMatches;
            return new PetRuntime { State = activity.State, Motion = displayedClip == null ? clipName : displayedClip.Name, FullWidth = toolbarFull, ChinesePunctuation = toolbarPunctuation, Enabled = settings != null && settings.PetEnabled, CandidateVisible = candidatePet.Visible && candidatePet.NativeVisible,
                ToolbarVisible = toolbarPet.Visible && toolbarPet.NativeVisible, CandidateBounds = RectArray(candidatePet.Bounds), ToolbarBounds = RectArray(toolbarPet.Bounds),
                CandidateTopMost = candidatePet.NativeTopMost, ToolbarTopMost = toolbarPet.NativeTopMost,
                CandidateScreenMatch = matches != null && matches[0] >= 0 && candidatePet.NativeVisible ? (double?)matches[0] : null,
                ToolbarScreenMatch = matches != null && matches[1] >= 0 && toolbarPet.NativeVisible ? (double?)matches[1] : null,
                Frame = lastFrame, Transitions = transitions.ToArray(), Error = error, RenderFailures = candidatePet.RenderFailures + toolbarPet.RenderFailures,
                CandidateAnchor = RectArray(candidateAnchor), ToolbarAnchor = RectArray(toolbarAnchor), CandidateHandle = candidate.ToInt64(), ToolbarHandle = toolbar.ToInt64(), Windows = observedWindows,
                ElapsedMs = clock.ElapsedMilliseconds, TickCount = tickCount, MaxTickMs = maxTickMs, LastTickMs = lastTickMs, InputEvents = inputEvents,
                CandidateFrames = candidatePet.RenderedFrames, ToolbarFrames = toolbarPet.RenderedFrames, MouseModeClicks = mouseModeClicks,
                MouseEvents = mouseEvents, LastMouseRegion = lastMouseRegion, PolledMouseEvents = polledMouseEvents,
                ModeFeedbacks = activity.Generation, LastModeFeedback = lastModeFeedback,
                InputMode = inputMode.Current.HasValue ? (inputMode.Current.Value.Open ? "open:" : "closed:") + inputMode.Current.Value.Conversion : null };
        }
        private static int[] RectArray(Rectangle rect) { return new[] { rect.X, rect.Y, rect.Width, rect.Height }; }
        private void Publish() { if (Changed != null) Changed(Snapshot()); }
        private void Unhook()
        {
            if (events != IntPtr.Zero) PetNative.UnhookWinEvent(events);
            if (foregroundEvents != IntPtr.Zero) PetNative.UnhookWinEvent(foregroundEvents);
            if (keyboard != IntPtr.Zero) PetNative.UnhookWindowsHookEx(keyboard);
            if (mouse != IntPtr.Zero) PetNative.UnhookWindowsHookEx(mouse);
            events = foregroundEvents = keyboard = mouse = IntPtr.Zero;
        }
        public void Dispose() { disposed = true; timer.Stop(); Unhook(); timer.Dispose(); candidatePet.Dispose(); toolbarPet.Dispose(); assets.Dispose(); }
    }
    internal static class PetNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Size { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, ImageSize, XPixels, YPixels, ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        {
            public int Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Blend { public byte Operation, Flags, Alpha, Format; }
        internal delegate void WinEventCallback(IntPtr hook, uint ev, IntPtr window, int objectId, int child, uint thread, uint time);
        internal delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        internal delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] internal static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int GetRgnBox(IntPtr region, out Rect rect);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll")] internal static extern bool ValidateRect(IntPtr window, IntPtr rect);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, ref Point position, ref Size size, IntPtr source, ref Point sourcePosition, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("kernel32.dll")] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("imm32.dll")] internal static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    }
}
