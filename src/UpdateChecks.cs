using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace WeTypeSkinStudio
{
    internal static class UpdateChecks
    {
        internal static void Run(string directory)
        {
            Directory.CreateDirectory(directory); var results = new List<string>();
            Action<bool, string> check = delegate(bool condition, string label)
            { if (!condition) throw new InvalidDataException(label); results.Add(label); };
            var activity = new PetActivity(); activity.Input(1); activity.Tick(true, 4);
            check(activity.State == "waiting", "候选停留进入等待动作");
            activity.Navigate(5); activity.Tick(true, 5.1); check(activity.State == "navigating", "翻页即时中断等待");
            using (var assets = new PetAssets())
            {
                check(assets.Clips.Count == 26, "26组动作和举牌素材内嵌");
                var motion = new PetMotion(); var seen = new HashSet<string>();
                for (int i = 0; i < 24; i++) { motion.Select("idle", "mode", i * 11, true, assets.Clips); seen.Add(motion.Clip); }
                check(seen.Count >= 4, "待机自然轮换多组动作");
                motion.Select("waiting", "mode", 300, true, assets.Clips);
                check(motion.Clip == "thinking" || motion.Clip == "watch" || motion.Clip == "tapping" || motion.Clip == "cube", "长时间候选有独立动作池");
                motion.Select("mode", "mode-english", 301, true, assets.Clips);
                check(motion.Clip == "mode-english", "实际模式驱动正确举牌");
                using (var preview = new Bitmap(640, 480))
                using (var graphics = Graphics.FromImage(preview))
                {
                    graphics.Clear(Color.FromArgb(240, 245, 248));
                    string[] names = { "mode-chinese", "mode-english", "mode-half", "mode-full", "rice", "tea", "stretch", "cube", "thinking", "humming", "bubbles", "yawn" };
                    for (int i = 0; i < names.Length; i++)
                    {
                        PetClip clip = assets.Prepare(names[i]);
                        graphics.DrawImage(clip.ImageAt(names[i].StartsWith("mode-") ? 24 : 96), (i % 4) * 160, (i / 4) * 160, 160, 160);
                    }
                    preview.Save(Path.Combine(directory, "actions.png"), ImageFormat.Png);
                }
                using (var window = new PetWindow("桌宠透明渲染检查"))
                {
                    PetClip clip = assets.Prepare("mode-english");
                    var bounds = new Rectangle(200, 200, 128, 128);
                    window.Render(clip, 24, bounds, false);
                    byte[] pixels = window.Pixels; bool alpha = false, premultiplied = true;
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        alpha |= pixels[i + 3] == 0;
                        premultiplied &= pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3] && pixels[i + 2] <= pixels[i + 3];
                    }
                    check(alpha && premultiplied && window.RenderFailures == 0, "DIB保留透明和预乘alpha，不转成黑底");
                    window.Render(clip, 25, new Rectangle(200, 200, 140, 140), false);
                    check(window.RenderFailures == 0 && window.RenderedFrames == 2, "原子尺寸更新继续绘制");
                }
            }
            var theme = new SkinTheme { PetBackground = false, PetStartWithWindows = true };
            string path = Path.Combine(directory, "settings.json"); ThemeStore.Save(theme, path); var loaded = ThemeStore.Load(path);
            check(loaded.PetBackground && loaded.PetStartWithWindows, "旧后台关闭值自动迁移，登录启动设置保留");
            File.WriteAllLines(Path.Combine(directory, "checks.txt"), results);
            File.WriteAllText(Path.Combine(directory, "summary.txt"), "PASS: " + results.Count + " targeted checks; no native installation modified.");
        }
    }
}
