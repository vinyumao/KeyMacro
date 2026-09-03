using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KeyMacro.Core;
using KeyMacro.Models;
using KeyMacro.Services;
using Microsoft.Win32;
using WMessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace KeyMacro
{
    public partial class MainWindow : Window
    {
        private Macro? _currentMacro;
        private bool _loading;

        // 按键捕获状态
        private enum CaptureMode { None, ToggleKey, TriggerKey, StepKey }
        private CaptureMode _captureMode = CaptureMode.None;

        // 录制步骤按键时保存的键与修饰键
        private string _stepKey = "";
        private List<int> _stepKeyMods = new();

        private WinForms.NotifyIcon? _trayIcon;
        private bool _reallyExit;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosingWindow;

            if (App.Manager != null)
            {
                App.Manager.Hook.KeyEvent += OnHookKeyEvent;
                App.Manager.MacroEnabledChanged += OnMacroEnabledChanged;
            }

            // 初始化"添加动作"面板状态(默认按键类型)
            NewTextPanel.Visibility = Visibility.Collapsed;
            NewKeyPanel.Visibility = Visibility.Visible;
            RefreshStatus();
            LoadMacros();
            LoadGlobalSettings();
        }

        // ================= 窗口行为 =================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 配置了最小化启动则直接隐藏到托盘
            if (App.Config.StartMinimized)
                HideToTray();
        }

        private void OnClosingWindow(object? sender, CancelEventArgs e)
        {
            if (_reallyExit) return;
            e.Cancel = true;
            HideToTray();
        }

        private void HideToTray()
        {
            EnsureTrayIcon();
            ShowInTaskbar = false;
            Hide();
            _trayIcon?.ShowBalloonTip(1200, "键盘宏", "已最小化到托盘,双击图标恢复窗口", WinForms.ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => HideToTray();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点击标题栏内的按钮/开关等交互控件时不触发拖动
            if (e.OriginalSource is DependencyObject src && IsInteractiveElement(src)) return;
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private static bool IsInteractiveElement(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.TextBox
                    or System.Windows.Controls.ComboBox or System.Windows.Controls.CheckBox)
                    return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        // ================= 托盘 =================

        private void EnsureTrayIcon()
        {
            if (_trayIcon != null) return;
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = CreateAppIcon(),
                Text = "键盘宏 - 运行中",
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var toggleItem = new WinForms.ToolStripMenuItem("启用 / 暂停宏");
            toggleItem.Click += (_, _) => ToggleMacrosNow();
            menu.Items.Add(toggleItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _trayIcon.ContextMenuStrip = menu;

            App.Manager!.MacroTriggered += OnMacroTriggeredFromBackground;
        }

        private void OnMacroTriggeredFromBackground()
        {
            // 托盘图标闪烁提示宏触发(在 UI 线程执行)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_trayIcon == null) return;
                _trayIcon.Text = "键盘宏 - 宏已触发";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                t.Tick += (_, _) => { _trayIcon.Text = "键盘宏 - 运行中"; t.Stop(); };
                t.Start();
            }), DispatcherPriority.Background);
        }

        private void ExitApp()
        {
            _reallyExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            System.Windows.Application.Current.Shutdown();
        }

        // ================= 状态 =================

        private void RefreshStatus()
        {
            bool on = App.Config.MacroEnabled;
            StatusText.Text = on ? "运行中" : "已暂停";
            StatusText.Foreground = on
                ? (System.Windows.Media.Brush)FindResource("GreenBrush")
                : (System.Windows.Media.Brush)FindResource("RedBrush");
            BtnToggleNow.Content = on ? "立即暂停" : "立即启用";
            FooterHint.Text = (on ? "宏已启用" : "宏已暂停") + " · 触发键在游戏中全局生效";
            ToggleKeyText.Text = FormatHotkey(App.Config.ToggleKey, App.Config.ToggleModifiers);
        }

        private void ToggleMacrosNow()
        {
            // 统一走管理器,保证暂停时清空排队
            if (App.Manager != null)
                App.Manager.SetEnabled(!App.Config.MacroEnabled);
            else
                App.Config.MacroEnabled = !App.Config.MacroEnabled;
            RefreshStatus();
        }

        // 「应用」按钮:解析输入(0~100,非法还原当前值),写回配置,
        // 并立即对当前已存在的排队裁剪生效(即使之前是 0 也当场生效),之后新触发按新上限判断。
        private void BtnApplyQueueLimit_Click(object sender, RoutedEventArgs e)
        {
            int value = int.TryParse(QueueLimitInput.Text.Trim(), out int v)
                ? Math.Clamp(v, 0, 100)
                : App.Config.MaxQueueSize;
            QueueLimitInput.Text = value.ToString();
            App.Config.MaxQueueSize = value;
            ConfigStore.Save(App.Config);
            App.Manager?.ApplyQueueLimit(value);
        }

        // 失焦时规范化显示(提交由「应用」按钮负责)
        private void QueueLimitInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            QueueLimitInput.Text = (int.TryParse(QueueLimitInput.Text.Trim(), out int v)
                ? Math.Clamp(v, 0, 100)
                : App.Config.MaxQueueSize).ToString();
        }

        // 全局热键(F8)切换时由 MacroManager 触发,刷新界面状态
        private void OnMacroEnabledChanged()
        {
            Dispatcher.BeginInvoke(new Action(RefreshStatus));
        }

        // ================= 全局设置 =================

        private void LoadGlobalSettings()
        {
            AutoStartCheck.IsChecked = App.Config.AutoStart;
            ToggleKeyText.Text = FormatHotkey(App.Config.ToggleKey, App.Config.ToggleModifiers);
            QueueLimitInput.Text = App.Config.MaxQueueSize.ToString();
        }

        private void BtnToggleNow_Click(object sender, RoutedEventArgs e) => ToggleMacrosNow();

        private void BtnToggleCapture_Click(object sender, RoutedEventArgs e) =>
            StartCapture(CaptureMode.ToggleKey, "请按下新的全局开关热键… (Esc 取消)");

        private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Config.AutoStart = AutoStartCheck.IsChecked == true;
            SetAutoStart(App.Config.AutoStart);
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    key.SetValue("KeyMacro", "\"" + exe + "\"");
                }
                else
                {
                    key.DeleteValue("KeyMacro", false);
                }
            }
            catch { /* 忽略注册表异常 */ }
        }

        // ================= 宏列表 =================

        private void LoadMacros()
        {
            _loading = true;
            MacroList.ItemsSource = null;
            MacroList.ItemsSource = App.Config.Macros;
            if (App.Config.Macros.Count > 0)
                MacroList.SelectedIndex = 0;
            _loading = false;

            // _loading 期间 SelectionChanged 被跳过,这里手动同步当前宏与编辑器
            // (否则启动时列表高亮第一项但 _currentMacro 为 null,录制触发键会静默失败)
            _currentMacro = MacroList.SelectedItem as Macro;
            BindMacroToEditor();
        }

        private void BtnAddMacro_Click(object sender, RoutedEventArgs e)
        {
            var macro = new Macro
            {
                Name = "新宏 " + (App.Config.Macros.Count + 1),
                TriggerKey = "",
            };
            App.Config.Macros.Add(macro);
            LoadMacros();
            MacroList.SelectedItem = macro;
        }

        private void MacroDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.FrameworkElement fe || fe.Tag is not Macro macro) return;

            var result = WMessageBox.Show(
                $"确定要删除宏“{macro.Name}”吗?\n删除后不可恢复。",
                "删除宏",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            int removedIndex = App.Config.Macros.IndexOf(macro);
            // 删除前停止该宏可能正在进行的循环
            App.Manager?.StopLoop(macro);
            App.Config.Macros.Remove(macro);
            _currentMacro = null;

            // 重新加载列表;若仍有宏则尽量选中原位置(或前一项),否则清空编辑器
            LoadMacros();
            if (App.Config.Macros.Count > 0)
            {
                int newIndex = Math.Min(removedIndex, App.Config.Macros.Count - 1);
                MacroList.SelectedIndex = newIndex;
                _currentMacro = MacroList.SelectedItem as Macro;
            }
            BindMacroToEditor();
        }

        private void MacroList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _currentMacro = MacroList.SelectedItem as Macro;
            BindMacroToEditor();
        }

        private void BindMacroToEditor()
        {
            var m = _currentMacro;
            if (m == null)
            {
                MacroNameBox.Text = "";
                TriggerKeyText.Text = "未设置";
                MacroEnabledCheck.IsChecked = false;
                LoopEnabledCheck.IsChecked = false;
                LoopIntervalInput.Text = "500";
                StepList.ItemsSource = null;
                return;
            }

            _loading = true;
            MacroNameBox.Text = m.Name;
            MacroEnabledCheck.IsChecked = m.Enabled;
            MacroEnabledLabel.Text = m.Enabled ? "已启用" : "已禁用";
            TriggerKeyText.Text = string.IsNullOrEmpty(m.TriggerKey) ? "未设置" : FormatHotkey(m.TriggerKey, m.TriggerModifiers);
            ModCtrl.IsChecked = m.TriggerModifiers.Contains("Ctrl");
            ModShift.IsChecked = m.TriggerModifiers.Contains("Shift");
            ModAlt.IsChecked = m.TriggerModifiers.Contains("Alt");
            ModWin.IsChecked = m.TriggerModifiers.Contains("Win");
            LoopEnabledCheck.IsChecked = m.LoopEnabled;
            LoopIntervalInput.Text = m.LoopIntervalMs.ToString();
            StepList.ItemsSource = m.Steps;
            _loading = false;
        }

        // ================= 宏编辑 =================

        private void MacroNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading || _currentMacro == null) return;
            _currentMacro.Name = MacroNameBox.Text;
        }

        private void MacroEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _currentMacro == null) return;
            _currentMacro.Enabled = MacroEnabledCheck.IsChecked == true;
            MacroEnabledLabel.Text = _currentMacro.Enabled ? "已启用" : "已禁用";
            // 禁用宏时立即停止其循环
            if (!_currentMacro.Enabled)
                App.Manager?.StopLoop(_currentMacro);
            RefreshMacroListItem(_currentMacro);
        }

        private void LoopEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _currentMacro == null) return;
            _currentMacro.LoopEnabled = LoopEnabledCheck.IsChecked == true;
            // 关闭循环模式时立即停止当前循环
            if (!_currentMacro.LoopEnabled)
                App.Manager?.StopLoop(_currentMacro);
            RefreshMacroListItem(_currentMacro);
        }

        private void LoopIntervalInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading || _currentMacro == null) return;
            _currentMacro.LoopIntervalMs = Math.Max(1, ParseDelay(LoopIntervalInput.Text));
            LoopIntervalInput.Text = _currentMacro.LoopIntervalMs.ToString();
            RefreshMacroListItem(_currentMacro);
        }

        private void Modifiers_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _currentMacro == null) return;
            _currentMacro.TriggerModifiers.Clear();
            if (ModCtrl.IsChecked == true) _currentMacro.TriggerModifiers.Add("Ctrl");
            if (ModShift.IsChecked == true) _currentMacro.TriggerModifiers.Add("Shift");
            if (ModAlt.IsChecked == true) _currentMacro.TriggerModifiers.Add("Alt");
            if (ModWin.IsChecked == true) _currentMacro.TriggerModifiers.Add("Win");
            TriggerKeyText.Text = string.IsNullOrEmpty(_currentMacro.TriggerKey)
                ? "未设置"
                : FormatHotkey(_currentMacro.TriggerKey, _currentMacro.TriggerModifiers);
            RefreshMacroListItem(_currentMacro);
        }

        private void RefreshMacroListItem(Macro m)
        {
            // Macro 实现了 INotifyPropertyChanged,ListTitle 变化会自动刷新列表项
        }

        private void BtnCaptureTrigger_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMacro == null)
            {
                WMessageBox.Show("请先在左侧列表中选择或新建一个宏", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            StartCapture(CaptureMode.TriggerKey, "按下触发键… (Esc 取消)");
        }

        // ================= 动作步骤 =================

        private void BtnAddStep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMacro == null)
            {
                WMessageBox.Show("请先选择或新建一个宏", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int delay = ParseDelay(NewDelayInput.Text);
            MacroStep step;
            switch (NewStepType.SelectedIndex)
            {
                case 1:
                    step = new MacroStep { Type = ActionType.Delay, DelayMs = Math.Max(1, delay) };
                    break;
                case 2:
                    step = new MacroStep { Type = ActionType.Text, Text = NewTextInput.Text, DelayMs = delay };
                    break;
                default:
                    string key = _stepKey;
                    if (string.IsNullOrEmpty(key))
                    {
                        WMessageBox.Show("请先录制或输入按键", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    step = new MacroStep
                    {
                        Type = ActionType.Key,
                        Key = key,
                        Modifiers = _stepKeyMods.Select(KeyMap.GetName).ToList(),
                        DelayMs = delay
                    };
                    _stepKey = "";
                    _stepKeyMods = new List<int>();
                    break;
            }

            _currentMacro.Steps.Add(step);
            StepList.ScrollIntoView(step);
        }

        private static int ParseDelay(string? s) =>
            int.TryParse(s, out int v) ? Math.Max(0, Math.Min(v, 60000)) : 100;

        private void StepUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMacro == null || sender is not System.Windows.FrameworkElement fe || fe.Tag is not MacroStep step) return;
            int i = _currentMacro.Steps.IndexOf(step);
            if (i <= 0) return;
            _currentMacro.Steps.Move(i, i - 1);
        }

        private void StepDown_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMacro == null || sender is not System.Windows.FrameworkElement fe || fe.Tag is not MacroStep step) return;
            int i = _currentMacro.Steps.IndexOf(step);
            if (i < 0 || i >= _currentMacro.Steps.Count - 1) return;
            _currentMacro.Steps.Move(i, i + 1);
        }

        private void StepDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMacro == null || sender is not System.Windows.FrameworkElement fe || fe.Tag is not MacroStep step) return;
            _currentMacro.Steps.Remove(step);
        }

        private void NewStepType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // XAML 加载早期 SelectedIndex=0 会触发本事件,此时面板可能尚未创建
            if (NewTextPanel == null || NewKeyPanel == null) return;
            bool isText = NewStepType.SelectedIndex == 2;
            NewTextPanel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
            NewKeyPanel.Visibility = isText ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================= 按键捕获 =================

        private void StartCapture(CaptureMode mode, string hint)
        {
            _captureMode = mode;
            if (App.Manager != null) App.Manager.IsCapturing = true;
            CaptureHint.Text = hint;
            CaptureHint.Visibility = Visibility.Visible;

            if (mode == CaptureMode.TriggerKey)
            {
                BtnCaptureTrigger.Content = "等待按键…";
                BtnCaptureTrigger.IsEnabled = false;
            }
            else if (mode == CaptureMode.StepKey)
            {
                BtnCaptureStepKey.Content = "等待按键…";
                BtnCaptureStepKey.IsEnabled = false;
            }
            else
            {
                BtnToggleCapture.Content = "等待按键…";
                BtnToggleCapture.IsEnabled = false;
            }
        }

        private void EndCapture()
        {
            _captureMode = CaptureMode.None;
            if (App.Manager != null) App.Manager.IsCapturing = false;
            CaptureHint.Visibility = Visibility.Collapsed;
            BtnCaptureTrigger.Content = "录制触发键";
            BtnCaptureTrigger.IsEnabled = true;
            BtnCaptureStepKey.Content = "录制";
            BtnCaptureStepKey.IsEnabled = true;
            BtnToggleCapture.Content = "录制";
            BtnToggleCapture.IsEnabled = true;
        }

        private void OnHookKeyEvent(int vk, bool isDown)
        {
            if (_captureMode == CaptureMode.None || !isDown) return;

            // Esc 取消
            if (vk == KeyMap.GetVk("Esc"))
            {
                Dispatcher.BeginInvoke(new Action(EndCapture));
                return;
            }

            // 只接受非修饰键作为主键
            if (KeyMap.IsModifier(vk)) return;

            int key = vk;
            var mods = KeyMap.GetPressedModifiers();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (_captureMode)
                {
                    case CaptureMode.ToggleKey:
                        App.Config.ToggleKey = KeyMap.GetName(key);
                        App.Config.ToggleModifiers = ModsToStrings(mods);
                        RefreshStatus();
                        break;
                    case CaptureMode.TriggerKey:
                        if (_currentMacro != null)
                        {
                            _currentMacro.TriggerKey = KeyMap.GetName(key);
                            _currentMacro.TriggerModifiers.Clear();
                            _currentMacro.TriggerModifiers.AddRange(ModsToStrings(mods));
                            TriggerKeyText.Text = FormatHotkey(_currentMacro.TriggerKey, _currentMacro.TriggerModifiers);
                            ModCtrl.IsChecked = _currentMacro.TriggerModifiers.Contains("Ctrl");
                            ModShift.IsChecked = _currentMacro.TriggerModifiers.Contains("Shift");
                            ModAlt.IsChecked = _currentMacro.TriggerModifiers.Contains("Alt");
                            ModWin.IsChecked = _currentMacro.TriggerModifiers.Contains("Win");
                            RefreshMacroListItem(_currentMacro);
                        }
                        break;
                    case CaptureMode.StepKey:
                        _stepKey = KeyMap.GetName(key);
                        _stepKeyMods = new List<int>(mods);
                        NewKeyText.Text = FormatStepKey(_stepKey, mods);
                        break;
                }
                EndCapture();
            }));
        }

        private static List<string> ModsToStrings(List<int> mods)
        {
            var list = new List<string>();
            foreach (int vk in mods)
            {
                string n = KeyMap.GetName(vk);
                if (!list.Contains(n)) list.Add(n);
            }
            return list;
        }

        private static string FormatStepKey(string key, List<int> mods)
        {
            var parts = mods.Select(KeyMap.GetName).ToList();
            parts.Add(key);
            return string.Join("+", parts);
        }

        private static string FormatHotkey(string key, List<string> mods)
        {
            var parts = new List<string>(mods);
            if (!string.IsNullOrEmpty(key)) parts.Add(key);
            return parts.Count == 0 ? "未设置" : string.Join("+", parts);
        }

        private void BtnCaptureStepKey_Click(object sender, RoutedEventArgs e) =>
            StartCapture(CaptureMode.StepKey, "按下按键… (Esc 取消)");

        // ================= 图标 =================

        private static System.Drawing.Icon CreateAppIcon()
        {
            // 优先从嵌入资源加载 app.ico(与窗口/exe 图标一致)
            try
            {
                var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info != null)
                {
                    using (info.Stream)
                    {
                        var resIcon = new System.Drawing.Icon(info.Stream);
                        return (System.Drawing.Icon)resIcon.Clone();
                    }
                }
            }
            catch { /* 资源加载失败时退回动态绘制 */ }

            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                // 圆角方块背景
                using var path = RoundedRect(0, 0, 32, 32, 7);
                using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32), Color.FromArgb(79, 140, 255), Color.FromArgb(58, 111, 216), 45f);
                g.FillPath(bg, path);
                // 白色键帽图形
                using var white = new SolidBrush(Color.White);
                using var pen = new Pen(Color.White, 2.2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.FillEllipse(white, 9, 8, 5, 5);
                g.DrawLine(pen, 16, 8, 21, 8);
                g.DrawLine(pen, 16, 15, 21, 15);
                g.DrawLine(pen, 16, 22, 21, 22);
                g.DrawLine(pen, 13, 12, 9, 12);
                g.DrawLine(pen, 13, 19, 9, 19);
            }
            IntPtr hIcon = bmp.GetHicon();
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            // 拷贝一份,避免句柄释放后失效
            var copy = (System.Drawing.Icon)icon.Clone();
            DestroyIcon(hIcon);
            return copy;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
