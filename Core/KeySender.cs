using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyMacro.Core
{
    /// <summary>
    /// 通过 SendInput 模拟键盘输入(比 keybd_event 更可靠,游戏内通常有效)。
    /// </summary>
    public static class KeySender
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        /// <summary>
        /// 按下或抬起一个按键。
        /// 使用"扫描码"注入(KEYEVENTF_SCANCODE):DirectInput 独占键盘的游戏
        /// (如恐怖黎明)直接读取硬件扫描码状态,只发虚拟键码(VK)它们收不到。
        /// </summary>
        public static void SendKey(int vk, bool keyUp = false)
        {
            ushort scan = (ushort)MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            if (scan == 0) scan = (ushort)vk;

            uint flags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
            // 扩展键(右Ctrl/右Alt/Win/Apps/方向键等)需加 EXTENDEDKEY 标志,
            // 否则 DirectInput 会解析成错误扫描码
            if (IsExtendedKey(vk))
                flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = scan,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private static bool IsExtendedKey(int vk)
        {
            return vk is 0x5B or 0x5C   // LWin / RWin
                or 0xA3                // RCtrl
                or 0xA5                // RAlt
                or 0x5D                // Apps
                or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PageUp/Down Home End 方向键
                or 0x2D or 0x2E        // Insert / Delete
                or 0x2F;               // Help
        }

        /// <summary>通过 Unicode 通道输入任意文本(支持中文等,不受输入法影响)。</summary>
        public static void SendUnicodeText(string text, int charDelayMs = 10)
        {
            foreach (char c in text)
            {
                SendUnicodeChar(c);
                if (charDelayMs > 0) Thread.Sleep(charDelayMs);
            }
        }

        private static void SendUnicodeChar(char c)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// 完整发送一次按键组合,如 Ctrl+Shift+C:
        /// 依次按下修饰键、按下主键、抬起主键、抬起修饰键。
        /// </summary>
        public static void SendCombo(IReadOnlyList<int> mods, int mainKey, int holdMs = 30)
        {
            foreach (int m in mods) { SendKey(m); Thread.Sleep(15); }
            SendKey(mainKey);
            Thread.Sleep(holdMs);
            SendKey(mainKey, keyUp: true);
            for (int i = mods.Count - 1; i >= 0; i--) { SendKey(mods[i], keyUp: true); Thread.Sleep(10); }
        }
    }
}
