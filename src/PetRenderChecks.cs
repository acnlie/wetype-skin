using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace WeTypeSkinStudio
{
    internal static class PetRenderChecks
    {
        internal static void Visibility(string directory)
        {
            Directory.CreateDirectory(directory);
            using (var assets = new PetAssets())
            using (var window = new PetWindow("桌宠后台显示检查"))
            {
                PetClip clip = assets.Prepare("typing");
                var bounds = new Rectangle(-1000, -1000, 128, 128);
                for (int i = 0; i < 3; i++)
                {
                    window.Render(clip, 24, bounds, false);
                    if (!window.NativeVisible || !window.NativeTopMost)
                        throw new InvalidDataException("后台显示没有保持原生可见与置顶：" + i);
                    window.Hide();
                    if (window.NativeVisible) throw new InvalidDataException("收起后仍然显示。");
                }
                if (window.RenderFailures != 0 || window.RenderedFrames != 3)
                    throw new InvalidDataException("重复显示提交失败。");
                PetClip typing = assets.Prepare("typing");
                var clock = Stopwatch.StartNew();
                if (assets.TryPrepare("rice", typing) != null) throw new InvalidDataException("未缓存动作没有进入异步加载。");
                if (assets.TryPrepare("typing", typing) != typing) throw new InvalidDataException("输入动作等待其他动画解码。");
                PetClip rice = null;
                while (rice == null && clock.ElapsedMilliseconds < 5000)
                { rice = assets.TryPrepare("rice", typing); if (rice == null) Thread.Sleep(5); }
                if (rice == null || !rice.Loaded || !typing.Loaded) throw new InvalidDataException("异步动画加载失败或输入动作缓存丢失。");
                window.Render(rice, 24, bounds, false);
                if (!window.NativeVisible || !window.NativeTopMost || window.RenderFailures != 0)
                    throw new InvalidDataException("异步切换后显示状态错误。");
            }
            File.WriteAllText(Path.Combine(directory, "summary.txt"),
                "PASS: three show/hide cycles retained native visibility and topmost; hidden startup supported.\r\n" +
                "Async action decoding preserved an immediately available typing clip and native visibility.\r\n");
        }
        internal static void Run(string directory)
        {
            Directory.CreateDirectory(directory);
            int frames = 0, transitions = 0;
            var actions = new List<string>();
            using (var assets = new PetAssets())
            using (var window = new PetWindow("桌宠透明帧检查"))
            {
                var bounds = new Rectangle(-1000, -1000, 128, 128);
                foreach (string name in assets.Clips.Keys)
                {
                    PetClip clip = assets.Prepare(name);
                    for (int frame = 0; frame < clip.Frames; frame++)
                    {
                        window.Render(clip, frame, bounds, false);
                        Inspect(window.Pixels, name + ":" + frame);
                        frames++;
                    }
                    actions.Add(name);
                }
                // Exercise the same crossfade and surface resize path as runtime.
                foreach (string name in new[] { "rice", "typing", "thinking", "navigating", "committed", "mode-full", "mode-half", "mode-english", "mode-chinese", "sleepy" })
                {
                    PetClip clip = assets.Prepare(name);
                    window.Render(clip, 0, bounds, true);
                    Inspect(window.Pixels, "transition:" + name);
                    window.Render(clip, 1, new Rectangle(-1000, -1000, 140, 140), true);
                    Inspect(window.Pixels, "resize:" + name);
                    transitions++;
                }
                if (window.RenderFailures != 0) throw new InvalidDataException("透明窗口提交失败。");
                if (window.RenderedFrames != frames + transitions * 2) throw new InvalidDataException("动画帧未全部提交。");
            }
            File.WriteAllText(Path.Combine(directory, "summary.txt"),
                "PASS: " + actions.Count + " actions, " + frames + " frames, " + transitions + " transitions/resizes.\r\n" +
                "Every submitted DIB retained transparency, visible character pixels and premultiplied alpha.\r\n" +
                "This verifies native render buffers, not long-term compositor behavior.\r\n");
            File.WriteAllLines(Path.Combine(directory, "actions.txt"), actions);
        }
        private static void Inspect(byte[] pixels, string label)
        {
            if (pixels == null) throw new InvalidDataException("没有绘制像素：" + label);
            int transparent = 0, visible = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                if (alpha == 0) transparent++;
                if (alpha > 20) visible++;
                if (pixels[i] > alpha || pixels[i + 1] > alpha || pixels[i + 2] > alpha)
                    throw new InvalidDataException("非预乘 alpha：" + label);
            }
            if (transparent < pixels.Length / 32 || visible < 32)
                throw new InvalidDataException("不透明底色或空白帧：" + label);
        }
    }
}
