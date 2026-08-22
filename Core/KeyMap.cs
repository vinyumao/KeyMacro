using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyMacro.Core
{
    /// <summary>
    /// 虚拟键码(VK)与友好名称的映射。友好名称用于界面展示与配置持久化。
    /// </summary>
    public static class KeyMap
    {
        // 修饰键 VK
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12; // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        public static readonly int[] ModifierKeys = { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN };

        private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
            ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
            ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
            ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
            ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
            ["Z"] = 0x5A,
            ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
            ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
            ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
            ["F11"] = 0x7A, ["F12"] = 0x7B, ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E,
            ["F16"] = 0x7F, ["F17"] = 0x80, ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83,
            ["F21"] = 0x84, ["F22"] = 0x85, ["F23"] = 0x86, ["F24"] = 0x87,
            ["Enter"] = 0x0D, ["Return"] = 0x0D,
            ["Esc"] = 0x1B, ["Escape"] = 0x1B,
            ["Space"] = 0x20,
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["CapsLock"] = 0x14,
            ["PrintScreen"] = 0x2C, ["PrtSc"] = 0x2C,
            ["ScrollLock"] = 0x91,
            ["Pause"] = 0x13,
            ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Del"] = 0x2E,
            ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
            ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63, ["Num4"] = 0x64,
            ["Num5"] = 0x65, ["Num6"] = 0x66, ["Num7"] = 0x67, ["Num8"] = 0x68, ["Num9"] = 0x69,
            ["NumLock"] = 0x90,
            ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Separator"] = 0x6C, ["Subtract"] = 0x6D,
            ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
            ["Oem_1"] = 0xBA, ["Oem_Plus"] = 0xBB, ["Oem_Comma"] = 0xBC, ["Oem_Minus"] = 0xBD,
            ["Oem_Period"] = 0xBE, ["Oem_2"] = 0xBF, ["Oem_3"] = 0xC0,
            ["Oem_4"] = 0xDB, ["Oem_5"] = 0xDC, ["Oem_6"] = 0xDD, ["Oem_7"] = 0xDE,
            ["Oem_8"] = 0xDF, ["Oem_102"] = 0xE2,
            ["LShift"] = 0xA0, ["RShift"] = 0xA1,
            ["LCtrl"] = 0xA2, ["RCtrl"] = 0xA3,
            ["LAlt"] = 0xA4, ["RAlt"] = 0xA5,
            ["LWin"] = 0x5B, ["RWin"] = 0x5C,
            ["Apps"] = 0x5D,
            ["Shift"] = VK_SHIFT, ["Ctrl"] = VK_CONTROL, ["Alt"] = VK_MENU, ["Win"] = VK_LWIN,
            ["Sleep"] = 0x5F,
            ["Browser_Back"] = 0xA6, ["Browser_Forward"] = 0xA7,
            ["Media_PlayPause"] = 0xB3, ["Media_Stop"] = 0xB2,
            ["Volume_Mute"] = 0xAD, ["Volume_Down"] = 0xAE, ["Volume_Up"] = 0xAF,
        };

        private static readonly Dictionary<int, string> VkToName = NameToVk
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => g.First().Key);

        /// <summary>把友好名称解析为 VK 码;无法识别返回 0。</summary>
        public static int GetVk(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            if (NameToVk.TryGetValue(name.Trim(), out int vk)) return vk;
            // 允许 "Numpad1" 之类写法
            if (name.Trim().StartsWith("Num", StringComparison.OrdinalIgnoreCase))
                return GetVk(name.Trim().Substring(3));
            return 0;
        }

        /// <summary>把 VK 码转成友好名称;未知返回 "Key{hex}"。</summary>
        public static string GetName(int vk)
        {
            if (VkToName.TryGetValue(vk, out string? name)) return name;
            return $"Key0x{vk:X2}";
        }

        public static bool IsModifier(int vk) =>
            vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN;

        public static bool IsPrintable(int vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return true; // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return true; // A-Z
            return false;
        }

        /// <summary>获取当前按下的修饰键列表(VK)。</summary>
        public static List<int> GetPressedModifiers()
        {
            var result = new List<int>();
            if (IsKeyDown(VK_SHIFT)) result.Add(VK_SHIFT);
            if (IsKeyDown(VK_CONTROL)) result.Add(VK_CONTROL);
            if (IsKeyDown(VK_MENU)) result.Add(VK_MENU);
            if (IsKeyDown(VK_LWIN)) result.Add(VK_LWIN);
            return result;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
