using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyMacro.Core
{
    /// <summary>
    /// 全局低级键盘钩子 (WH_KEYBOARD_LL)。
    /// 注入到系统级,游戏(非独占全屏)内同样生效。
    /// </summary>
    public sealed class GlobalKeyboardHook : IDisposable
    {
        public const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        private LowLevelKeyboardProc? _proc;
        private IntPtr _hookId = IntPtr.Zero;

        /// <summary>按键事件:参数为虚拟键码与是否按下(按下=true)。</summary>
        public event Action<int, bool>? KeyEvent;

        /// <summary>是否应吞掉(阻止)该按键:返回 true 表示吞掉。
        /// 参数:虚拟键码、是否按下、dwExtraInfo(用于识别本程序合成的按键)。</summary>
        public Func<int, bool, long, bool>? ShouldSuppress;

        public bool IsInstalled => _hookId != IntPtr.Zero;

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;

            _proc = HookCallback;
            // WH_KEYBOARD_LL 是进程内回调,hMod 传 IntPtr.Zero 即可
            // (单文件发布下 Process.MainModule 可能为 null,不能依赖它)
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hookId == IntPtr.Zero)
                throw new InvalidOperationException("安装全局键盘钩子失败: " + Marshal.GetLastWin32Error());

            Log("HOOK INSTALLED OK, id=" + _hookId);
        }

        private static readonly object LogLock = new();
        private static string? _logPath;

        /// <summary>诊断日志(钩子诊断用,记录到 exe 同目录 hook_diag.log)。</summary>
        internal static void Log(string msg)
        {
            try
            {
                lock (LogLock)
                {
                    if (_logPath == null)
                    {
                        string dir = AppContext.BaseDirectory;
                        _logPath = System.IO.Path.Combine(dir, "hook_diag.log");
                        // 每次启动重写,避免无限累积
                        System.IO.File.WriteAllText(_logPath, "=== KeyMacro hook diag ===\r\n");
                    }
                    System.IO.File.AppendAllText(_logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
                }
            }
            catch { /* 日志失败不影响主功能 */ }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    int vk = (int)data.vkCode;

                    // 先判定是否吞键,再上报事件:
                    // - 吞键时(宏触发/开关)不再上报,避免"已按下集合"污染触发判定
                    // - 未吞键(如录制捕获)则照常上报
                    // - dwExtraInfo 用于识别本程序合成的按键(不吞、不上报,防止自触发)
                    bool suppress = ShouldSuppress?.Invoke(vk, isDown, data.dwExtraInfo.ToInt64()) ?? false;
                    if (suppress)
                    {
                        Log($"  -> SUPPRESSED vk={vk} down={isDown}");
                        return new IntPtr(1); // 吞掉该按键,不向下传递
                    }

                    KeyEvent?.Invoke(vk, isDown);
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Uninstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _proc = null;
            }
        }

        public void Dispose() => Uninstall();
    }
}
