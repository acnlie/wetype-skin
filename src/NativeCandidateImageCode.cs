using System;
using System.Collections.Generic;
using System.IO;

namespace WeTypeSkinStudio
{
    // Reuse allocation calls that already have Dart stack maps. Helpers only
    // initialize objects; the one write-barrier call is a Dart leaf stub.
    internal static class NativeCandidateImageCode
    {
        internal const int Cave = 0xCFD680;
        private const int Capacity = 0x2980;
        private static byte[] Hex(string s)
        {
            string[] parts = s.Split(' '); byte[] b = new byte[parts.Length];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(parts[i], 16);
            return b;
        }
        private sealed class Code
        {
            public readonly List<byte> Bytes = new List<byte>();
            private readonly int origin;
            public Code(int start) { origin = start; }
            public void Emit(string s) { Bytes.AddRange(Hex(s)); }
            public void Pool(int displacement, string opcode = "49 8b 87") { Emit(opcode); Bytes.AddRange(BitConverter.GetBytes(displacement)); }
            public void Local(byte displacement) { Emit("48 8b 45"); Bytes.Add(displacement); }
            public void Store(byte displacement) { Emit("48 89 41"); Bytes.Add(displacement); }
            public void Double(double value, byte field)
            {
                Emit("48 b8"); Bytes.AddRange(BitConverter.GetBytes(value));
                // Storing raw IEEE bits avoids any additional FP-register liveness.
                Store(field);
            }
            public void Jump(int target) { int at = origin + Bytes.Count; Emit("e9"); Bytes.AddRange(BitConverter.GetBytes(target - at - 5)); }
            public void Barrier()
            {
                // RCX destination, RAX value. Same conditional leaf write barrier
                // as the official compiled stores in this snapshot.
                Emit("44 8a 59 ff 41 c1 eb 02 45 23 5e 40 44 84 58 ff 74 05");
                int at = origin + Bytes.Count; Emit("e8"); Bytes.AddRange(BitConverter.GetBytes(0xCD47E1 - at - 5));
            }
            public void LeaR11(int target)
            {
                int at = origin + Bytes.Count; Emit("4c 8d 1d"); Bytes.AddRange(BitConverter.GetBytes(target - at - 7));
            }
            public void LeaR10(int target)
            {
                int at = origin + Bytes.Count; Emit("4c 8d 15"); Bytes.AddRange(BitConverter.GetBytes(target - at - 7));
            }
            public int UnlessEqual() { Emit("0f 85"); int at = Bytes.Count; Bytes.AddRange(new byte[4]); return at; }
            public void Bind(int at) { byte[] delta = BitConverter.GetBytes(Bytes.Count - at - 4); for (int i = 0; i < 4; i++) Bytes[at + i] = delta[i]; }
        }
        private static void Replace(PatchPlan plan, byte[] original, int start, int end, byte[] replacement, string name)
        {
            if (replacement.Length > end - start) throw new InvalidDataException("原生构造片段超出指令边界：" + name);
            byte[] old = new byte[end - start], next = new byte[end - start];
            Array.Copy(original, start, old, 0, old.Length);
            for (int i = 0; i < next.Length; i++) next[i] = 0x90;
            Array.Copy(replacement, next, replacement.Length);
            NativeSkinPatch.Edit(plan, start, old, next, name);
        }
        private static void Hook(PatchPlan plan, byte[] original, int start, int end, int helper, string name)
        {
            var c = new Code(start); c.Jump(helper); Replace(plan, original, start, end, c.Bytes.ToArray(), name);
        }
        private static void Retarget(PatchPlan plan, byte[] original, int site, int expectedTarget, int target, string name)
        {
            if (original[site] != 0xE8 || site + 5 + BitConverter.ToInt32(original, site + 1) != expectedTarget)
                throw new InvalidDataException("原生分配调用不匹配：" + name);
            byte[] next = new byte[5]; next[0] = 0xE8;
            Array.Copy(BitConverter.GetBytes(target - site - 5), 0, next, 1, 4);
            Replace(plan, original, site, site + 5, next, name);
        }
        private static void Put(byte[] cave, Code code, int offset)
        {
            if (code.Bytes.Count > 0x100) throw new InvalidDataException("原生无分配辅助代码过长。");
            code.Bytes.CopyTo(cave, offset);
        }
        private static Code InscribeParameter(int origin, bool vertical, double value)
        {
            var code = new Code(origin);
            code.Emit(vertical ? "f2 0f 10 40 0f" : "f2 0f 10 40 07");
            code.Emit("41 52 41 53");
            code.LeaR11(0x6CC72E); code.Emit("4c 39 5d 08"); int a = code.UnlessEqual();
            code.Emit("4c 8b 5d 00"); code.LeaR10(0xC8D59B);
            code.Emit("4d 39 53 08"); int b = code.UnlessEqual();
            // The fit argument is an immutable enum, so reading it from the
            // parent call's argument area is safe across moving collections.
            code.Emit("4d 8b 53 40 4d 3b 97"); code.Bytes.AddRange(BitConverter.GetBytes(0x29C7F));
            int c = code.UnlessEqual();
            code.Emit("49 bb"); code.Bytes.AddRange(BitConverter.GetBytes(value)); code.Emit("66 49 0f 6e c3");
            code.Bind(a); code.Bind(b); code.Bind(c); code.Emit("41 5b 41 5a");
            code.Jump(vertical ? 0x697CB2 : 0x697C9C); return code;
        }
        public static void Apply(PatchPlan plan, byte[] original, SkinTheme theme)
        {
            // 1. Keep native Offset, BoxShadow and Array allocations. Initialize
            // the fresh Array directly as List<BoxShadow>, avoiding a redundant
            // growable wrapper and freeing its mapped allocation site.
            var array = new Code(Cave);
            array.Emit("48 89 c1 48 89 4d d8");
            array.Pool(0x1AC47); array.Store(0x07);
            array.Local(0xC8); array.Store(0x17);
            array.Jump(0xA98E55);

            // 2. Allocate Alignment at the old wrapper call. Keep it in the
            // existing color pointer slot until BoxDecoration can root it.
            var alignment = new Code(Cave + 0x100);
            alignment.Emit("48 89 c1 48 89 4d e0");
            alignment.Double(theme.ImagePositionX * 2 - 1, 0x07);
            alignment.Double(theme.ImagePositionY * 2 - 1, 0x0F);
            alignment.Local(0xD8); alignment.Emit("48 89 45 d0");
            alignment.Jump(0xA98E71);

            // 3. Share the already-created BorderRadius with the later clip.
            // BoxDecoration temporarily roots Alignment until the image is ready.
            var box = new Code(Cave + 0x200);
            box.Local(0xC8); box.Store(0x1F);
            box.Local(0xD0); box.Store(0x27);
            box.Local(0xE0); box.Store(0x0F);
            box.Pool(0x1AFC7, "49 8b 97");
            box.Jump(0xA98EDF);

            // 4. Replace the redundant second Radius allocation with AssetImage.
            var asset = new Code(Cave + 0x300);
            asset.Emit("48 89 c1 48 89 4d e0");
            asset.Pool(0x29777); asset.Store(0x0F);
            asset.Jump(0xA98F02);

            // 5. Replace the redundant second BorderRadius allocation with
            // DecorationImage, then publish it through a proper write barrier.
            var image = new Code(Cave + 0x400);
            image.Emit("48 89 c1");
            image.Local(0xE0); image.Store(0x07);
            image.Pool(0x29C7F); image.Store(0x1F); // BoxFit.cover
            image.Emit("48 8b 55 d8 48 8b 42 0f"); image.Store(0x27);
            image.Pool(0x1AFBF); image.Store(0x37);
            image.Emit("49 8b 46 78"); image.Store(0x3F); image.Store(0x5F); image.Store(0x67);
            image.Double(1.0, 0x47); image.Double(theme.Opacity, 0x4F);
            image.Pool(0x1AFAF); image.Store(0x57);
            image.Emit("48 89 c8 48 8b 4d d8 48 89 41 0f"); image.Barrier();
            image.Emit("48 8b 41 1f 48 89 45 d0");
            image.Jump(0xA98F22);

            // Whole-program optimization folded original DecorationImage.fit,
            // alignment and opacity into constants. Restore property reads in
            // its painter; changing the constructor fields alone is insufficient.
            var opacity = new Code(Cave + 0x500);
            opacity.Emit("f2 0f 10 45 a8 4c 8b 5d f8 f2 41 0f 59 43 4f 4d 8b 5b 1f 4c 89 5c 24 30");
            opacity.Jump(0xC8D4C1);
            // Avoid retaining the painter's dead local across paintImage's
            // allocations. Crop parameters are immutable skin-time scalars;
            // scope them to the exact caller chain and cover enum argument.
            var alignX = InscribeParameter(Cave + 0x700, false, theme.ImagePositionX * 2 - 1);
            var alignY = InscribeParameter(Cave + 0x800, true, theme.ImagePositionY * 2 - 1);

            byte[] cave = new byte[Capacity]; for (int i = 0; i < cave.Length; i++) cave[i] = 0xCC;
            Put(cave, array, 0); Put(cave, alignment, 0x100); Put(cave, box, 0x200); Put(cave, asset, 0x300); Put(cave, image, 0x400);
            Put(cave, opacity, 0x500); Put(cave, alignX, 0x700); Put(cave, alignY, 0x800);
            NativeSkinPatch.Edit(plan, 0xD0, BitConverter.GetBytes((long)0x725680), BitConverter.GetBytes((long)0x728000), "扩展原生可执行段文件长度");
            NativeSkinPatch.Edit(plan, 0xD8, BitConverter.GetBytes((long)0x725680), BitConverter.GetBytes((long)0x728000), "扩展原生可执行段内存长度");
            NativeSkinPatch.Edit(plan, Cave, new byte[Capacity], cave, "保留原生分配栈映射的图片构造辅助代码");
            Hook(plan, original, 0xA98E3F, 0xA98E55, Cave, "原生阴影定长列表初始化");
            Retarget(plan, original, 0xA98E55, 0xCD4D84, 0x69765C, "复用列表包装分配位置构造图片对齐");
            Hook(plan, original, 0xA98E5A, 0xA98E71, Cave + 0x100, "原生图片对齐参数初始化");
            Hook(plan, original, 0xA98ECF, 0xA98EDF, Cave + 0x200, "原生图片构造根引用");
            Retarget(plan, original, 0xA98EEA, 0x6B9914, 0x8A5A0C, "复用重复圆角分配位置构造图片来源");
            Hook(plan, original, 0xA98EEF, 0xA98F02, Cave + 0x300, "原生图片来源初始化");
            Retarget(plan, original, 0xA98F02, 0x6CF800, 0x96E2D8, "复用重复圆角对象分配位置构造图片装饰");
            Hook(plan, original, 0xA98F07, 0xA98F22, Cave + 0x400, "原生图片装饰初始化及写屏障");
            Hook(plan, original, 0xC8D4BC, 0xC8D4C1, Cave + 0x500, "图片绘制读取透明度并乘以原生混合系数");
            Replace(plan, original, 0xC8D557, 0xC8D563, new byte[0], "保留分配前已读取的图片 fit 参数");
            Hook(plan, original, 0x697C97, 0x697C9C, Cave + 0x700, "候选图片等比裁剪水平位置");
            Hook(plan, original, 0x697CAD, 0x697CB2, Cave + 0x800, "候选图片等比裁剪垂直位置");
        }
    }
}
