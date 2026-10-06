using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WeTypeSkinStudio
{
    internal static class TrayPunctuationChecks
    {
        internal static void Run(string directory)
        {
            Directory.CreateDirectory(directory);
            var checks = new List<string>();
            Action<bool, string> check = delegate(bool ok, string name)
            { if (!ok) throw new InvalidDataException(name); checks.Add(name); };
            string executable = Application.ExecutablePath;
            ProcessStartInfo observed = null;
            string launched = PetHost.LaunchEditor(" \"" + executable + "\"\r\n", executable, delegate(ProcessStartInfo start) { observed = start; });
            check(launched == executable && observed.FileName == executable && !observed.UseShellExecute
                && observed.WorkingDirectory == Path.GetDirectoryName(executable), "带换行和引号的托盘路径直接启动，工作目录有效");
            string missing = Path.Combine(directory, "missing-editor.exe");
            launched = PetHost.LaunchEditor(missing, executable, delegate(ProcessStartInfo start) { observed = start; });
            check(launched == executable, "编辑器被移动后回退到后台自带程序");
            int attempts = 0;
            launched = PetHost.LaunchEditor(executable, executable, delegate(ProcessStartInfo start)
            { attempts++; if (attempts == 1) throw new Win32Exception(2); });
            check(attempts == 2 && launched == executable, "启动失败后继续尝试备用入口");
            bool failed = false;
            try { PetHost.LaunchEditor(missing, missing, delegate { throw new Exception("不应启动缺失文件"); }); }
            catch (IOException) { failed = true; }
            check(failed, "两条路径均失效时返回可处理错误");
            var before = new PetInputMode { Target = 1, Open = true, Conversion = 1 };
            var chinese = new PetInputMode { Target = 1, Open = true, Conversion = 0x401 };
            check(PetModeState.FeedbackClip(before, chinese) == "punctuation-chinese"
                && PetModeState.FeedbackClip(chinese, before) == "punctuation-english", "IMM标点状态双向映射正确举牌");
            var gesture = new PetToolbarGesture(); var bounds = new Rectangle(100, 100, 220, 40);
            check(gesture.Begin(bounds, new Point(170, 115), 96) && gesture.End(bounds, new Point(170, 115))
                && gesture.Region == "punctuation", "工具条标点按钮识别为独立反馈类型");
            gesture.Begin(bounds, new Point(157, 115), 96);
            check(!gesture.End(bounds, new Point(159, 115)), "跨按钮拖动不当作标点切换");
            using (var assets = new PetAssets())
            using (var preview = new Bitmap(480, 160))
            using (var graphics = Graphics.FromImage(preview))
            {
                check(assets.Clips.Count == 26, "26组动作与标点素材内嵌");
                var motion = new PetMotion();
                motion.Select("mode", "punctuation-chinese", 1, true, assets.Clips);
                check(motion.Select("mode", "punctuation-english", 1.1, true, assets.Clips)
                    && motion.Clip == "punctuation-english", "连续标点切换立即换牌");
                graphics.Clear(Color.FromArgb(240, 245, 248));
                string[] names = { "punctuation-chinese", "punctuation-english", "punctuation" };
                for (int i = 0; i < names.Length; i++)
                {
                    var clip = assets.Prepare(names[i]);
                    graphics.DrawImage(clip.ImageAt(24), i * 160, 0, 160, 160);
                }
                preview.Save(Path.Combine(directory, "punctuation-signs.png"), ImageFormat.Png);
            }
            File.WriteAllLines(Path.Combine(directory, "checks.txt"), checks);
            File.WriteAllText(Path.Combine(directory, "summary.txt"), "PASS: " + checks.Count + " targeted tray/punctuation checks.\r\nNo native installation modified.\r\n");
        }
    }
}
