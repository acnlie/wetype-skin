using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    internal static class SelfTests
    {
        private static List<string> passed = new List<string>();
        private static string output;
        private static void Assert(bool condition, string test)
        {
            if (!condition) throw new Exception("测试失败：" + test);
            passed.Add(test); File.WriteAllLines(Path.Combine(output, "test-results.txt"), passed.ToArray());
        }
        private static bool Near(Color a, Color b) { return Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2; }
        private static Bitmap Flat(Size size, Color color) { var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb); using (var g = Graphics.FromImage(b)) g.Clear(color); return b; }
        private static Bitmap Source(bool dark)
        {
            var bitmap = Flat(new Size(320, 100), dark ? Color.FromArgb(24, 24, 24) : Color.White);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.FillRectangle(dark ? Brushes.White : Brushes.Black, 70, 38, 35, 20);
                using (var brush = new SolidBrush(Color.FromArgb(18, 157, 91))) g.FillRectangle(brush, 170, 38, 35, 20);
            }
            return bitmap;
        }
        public static void Run(string directory)
        {
            passed.Clear(); output = Path.GetFullPath(directory); Directory.CreateDirectory(output);
            using (var renderer = new SkinRenderer())
            using (var source = Source(false))
            using (var dark = Source(true))
            using (var behind = Flat(source.Size, Color.FromArgb(10, 50, 90)))
            {
                var theme = new SkinTheme { Opacity = 1 };
                using (var image = renderer.Render(source, behind, theme))
                {
                    Assert(Near(image.GetPixel(30, 25), SkinTheme.ParseColor(theme.Background)), "背景颜色准确");
                    Assert(Near(image.GetPixel(80, 45), SkinTheme.ParseColor(theme.Foreground)), "示例文字映射到自定义文字颜色");
                    Assert(Near(image.GetPixel(180, 45), SkinTheme.ParseColor(theme.Accent)), "示例绿色文字映射到自定义强调色");
                    Assert(image.GetPixel(0, 0).A == 0, "圆角外侧完全透明");
                    Assert(image.GetPixel(160, 0).A == 255, "直边无意外透明");
                }
                using (var image = renderer.Render(dark, behind, theme)) Assert(Near(image.GetPixel(80, 45), SkinTheme.ParseColor(theme.Foreground)), "兼容原候选框深色模式");
                theme.Opacity = 0.5;
                Color bg = SkinTheme.ParseColor(theme.Background);
                using (var image = renderer.Render(source, behind, theme))
                    Assert(Near(image.GetPixel(30, 25), Color.FromArgb((bg.R + 10) / 2, (bg.G + 50) / 2, (bg.B + 90) / 2)), "透明背景正确混合示例画面");
                string picturePath = Path.Combine(output, "sample-background.png");
                using (var picture = Flat(new Size(64, 64), Color.FromArgb(130, 82, 185))) picture.Save(picturePath, ImageFormat.Png);
                theme.SetImage(picturePath); theme.Opacity = 1; theme.ImageTint = 0;
                using (var image = renderer.Render(source, behind, theme)) Assert(Near(image.GetPixel(30, 25), Color.FromArgb(130, 82, 185)), "背景图片正确填充");
                theme.ImageTint = 1;
                using (var image = renderer.Render(source, behind, theme)) Assert(Near(image.GetPixel(30, 25), bg), "图片蒙层可完全覆盖图片");
                string themePath = Path.Combine(output, "roundtrip.wtskin.json"); ThemeStore.Save(theme, themePath); ThemeStore.Save(theme, themePath);
                var read = ThemeStore.Load(themePath); Assert(read.BackgroundImageData == theme.BackgroundImageData && read.Opacity == theme.Opacity && read.Name == theme.Name, "皮肤文件与内嵌图片导出导入无损，覆盖保存成功");
                var legacy = theme.Clone(); legacy.PetBackground = false; legacy.Validate();
                Assert(!legacy.PetBackground, "皮肤校验不再静默改写旧兼容字段");
                string legacyPath = Path.Combine(output, "legacy.json"); ThemeStore.Save(legacy, legacyPath);
                Assert(ThemeStore.Load(legacyPath).PetBackground, "读取旧皮肤时集中迁移兼容字段");
                var invalid = theme.Clone(); invalid.Opacity = double.NaN; bool rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "拒绝非数值透明度");
                invalid = theme.Clone(); invalid.Background = "invalid"; rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; } Assert(rejected, "拒绝无效颜色");
                string incomplete = Path.Combine(output, "invalid.json"); File.WriteAllText(incomplete, "{\"name\":\"test\"}"); rejected = false;
                try { ThemeStore.Load(incomplete); } catch (InvalidDataException) { rejected = true; } Assert(rejected, "拒绝不完整皮肤文件");
                foreach (SkinTheme preset in SkinTheme.Presets())
                {
                    preset.Validate(); ThemeStore.Save(preset, Path.Combine(output, preset.Name + ".wtskin.json"));
                    using (var sample = SkinRenderer.MakeSample(new Size(720, 112)))
                    using (var background = SkinRenderer.MakeBackdrop(sample.Size))
                    using (var image = renderer.Render(sample, background, preset)) image.Save(Path.Combine(output, preset.Name + ".png"), ImageFormat.Png);
                }
                Assert(true, "6 款预设生成和预览渲染成功");
            }
            PatchTests(); ToolbarTests(); TransactionTests(); EditorTests(); PetTests();
            string failure = Path.Combine(output, "test-failure.txt"); if (File.Exists(failure)) File.Delete(failure);
            File.WriteAllText(Path.Combine(output, "summary.txt"), "PASS: " + passed.Count + " checks\r\n" + DateTime.UtcNow.ToString("o") + "\r\nOffline patch / editor tests only; real WeType typing evidence is recorded separately.\r\n");
        }
        private static void PetTests()
        {
            var mouse = new PetMouseState();
            Assert(mouse.Change(true) && !mouse.Change(true) && mouse.Change(false) && !mouse.Change(false),
                "钩子与只读按键状态合并后不会重复触发鼠标点击");
            var gesture = new PetToolbarGesture();
            var toolbarBounds = new Rectangle(100, 200, 275, 50);
            Assert(gesture.Begin(toolbarBounds, new Point(215, 225), 120) && gesture.End(toolbarBounds, new Point(215, 225)),
                "原生半全角按钮完成点击后才反馈");
            Assert(!gesture.End(toolbarBounds, new Point(215, 225)), "同一按钮松开不重复反馈");
            gesture.Begin(toolbarBounds, new Point(150, 225), 120);
            Assert(!gesture.End(toolbarBounds, new Point(210, 225)), "从按钮拖到另一个按钮不冒充切换");
            gesture.Begin(toolbarBounds, new Point(150, 225), 120);
            Assert(!gesture.End(new Rectangle(120, 200, 275, 50), new Point(170, 225)), "拖动工具条只跟随，不误播模式切换");
            gesture.Begin(toolbarBounds, new Point(150, 225), 120);
            Assert(!gesture.End(toolbarBounds, new Point(150, 180)), "移出模式区域后松开不反馈");
            Assert(!gesture.Begin(toolbarBounds, new Point(120, 225), 120) && !gesture.End(toolbarBounds, new Point(120, 225)),
                "工具条标志和拖动区域不触发模式反馈");
            Assert(!gesture.Begin(toolbarBounds, new Point(249, 225), 120) && !gesture.End(toolbarBounds, new Point(249, 225)),
                "工具条麦克风按钮不冒充模式切换");
            var extendedToolbar = new Rectangle(100, 200, 350, 50);
            Assert(!gesture.Begin(extendedToolbar, new Point(280, 225), 120)
                && gesture.Begin(extendedToolbar, new Point(215, 225), 120) && gesture.End(extendedToolbar, new Point(215, 225)),
                "工具条追加AI按钮后模式区域保持正确");
            var inputMode = new PetInputMode { Target = 1, Open = true, Conversion = 1 };
            Assert(!inputMode.Differs(inputMode) && inputMode.Differs(new PetInputMode { Target = 1, Open = true, Conversion = 9 }),
                "真实半全角模式变化可独立识别，未变化不判切换");
            var modes = new PetModeState();
            Assert(!modes.Accept(inputMode, 1), "首次检测只建立基线，不冒充模式切换");
            Assert(!modes.Accept(inputMode, 1.1), "模式不变时不重复播放切换反馈");
            Assert(modes.NeedsOperationFeedback(true) && !modes.NeedsOperationFeedback(false),
                "TSF内部按钮状态不依赖IMM镜像；未改变模式的快捷键仍不误播");
            Assert(modes.Accept(new PetInputMode { Target = 1, Open = true, Conversion = 9 }, 1.2), "同一输入窗口半全角变化触发反馈");
            modes.Accept(null, 1.3);
            Assert(modes.Current.HasValue, "短暂探测失败保留模式基线");
            modes.Accept(null, 1.9);
            Assert(!modes.Current.HasValue, "探测持续失败后旧模式过期，不永久阻止反馈");
            Assert(!modes.Accept(inputMode, 2), "探测恢复不把过期状态当成一次新切换");
            Assert(!modes.Accept(new PetInputMode { Target = 2, Open = false, Conversion = 1 }, 2.1), "更换输入窗口不误判中英切换");
            Assert(modes.Accept(new PetInputMode { Target = 2, Open = true, Conversion = 1 }, 2.2), "当前输入窗口真实中英变化触发反馈");
            var activity = new PetActivity();
            activity.Input(1); activity.ExpectEnd("committed", 1.1); activity.Tick(true, 1.2);
            Assert(activity.State == "typing", "上屏请求必须等待候选框收起，不能只按空格就判成功");
            activity.Visibility(false, 1.3); Assert(activity.State == "committed", "候选框确认收起后播放上屏动画");
            activity.Tick(false, 2); Assert(activity.State == "committed", "上屏动画保持足够时间");
            activity.Tick(false, 4); Assert(activity.State == "idle", "一次性反馈结束后回到待机");
            activity.Visibility(true, 5); activity.ExpectEnd("cancelled", 5.1); activity.Visibility(false, 5.2);
            Assert(activity.State == "cancelled", "真实取消收起播放取消动画");
            activity.Visibility(true, 6); activity.Visibility(false, 6.1);
            Assert(activity.State == "idle", "失去焦点收起不冒充上屏");
            activity.Visibility(true, 7); activity.ExpectEnd("committed", 7.1); activity.Tick(true, 8); activity.Visibility(false, 8.1);
            Assert(activity.State == "idle", "过期选择请求不能污染之后的窗口收起");
            activity.Mode(9); activity.Tick(false, 10); Assert(activity.State == "mode", "模式反馈独立于候选框可见性");
            long generation = activity.Generation; activity.Mode(10);
            Assert(activity.Generation > generation, "连续模式切换重新开始反馈，不能被已有动画吞掉");
            activity.Tick(false, 141); Assert(activity.State == "sleeping", "持续空闲进入休息状态");
            var area = new Rectangle(0, 0, 1920, 1080); var anchor = new Rectangle(500, 800, 520, 42);
            var nativePanel = PetPlacement.CandidatePanel(new Rectangle(840, 793, 665, 332), 120);
            Assert(nativePanel == new Rectangle(875, 933, 595, 52), "候选窗口的原生透明边距从角色定位中排除");
            var placement = PetPlacement.Place(anchor, 88, area, Rectangle.Empty);
            Assert(placement.Bottom < anchor.Top && !placement.IntersectsWith(anchor), "桌宠默认在候选框右上方且不覆盖文字");
            var top = new Rectangle(1800, 5, 110, 42); placement = PetPlacement.Place(top, 88, area, Rectangle.Empty);
            Assert(area.Contains(placement) && !placement.IntersectsWith(top), "屏幕顶部和右边缘回退位置可见且不覆盖候选框");
            var first = PetPlacement.Place(anchor, 88, area, Rectangle.Empty);
            var second = PetPlacement.Place(anchor, 88, area, first);
            Assert(!first.IntersectsWith(second), "两个角色相邻时避免互相重叠");
            var candidateAnchor = new Rectangle(244, 667, 596, 43); var toolbarAnchor = new Rectangle(512, 767, 268, 42);
            var candidatePet = PetPlacement.Place(candidateAnchor, 88, area, toolbarAnchor);
            var toolbarPet = PetPlacement.Place(toolbarAnchor, 88, area, Rectangle.Union(candidateAnchor, candidatePet));
            Assert(!candidatePet.IsEmpty && !toolbarPet.IsEmpty && !toolbarPet.IntersectsWith(candidateAnchor),
                "用户截图中的上下相邻位置能同时显示两个角色，工具条角色避开候选文字");
            Assert(PetPlacement.Place(new Rectangle(0, 0, 100, 100), 88, new Rectangle(0, 0, 100, 100), Rectangle.Empty).IsEmpty,
                "没有足够空间时隐藏角色，不覆盖输入界面");
            var theme = SkinTheme.Presets()[3]; theme.PetCandidate = false; theme.PetAnimate = false; theme.PetSize = 137;
            string path = Path.Combine(output, "pet-roundtrip.json"); ThemeStore.Save(theme, path); var loaded = ThemeStore.Load(path);
            Assert(!loaded.PetCandidate && !loaded.PetAnimate && loaded.PetToolbar && loaded.PetSize == 137, "桌宠位置开关、动画与大小导入导出无损");
            theme.PetSize = 161; bool rejected = false; try { theme.Validate(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "拒绝桌宠尺寸越界");
            using (var assets = new PetAssets())
            {
                Assert(assets.Clips.Count >= 26, "扩充透明动画与状态举牌已内嵌");
                foreach (PetClip clip in assets.Clips.Values)
                {
                    Bitmap cached = clip.ImageAt(0);
                    Assert(cached.Size == new Size(128, 128) && object.ReferenceEquals(cached, clip.ImageAt(0)),
                        clip.Name + " 原生动画使用可复用小帧缓存");
                    bool allVisible = true, hasTransparency = false;
                    for (int frame = 0; frame < clip.Frames; frame++)
                    {
                        Rectangle bounds = clip.Frame(frame); bool visible = false;
                        for (int x = 0; x < bounds.Width; x += 8)
                            for (int y = 0; y < bounds.Height; y += 8)
                            {
                                int alpha = clip.Sheet.GetPixel(bounds.X + x, bounds.Y + y).A;
                                visible |= alpha > 128; hasTransparency |= alpha == 0;
                            }
                        allVisible &= visible;
                    }
                    Assert(allVisible && hasTransparency, clip.Name + " 每一动画帧都有可见角色和透明背景，无空白帧");
                }
                using (var window = new PetWindow("DeepSeek Chan · 离线渲染自测"))
                {
                    IntPtr foreground = PetNative.GetForegroundWindow();
                    Rectangle bounds = PetPlacement.Place(new Rectangle(300, 500, 400, 45), 88, Screen.PrimaryScreen.WorkingArea, Rectangle.Empty);
                    window.Render(assets.Clips["typing"], 0, bounds);
                    window.Render(assets.Clips["typing"], 1, bounds);
                    Assert(window.Visible && window.Bounds == bounds && window.RenderFailures == 0 && window.RenderedFrames == 2, "原生透明窗口真实绘制两帧成功且尺寸定位准确");
                    Assert((window.WindowStyle & 0x080800A0) == 0x080800A0, "原生角色窗口启用分层、点击穿透、工具窗口和禁止激活样式");
                    Assert(PetNative.GetForegroundWindow() == foreground, "角色出现与动画更新不抢前台焦点");
                    window.Render(assets.Clips["typing"], 1, bounds);
                    Assert(window.RenderedFrames == 2, "暂停或帧未变时不重复分配和绘制");
                    window.Render(assets.Clips["typing"], 1, Rectangle.Empty); Assert(!window.Visible, "没有可用定位区域时隐藏角色");
                }
            }
        }
        private static long ReadSigned(byte[] bytes, ref int position)
        {
            long value = 0; int shift = 0;
            while (shift < 63)
            {
                byte b = bytes[position++];
                if (b >= 128) return value + ((long)(b - 192) << shift);
                value += (long)b << shift; shift += 7;
            }
            throw new InvalidDataException("颜色编码无效。");
        }
        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        private static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
            catch (NotSupportedException) { return true; }
        }
        private static void PatchTests()
        {
            byte[] original = File.ReadAllBytes(NativeSkinPatch.TargetPath);
            if (NativeSkinPatch.Hash(original) != NativeSkinPatch.OriginalHash) original = File.ReadAllBytes(NativePatchStore.Installed().BackupPath);
            NativeSkinPatch.RequireOriginal(original, NativeSkinPatch.Version);
            byte[] originalAsset = File.ReadAllBytes(NativeSkinPatch.AssetPath);
            if (NativeSkinPatch.Hash(originalAsset) != NativeSkinPatch.OriginalAssetHash)
                originalAsset = File.ReadAllBytes(NativePatchStore.Installed().AssetBackupPath);
            NativeSkinPatch.RequireOriginalAsset(originalAsset);
            Assert(Rejected(delegate { NativeSkinPatch.Create(original, new SkinTheme(), "9.9.9.9"); }), "拒绝未支持版本");
            byte[] foreign = (byte[])original.Clone(); foreign[foreign.Length - 1] ^= 1;
            Assert(Rejected(delegate { NativeSkinPatch.Create(foreign, new SkinTheme(), NativeSkinPatch.Version); }), "拒绝不匹配的官方哈希");
            var badPlan = new PatchPlan { Bytes = (byte[])original.Clone() };
            Assert(Rejected(delegate { NativeSkinPatch.Edit(badPlan, 0x8484CC, new byte[7], new byte[7], "bad"); }), "逐补丁校验原始指令字节");
            string[] colorCases = { "#FFFFFF", "#000000", "#000001", "#EFFAF3", "#777777", "#FF00FF" };
            foreach (string color in colorCases)
            {
                byte[] encoded = NativeSkinPatch.EncodeColor(color); int position = 0;
                uint low = unchecked((uint)ReadSigned(encoded, ref position)); long high = ReadSigned(encoded, ref position);
                if (encoded.Length != 5 || position != 5 || high != 0 || low != unchecked((uint)SkinTheme.ParseColor(color).ToArgb())) throw new Exception("颜色编码往返失败。");
            }
            Assert(true, "包含黑白极值的颜色保持 5 字节 Dart 编码并解码一致");
            string nativeCompositionPath = Path.Combine(output, "native-composition.png");
            using (var composition = new Bitmap(200, 100))
            using (var graphics = Graphics.FromImage(composition))
            {
                graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 100, 0, 100, 100);
                composition.Save(nativeCompositionPath, ImageFormat.Png);
            }
            string nativeCompositionData = Convert.ToBase64String(File.ReadAllBytes(nativeCompositionPath));
            foreach (SkinTheme theme in SkinTheme.Presets())
            {
                PatchPlan plan = NativeSkinPatch.Create(original, theme, NativeSkinPatch.Version);
                Assert(plan.Bytes.Length == original.Length && plan.Edits.Count >= 19, theme.Name + "补丁保持文件布局且所有原生修改均有记录");
                var offsets = new HashSet<int>();
                foreach (PatchEdit edit in plan.Edits)
                {
                    int count = edit.NewBytes.Split(' ').Length;
                    for (int i = 0; i < count; i++) offsets.Add(edit.Offset + i);
                }
                for (int i = 0; i < original.Length; i++)
                    if (original[i] != plan.Bytes[i] && !offsets.Contains(i)) throw new Exception("存在未记录的补丁差异。");
                Assert(plan.Bytes[0x124F17] == 0xBF && plan.Bytes[0x124F18] == 0xC0, theme.Name + "共享白色对象保持完整");
                int p = 0x1251A3;
                Assert(unchecked((uint)ReadSigned(plan.Bytes, ref p)) == unchecked((uint)SkinTheme.ParseColor(theme.Background).ToArgb()), theme.Name + "背景对象正确解码");
                Assert(plan.Bytes[0x8484CF] == 0x67 && plan.Bytes[0x8484D0] == 0x9A && plan.Bytes[0xA9C717] == 7 && plan.Bytes[0xA9C718] == 0x9A,
                    theme.Name + "背景及候选文字对象池指针正确");
                Assert(BitConverter.ToInt32(plan.Bytes, 0xA98802) == theme.CornerRadius && plan.Bytes[0xA9880E] == original[0xA9880E],
                    theme.Name + "原生半径立即数正确且后续指令边界保持完整");
                var withImage = theme.Clone(); withImage.BackgroundImageData = nativeCompositionData;
                withImage.ImageZoom = 2; withImage.ImagePositionX = 0.2; withImage.ImagePositionY = 0.8;
                PatchPlan imagePlan = NativeSkinPatch.Create(original, withImage, NativeSkinPatch.Version);
                File.WriteAllBytes(Path.Combine(output, "native-image-plan.app.so"), imagePlan.Bytes);
                imagePlan.AssetBytes = NativeSkinPatch.BuildAsset(withImage, originalAsset); imagePlan.AssetHash = NativeSkinPatch.Hash(imagePlan.AssetBytes);
                Assert(imagePlan.Hash != plan.Hash, theme.Name + "构图位置写入原生 Alignment 参数");
                byte[] pngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                bool pngAsset = imagePlan.AssetBytes.Length >= pngSignature.Length;
                for (int i = 0; pngAsset && i < pngSignature.Length; i++) pngAsset = imagePlan.AssetBytes[i] == pngSignature[i];
                Assert(pngAsset, theme.Name + "原生图片资源使用保留 8 位透明度的 PNG 数据");
                Assert(imagePlan.Bytes[0xA98ECF] == 0xE9 && imagePlan.Bytes[0xA98ED4] == 0x90
                    && imagePlan.Bytes[0xA98ED5] == 0x90 && imagePlan.Bytes[0xA98ED6] == 0x90,
                    theme.Name + "BoxDecoration 字段跳转补丁长度及边界正确");
                Assert(imagePlan.Bytes[0xA98DD1] == original[0xA98DD1] && imagePlan.Bytes[0xA98E55] == 0xE8
                    && imagePlan.Bytes[0xA98EEA] == 0xE8 && imagePlan.Bytes[0xA98F02] == 0xE8,
                    theme.Name + "复用原函数已有分配调用，图片构造不新增分配调用位置");
                Assert(imagePlan.AssetBytes != null && imagePlan.AssetHash != null, theme.Name + "图片资源计划包含字节和哈希");
                using (var assetStream = new MemoryStream(NativeSkinPatch.BuildAsset(withImage, originalAsset)))
                using (var assetImage = Image.FromStream(assetStream, true, true))
                    Assert(assetImage.Width == 200 && assetImage.Height == 100,
                        theme.Name + "图片资源保留源图宽高比以适配真实候选框尺寸");
                var withOpacity = withImage.Clone(); withOpacity.Opacity = 0.3;
                PatchPlan opacityPlan = NativeSkinPatch.Create(original, withOpacity, NativeSkinPatch.Version);
                Assert(opacityPlan.Hash != plan.Hash && Equal(NativeSkinPatch.BuildAsset(withOpacity, originalAsset), imagePlan.AssetBytes),
                    theme.Name + "整体透明度由原生图片合成一次应用，资源不重复衰减");
                bool shadowUntouched = true;
                for (int k = 0xA98DAE; k < 0xA98DCD; k++) shadowUntouched &= plan.Bytes[k] == original[k];
                Assert(shadowUntouched, theme.Name + "原生阴影颜色与透明度保持官方指令");
                var withDifferentComposition = withImage.Clone(); withDifferentComposition.ImagePositionX = 0.8; withDifferentComposition.ImagePositionY = 0.2; withDifferentComposition.ImageZoom = 3;
                Assert(NativeSkinPatch.Hash(NativeSkinPatch.BuildAsset(withDifferentComposition, originalAsset)) != imagePlan.AssetHash,
                    theme.Name + "图片拖动和缩放会改变原生资源内容");
            }
            var radiusTheme = new SkinTheme();
            var radiusHashes = new HashSet<string>();
            foreach (int radius in new int[] { 0, 9, 14, 40 })
            {
                radiusTheme.CornerRadius = radius;
                PatchPlan radiusPlan = NativeSkinPatch.Create(original, radiusTheme, NativeSkinPatch.Version);
                Assert(radiusHashes.Add(radiusPlan.Hash) && BitConverter.ToInt32(radiusPlan.Bytes, 0xA98802) == radius,
                    "原生圆角 " + radius + " 可独立应用，不受原 9px 常量限制");
            }
            string alphaPath = Path.Combine(output, "native-alpha-source.png");
            using (var alphaSource = new Bitmap(NativeSkinPatch.AssetCanvasWidth, NativeSkinPatch.AssetCanvasHeight, PixelFormat.Format32bppArgb))
            {
                alphaSource.SetPixel(NativeSkinPatch.AssetCanvasWidth / 2, NativeSkinPatch.AssetCanvasHeight / 2, Color.FromArgb(96, 230, 50, 90));
                alphaSource.Save(alphaPath, ImageFormat.Png);
            }
            var alphaTheme = new SkinTheme(); alphaTheme.SetImage(alphaPath); alphaTheme.ImageTint = 0;
            using (var alphaStream = new MemoryStream(NativeSkinPatch.BuildAsset(alphaTheme, originalAsset)))
            using (var alphaDecoded = Image.FromStream(alphaStream, true, true))
            using (var alphaImage = new Bitmap(alphaDecoded))
            {
                Color pixel = alphaImage.GetPixel(NativeSkinPatch.AssetCanvasWidth / 2, NativeSkinPatch.AssetCanvasHeight / 2);
                Assert(pixel.A == 255 && pixel.G > 50 && pixel.G < SkinTheme.ParseColor(alphaTheme.Background).G,
                    "图片的半透明像素与底色正确合成，整体透明度留给原生绘制");
            }
            string fixture = Path.Combine(output, "native-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            string target = Path.Combine(fixture, "app.so"); File.WriteAllBytes(target, original);
            string assetTarget = Path.Combine(fixture, "ctrl_space_guide.gif"); File.WriteAllBytes(assetTarget, originalAsset);
            var store = new NativePatchStore(target, assetTarget, Path.Combine(fixture, "backups"));
            var first = SkinTheme.Presets()[0]; var second = SkinTheme.Presets()[1];
            Assert(store.Inspect() == null, "官方副本被识别为未应用");
            store.Change(first);
            Assert(NativeSkinPatch.Hash(File.ReadAllBytes(store.BackupPath)) == NativeSkinPatch.OriginalHash, "备份哈希与官方文件相同");
            Assert(NativeSkinPatch.Hash(File.ReadAllBytes(store.AssetBackupPath)) == NativeSkinPatch.OriginalAssetHash, "背景资源备份哈希与官方资源相同");
            PatchPlan firstPlan = NativeSkinPatch.Create(original, first, NativeSkinPatch.Version);
            firstPlan.AssetBytes = NativeSkinPatch.BuildAsset(first, originalAsset); firstPlan.AssetHash = NativeSkinPatch.Hash(firstPlan.AssetBytes);
            Assert(store.Inspect().Name == first.Name && NativeSkinPatch.Hash(File.ReadAllBytes(target)) == firstPlan.Hash
                && NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) == firstPlan.AssetHash, "副本应用、图片资源和状态记录成功");
            store.Change(second);
            PatchPlan secondPlan = NativeSkinPatch.Create(original, second, NativeSkinPatch.Version);
            secondPlan.AssetBytes = NativeSkinPatch.BuildAsset(second, originalAsset); secondPlan.AssetHash = NativeSkinPatch.Hash(secondPlan.AssetBytes);
            Assert(store.Inspect().Name == second.Name && NativeSkinPatch.Hash(File.ReadAllBytes(assetTarget)) == secondPlan.AssetHash, "从本工具已知补丁再次应用图片资源成功");
            store.Change(null);
            Assert(Equal(File.ReadAllBytes(target), original) && Equal(File.ReadAllBytes(assetTarget), originalAsset) && store.Inspect() == null,
                "副本还原后原生文件和背景资源均逐字节相同");
            string[] auditFiles = Directory.GetFiles(store.AuditDirectory, "*.json");
            bool auditApply = false, auditRestore = false;
            foreach (string file in auditFiles)
            {
                var audit = JsonFile.Read<PatchAudit>(file);
                if (audit.Phase != "committed") continue;
                if (string.IsNullOrEmpty(audit.Time) || audit.Version != NativeSkinPatch.Version || audit.OriginalHash != NativeSkinPatch.OriginalHash
                    || string.IsNullOrEmpty(audit.PreviousAssetHash) || string.IsNullOrEmpty(audit.NewAssetHash) || audit.Edits.Count == 0)
                    throw new Exception("审计缺失必要信息。");
                foreach (PatchEdit edit in audit.Edits)
                    if (edit.Offset < 0 || string.IsNullOrEmpty(edit.OriginalBytes) || string.IsNullOrEmpty(edit.NewBytes)) throw new Exception("审计缺失字节差异。");
                auditApply |= audit.Operation == "apply"; auditRestore |= audit.Operation == "restore";
            }
            Assert(auditApply && auditRestore, "应用和还原审计包含时间、版本、哈希、偏移及前后字节");
            File.WriteAllBytes(target, foreign);
            Assert(Rejected(delegate { store.Change(first); }) && Rejected(delegate { store.Change(null); }) && Equal(File.ReadAllBytes(target), foreign),
                "第三方或更新文件拒绝应用和还原且不被覆盖");
            File.WriteAllBytes(target, original); File.WriteAllBytes(assetTarget, new byte[] { 1, 2, 3, 4 });
            Assert(Rejected(delegate { store.Change(first); }) && Rejected(delegate { store.Change(null); }) && Equal(File.ReadAllBytes(assetTarget), new byte[] { 1, 2, 3, 4 }),
                "第三方背景资源拒绝应用和还原且不被覆盖");
            File.WriteAllBytes(assetTarget, originalAsset);
            File.WriteAllBytes(target, original); File.WriteAllBytes(store.BackupPath, foreign);
            Assert(Rejected(delegate { store.Change(first); }) && Equal(File.ReadAllBytes(target), original), "损坏备份拒绝写入");
            File.WriteAllBytes(store.BackupPath, original);
            PatchPlan next = NativeSkinPatch.Create(original, first, NativeSkinPatch.Version);
            next.AssetBytes = NativeSkinPatch.BuildAsset(first, originalAsset); next.AssetHash = NativeSkinPatch.Hash(next.AssetBytes);
            JsonFile.Write(store.StatePath, new PatchState { Pending = true, NextHash = next.Hash, NextAssetHash = next.AssetHash,
                OriginalAssetHash = NativeSkinPatch.OriginalAssetHash, CurrentAssetHash = NativeSkinPatch.OriginalAssetHash, NextTheme = first });
            File.WriteAllBytes(target, next.Bytes);
            File.WriteAllBytes(assetTarget, next.AssetBytes);
            Assert(store.Inspect().Name == first.Name, "中断后已写入的待完成补丁能被识别");
            store.Change(null);
            Assert(Equal(File.ReadAllBytes(target), original) && Equal(File.ReadAllBytes(assetTarget), originalAsset), "中断后的待完成补丁能够完整还原");
            JsonFile.Write(store.StatePath, new PatchState { CurrentHash = new string('0', 64), CurrentTheme = first });
            Assert(Rejected(delegate { store.Change(first); }), "篡改或不一致状态记录拒绝写入");
            Assert(typeof(Native).GetMethod("Capture") == null && typeof(Native).GetMethod("SetWindowRgn") == null
                && typeof(Native).Assembly.GetType("WeTypeSkinStudio.LayeredOverlay") == null
                && typeof(Native).Assembly.GetType("WeTypeSkinStudio.CandidateRegionSession") == null,
                "构建中不存在屏幕捕获、叠加窗口和外部候选裁剪入口");
        }

        private static void ToolbarTests()
        {
            string path = Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_update.exe");
            byte[] original = File.ReadAllBytes(path);
            string installedHash = NativeToolbarPatch.Hash(original);
            if (NativeToolbarPatch.Hash(original) != NativeToolbarPatch.OfficialHash)
                original = File.ReadAllBytes(NativeToolbarStore.Installed().BackupPath);
            NativeToolbarPatch.RequireOriginal(original);
            byte[] patched = NativeToolbarPatch.Create(original, SkinTheme.Presets()[0]);
            File.WriteAllBytes(Path.Combine(output, "toolbar-patched.exe"), patched);
            Assert(patched.Length > 0 && NativeToolbarPatch.Hash(patched) != NativeToolbarPatch.Hash(original), "原生工具条 WXZ 背景资源可重建");
            Assert(NativeToolbarPatch.Hash(File.ReadAllBytes(path)) == installedHash, "工具条离线测试不修改安装文件");
            string fixture = Path.Combine(output, "toolbar-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string target = Path.Combine(fixture, "wetype_update.exe"); File.WriteAllBytes(target, original);
            var store = new NativeToolbarStore(target, Path.Combine(fixture, "backups"));
            var first = SkinTheme.Presets()[0]; var second = SkinTheme.Presets()[1];
            store.Change(first);
            Assert(store.Inspect().Name == first.Name && Equal(File.ReadAllBytes(store.BackupPath), original), "工具条应用并保存准确官方备份");
            byte[] next = NativeToolbarPatch.Create(original, second);
            JsonFile.Write(store.StatePath, new ToolbarPatchState { CurrentHash = NativeToolbarPatch.Hash(File.ReadAllBytes(target)),
                CurrentTheme = first, Pending = true, NextHash = NativeToolbarPatch.Hash(next), NextTheme = second });
            File.WriteAllBytes(target, next);
            Assert(store.Inspect().Name == second.Name, "工具条替换后中断仍能识别待提交皮肤");
            store.Change(null);
            Assert(Equal(File.ReadAllBytes(target), original) && store.Inspect() == null, "工具条中断状态可完整逐字节还原官方文件");
            byte[] foreign = (byte[])original.Clone(); foreign[foreign.Length - 1] ^= 1; File.WriteAllBytes(target, foreign);
            Assert(Rejected(delegate { store.Change(first); }) && Equal(File.ReadAllBytes(target), foreign), "工具条未知文件拒绝覆盖");
        }
        private static void TransactionTests()
        {
            byte[] app = File.ReadAllBytes(NativeSkinPatch.TargetPath);
            if (NativeSkinPatch.Hash(app) != NativeSkinPatch.OriginalHash) app = File.ReadAllBytes(NativePatchStore.Installed().BackupPath);
            byte[] asset = File.ReadAllBytes(NativeSkinPatch.AssetPath);
            if (NativeSkinPatch.Hash(asset) != NativeSkinPatch.OriginalAssetHash) asset = File.ReadAllBytes(NativePatchStore.Installed().AssetBackupPath);
            byte[] update = File.ReadAllBytes(Path.Combine(NativeSkinPatch.InstallDirectory, "wetype_update.exe"));
            if (NativeToolbarPatch.Hash(update) != NativeToolbarPatch.OfficialHash) update = File.ReadAllBytes(NativeToolbarStore.Installed().BackupPath);
            string fixture = Path.Combine(output, "transaction-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string appPath = Path.Combine(fixture, "app.so"), assetPath = Path.Combine(fixture, "image.gif"), toolbarPath = Path.Combine(fixture, "wetype_update.exe");
            File.WriteAllBytes(appPath, app); File.WriteAllBytes(assetPath, asset); File.WriteAllBytes(toolbarPath, update);
            var candidate = new NativePatchStore(appPath, assetPath, Path.Combine(fixture, "backups"));
            var toolbar = new NativeToolbarStore(toolbarPath, Path.Combine(fixture, "backups"));
            var transaction = new NativeSkinTransaction(candidate, toolbar);
            transaction.Apply(SkinTheme.Presets()[0]); transaction.Rollback();
            Assert(Equal(File.ReadAllBytes(appPath), app) && Equal(File.ReadAllBytes(assetPath), asset)
                && Equal(File.ReadAllBytes(toolbarPath), update), "运行验证失败时三个原生文件均能回滚为官方原始字节");
            candidate.Change(SkinTheme.Presets()[0]); toolbar.Change(SkinTheme.Presets()[2]);
            byte[] previousApp = File.ReadAllBytes(appPath), previousAsset = File.ReadAllBytes(assetPath), previousToolbar = File.ReadAllBytes(toolbarPath);
            transaction = new NativeSkinTransaction(candidate, toolbar);
            transaction.Apply(SkinTheme.Presets()[1]); transaction.Rollback();
            Assert(Equal(File.ReadAllBytes(appPath), previousApp) && Equal(File.ReadAllBytes(assetPath), previousAsset)
                && Equal(File.ReadAllBytes(toolbarPath), previousToolbar), "回滚分别恢复候选框和工具条操作前各自的皮肤");
            transaction = new NativeSkinTransaction(candidate, toolbar);
            using (var locked = new FileStream(toolbarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool applyFailed = false, rollbackReported = false;
                try { transaction.Apply(SkinTheme.Presets()[1]); } catch (IOException) { applyFailed = true; }
                try { transaction.Rollback(); } catch (IOException) { rollbackReported = true; }
                Assert(applyFailed && rollbackReported && Equal(File.ReadAllBytes(appPath), previousApp)
                    && Equal(File.ReadAllBytes(assetPath), previousAsset), "工具条锁定失败仍独立回滚候选框，并报告未完成的回滚");
            }
            transaction.Rollback();
            Assert(Equal(File.ReadAllBytes(toolbarPath), previousToolbar), "工具条文件解锁后可重试完成回滚");
        }
        private static void EditorTests()
        {
            int inspections = 0;
            SkinTheme installed = SkinTheme.Presets()[5];
            using (var editor = new EditorForm(delegate {
                inspections++;
                if (inspections == 1) throw new InvalidDataException("首次检测遇到短暂文件状态");
                return new NativeSkinInspection { CandidateTheme = installed, ToolbarTheme = installed };
            }))
            {
                Assert(Rejected(delegate { editor.UpdateNativeInspection(); }), "启动检测失败可复现");
                editor.PrepareBootstrap();
                FrontendState state = editor.State(false);
                Assert(state.Applied != null && state.Applied.Name == installed.Name && state.Compatible,
                    "页面初始化重新核对已应用皮肤，首次检测失败不会永久禁用还原");
            }
            bool readable = true;
            using (var editor = new EditorForm(delegate {
                if (!readable) throw new InvalidDataException("记录不一致");
                return new NativeSkinInspection { CandidateTheme = installed, ToolbarTheme = installed };
            }))
            {
                editor.PrepareBootstrap();
                readable = false;
                editor.PrepareBootstrap();
                FrontendState failed = editor.State(false);
                Assert(failed.Applied == null && !failed.Compatible && !string.IsNullOrEmpty(failed.InspectionError),
                    "检测失败不沿用已应用缓存，并明确返回无法确认状态");
                readable = true;
                editor.PrepareBootstrap();
                Assert(editor.State(false).Applied != null && editor.State(false).InspectionError == null,
                    "重新检测成功清除过期的检测错误");
            }
            var theme = new SkinTheme();
            RectangleF center = theme.ImageBounds(new Size(200, 100), new Size(100, 100));
            Assert(center == new RectangleF(-50, 0, 200, 100), "图片默认 cover 居中构图");
            theme.ImagePositionX = 0;
            Assert(theme.ImageBounds(new Size(200, 100), new Size(100, 100)).X == 0, "图片可以移动到左边缘");
            theme.ImagePositionX = 1;
            Assert(theme.ImageBounds(new Size(200, 100), new Size(100, 100)).X == -100, "图片可以移动到右边缘且不露底");
            theme.ImageZoom = 2; theme.ImagePositionY = 1;
            Assert(theme.ImageBounds(new Size(200, 100), new Size(100, 100)) == new RectangleF(-300, -100, 400, 200), "缩放后支持两轴构图且不露底");
            theme.ImagePositionX = .23; theme.ImagePositionY = .78; theme.ImageZoom = 1.65;
            string path = Path.Combine(output, "composition.wtskin.json"); ThemeStore.Save(theme, path);
            SkinTheme loaded = ThemeStore.Load(path);
            Assert(loaded.ImagePositionX == .23 && loaded.ImagePositionY == .78 && loaded.ImageZoom == 1.65, "图片构图导出导入保留位置和缩放");
            string json = "{\"formatVersion\":1,\"name\":\"legacy\",\"background\":\"#FFFFFF\",\"foreground\":\"#222222\",\"accent\":\"#12936A\",\"border\":\"#888888\",\"opacity\":1,\"cornerRadius\":14,\"imageTint\":0}";
            File.WriteAllText(path, json);
            loaded = ThemeStore.Load(path);
            Assert(loaded.ImagePositionX == .5 && loaded.ImagePositionY == .5 && loaded.ImageZoom == 1, "旧皮肤文件默认居中且不缩放");
            theme.ImageZoom = double.NaN;
            Assert(Rejected(delegate { theme.Validate(); }), "拒绝无效构图缩放");
            theme.ImageZoom = 1; theme.ImagePositionX = -1;
            Assert(Rejected(delegate { theme.Validate(); }), "拒绝越界构图位置");
            theme.SetImage(Path.Combine(output, "sample-background.png"));
            Assert(theme.ImagePositionX == .5 && theme.ImagePositionY == .5 && theme.ImageZoom == 1, "替换图片重置构图");
            using (var picture = new Bitmap(200, 100))
            using (var g = Graphics.FromImage(picture))
            {
                g.Clear(Color.Red); g.FillRectangle(Brushes.Blue, 100, 0, 100, 100);
                picture.Save(Path.Combine(output, "drag-background.png"), ImageFormat.Png);
            }
            theme.SetImage(Path.Combine(output, "drag-background.png")); theme.Opacity = 1; theme.ImageTint = 0;
            using (var renderer = new SkinRenderer())
            using (var sample = Flat(new Size(100, 100), Color.White))
            using (var behind = Flat(sample.Size, Color.White))
            {
                theme.ImagePositionX = 0;
                using (var rendered = renderer.Render(sample, behind, theme)) Assert(Near(rendered.GetPixel(25, 25), Color.Red), "移动图片实际改变预览像素");
                theme.ImagePositionX = 1;
                using (var rendered = renderer.Render(sample, behind, theme)) Assert(Near(rendered.GetPixel(25, 25), Color.Blue), "构图改变时渲染缓存正确刷新");
            }
            var request = EditorForm.ParseRequest("{\"id\":\"check\",\"action\":\"save\",\"theme\":" + File.ReadAllText(Path.Combine(output, "composition.wtskin.json")) + "}");
            Assert(request.Id == "check" && request.Action == "save" && request.Theme.ImageZoom == 1, "前端桥可以读取旧皮肤请求");
            Assert(Rejected(delegate { EditorForm.ParseRequest("{\"action\":\"save\"}"); }), "前端桥拒绝缺少请求标识的消息");
            Assert(Rejected(delegate { EditorForm.ParseRequest(new string('x', 13 * 1024 * 1024 + 1)); }), "前端桥拒绝过大消息");
            foreach (string name in FrontendAssets.Names) Assert(FrontendAssets.Read("Frontend." + name).Length > 0, "前端资产已内嵌：" + name);
            foreach (string name in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
                Assert(FrontendAssets.Read("Dependencies." + name).Length > 0, "运行依赖已内嵌：" + name);
        }
    }
}
