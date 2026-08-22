using System;
using System.Windows;
using System.Windows.Threading;
using KeyMacro.Models;
using KeyMacro.Services;
using WMessageBox = System.Windows.MessageBox;

namespace KeyMacro
{
    public partial class App : System.Windows.Application
    {
        public static AppConfig Config = new();
        public static MacroManager? Manager;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 管理员自提升:游戏常以管理员身份运行,普通权限的键盘钩子会被
            // Windows UIPI 隔离(收不到游戏按键、注入被拒)。非管理员启动时
            // 自动请求提权重启;用户拒绝 UAC 则降级为普通权限继续运行。
            if (!IsAdministrator() && !e.Args.Contains("--elevated"))
            {
                try
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (exe.Length > 0)
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exe,
                            UseShellExecute = true,
                            Verb = "runas",
                            Arguments = "--elevated"
                        };
                        System.Diagnostics.Process.Start(psi);
                        Shutdown();
                        return;
                    }
                }
                catch { /* 用户拒绝 UAC:以普通权限继续运行 */ }
            }

            // 崩溃兜底:避免钩子异常导致整个进程退出无提示
            DispatcherUnhandledException += (_, args) =>
            {
                WMessageBox.Show("发生未处理的异常:\n" + args.Exception.Message,
                    "键盘宏", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            try
            {
                Config = ConfigStore.Load();
                Manager = new MacroManager(Config);
                Manager.Start();

                var win = new MainWindow();
                win.Show();
            }
            catch (Exception ex)
            {
                WMessageBox.Show("启动失败:" + ex.Message, "键盘宏", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Manager?.Dispose();
            ConfigStore.Save(Config);
            base.OnExit(e);
        }
    }
}
