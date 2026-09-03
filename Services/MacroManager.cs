using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KeyMacro.Core;
using KeyMacro.Models;

namespace KeyMacro.Services
{
    /// <summary>
    /// 宏调度核心:
    /// - 监听全局键盘钩子 + GetAsyncKeyState 轮询(双通道)
    /// - 触发键按下(含修饰键组合)时,吞掉该键并顺序执行动作序列
    /// - 支持全局启停热键
    /// - 防止按住连发与合成事件自触发
    /// 说明:DirectInput 独占键盘的游戏(如恐怖黎明)绕过低级钩子,
    ///      此时由轮询通道检测触发键。
    /// </summary>
    public class MacroManager : IDisposable
    {
        private readonly GlobalKeyboardHook _hook = new();
        private readonly AppConfig _config;
        private readonly object _lock = new();

        // 已触发的按键(处于"按住且被吞"状态):重复按下直接吞掉,keyup 时清除
        private readonly HashSet<int> _activeTriggers = new();

        // 本程序宏最近输出的按键(vk -> 输出时刻),用于识别合成键防止自触发。
        // 不依赖 LLKHF_INJECTED 标志:很多键盘驱动/输入法注入的按键也带该标志,
        // 用标志区分会把真实按键误过滤导致全局失效。
        private readonly Dictionary<int, long> _selfSentKeys = new();
        private const long SelfSentWindowMs = 150;

        // 宏执行串行化:同一时刻只执行一个宏,避免并行 SendInput 乱序
        private readonly SemaphoreSlim _execLock = new(1, 1);

        // 排队中的宏作业(按加入顺序,最前的最先执行)。受 MaxQueueSize 上限约束(0=不限制):
        // - 触发时排队已满 → 丢弃新触发
        // - 应用新上限(ApplyQueueLimit)时 → 立即裁剪,丢弃超出的部分
        // - 暂停(全局开关关闭)时 → 全部作废
        // 正在执行的宏不计入排队。每个作业自带取消令牌,可单独作废。
        private readonly List<QueuedMacro> _pending = new();

        private sealed class QueuedMacro
        {
            public Macro Macro = null!;
            public CancellationTokenSource Cts = new();
        }

        // 循环模式:正在循环执行的宏(macro -> 作业)。按触发键切换开关。
        // 循环任务持有 _execLock 进行每一轮执行,轮隙之间释放,允许其它宏插入。
        private sealed class ActiveLoop
        {
            public Macro Macro = null!;
            public CancellationTokenSource Cts = new();
        }
        private readonly Dictionary<Macro, ActiveLoop> _loops = new();

        // 触发去重:同一按键在短时间内只触发一次(钩子与轮询共用,避免双触发)
        private readonly Dictionary<int, long> _lastTrigger = new();
        private const long TriggerDedupMs = 120;

        // 轮询通道(应对 DirectInput 独占游戏):记录每个检测键的上一轮按下状态
        private readonly Dictionary<int, bool> _pollPrevDown = new();
        private CancellationTokenSource? _pollCts;

        // 界面正在"捕获按键"时置位:暂停宏触发,但事件照常上报
        public volatile bool IsCapturing;

        public event Action? MacroTriggered;

        /// <summary>宏全局启用状态变化(界面据此刷新)。</summary>
        public event Action? MacroEnabledChanged;

        /// <summary>底层钩子(界面录制按键时订阅)。</summary>
        public GlobalKeyboardHook Hook => _hook;

        public MacroManager(AppConfig config) => _config = config;

        public void Start()
        {
            _hook.ShouldSuppress = ShouldSuppress;
            _hook.Install();
            StartPolling();
        }

        // ================= 轮询通道 (GetAsyncKeyState) =================

        private void StartPolling()
        {
            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoop(_pollCts.Token));
        }

        private async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { PollOnce(); }
                catch { /* 轮询异常忽略 */ }
                await Task.Delay(15, ct);
            }
        }

        private void PollOnce()
        {
            if (IsCapturing) return;

            // 全局开关热键
            int toggleVk = KeyMap.GetVk(_config.ToggleKey);
            CheckPollKey(toggleVk, _config.ToggleModifiers, isToggle: true, macro: null);

            // 各启用宏的触发键
            if (_config.MacroEnabled)
            {
                foreach (var macro in _config.Macros)
                {
                    if (!macro.Enabled) continue;
                    int vk = KeyMap.GetVk(macro.TriggerKey);
                    if (vk == 0) continue;
                    CheckPollKey(vk, macro.TriggerModifiers, isToggle: false, macro);
                }
            }
        }

        private void CheckPollKey(int vk, List<string> mods, bool isToggle, Macro? macro)
        {
            if (vk == 0) return;
            bool down = IsKeyDown(vk);
            bool prev = _pollPrevDown.TryGetValue(vk, out bool p) && p;
            _pollPrevDown[vk] = down;
            if (!down || prev) return; // 仅在"松开→按下"边沿触发,按住不连发

            // 本程序刚输出的合成键:跳过,防止自触发
            if (IsSelfSentRecently(vk)) return;

            // 修饰键组合必须匹配
            if (!ModifiersMatch(mods)) return;

            // 与钩子通道去重
            if (!TryClaimTrigger(vk)) return;

            Core.GlobalKeyboardHook.Log($"  POLL {(isToggle ? "TOGGLE" : "TRIGGER")} vk={vk}");

            if (isToggle)
                ToggleEnabled();
            else if (macro != null)
                TriggerMacro(macro);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // ================= 钩子通道 =================

        /// <summary>钩子回调:决定是否吞掉该键。逻辑快速,不阻塞钩子线程。</summary>
        private bool ShouldSuppress(int vk, bool isDown, long extraInfo)
        {
            if (IsCapturing)
                return false;

            // 本程序通过 SendInput 合成的按键:dwExtraInfo 带标记,可精确识别。
            // 一律不吞、不触发、不改变"按住触发键"状态 → 动作键与触发键相同时,
            // 合成键既不会再次触发触发键功能,也不会打断触发键的循环。
            if (extraInfo == KeySender.SyntheticExtraInfo)
                return false;

            // keyup 无条件清除触发状态(宏执行中也要清除,否则下次触发会被误吞)
            if (!isDown)
            {
                lock (_lock)
                {
                    _activeTriggers.Remove(vk);
                }
                return false;
            }

            lock (_lock)
            {
                // 已处于"触发并按住"状态:重复 keydown 一律吞掉,不重复触发
                if (_activeTriggers.Contains(vk))
                    return true;

                // 全局启停热键(单独判定,允许热键与宏触发键相同,取热键优先)
                if (vk == KeyMap.GetVk(_config.ToggleKey) && ModifiersMatch(_config.ToggleModifiers))
                {
                    _activeTriggers.Add(vk);
                    if (!TryClaimTrigger(vk))
                        return true;
                    Core.GlobalKeyboardHook.Log($"  TOGGLE vk={vk}");
                    ToggleEnabled();
                    return true;
                }

                // 宏触发判定
                if (_config.MacroEnabled)
                {
                    foreach (var macro in _config.Macros)
                    {
                        if (!macro.Enabled) continue;
                        if (vk == KeyMap.GetVk(macro.TriggerKey) && ModifiersMatch(macro.TriggerModifiers))
                        {
                            _activeTriggers.Add(vk);
                            if (!TryClaimTrigger(vk))
                                return true;
                            Core.GlobalKeyboardHook.Log($"  TRIGGER vk={vk} macro={macro.Name}");
                            TriggerMacro(macro);
                            return true; // 吞掉触发键,不传给游戏/应用
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>触发去重:窗口期内已触发过则返回 false。钩子与轮询共用。</summary>
        private bool TryClaimTrigger(int vk)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                if (_lastTrigger.TryGetValue(vk, out long last) && now - last < TriggerDedupMs)
                    return false;
                _lastTrigger[vk] = now;
                return true;
            }
        }

        /// <summary>记录本程序输出的按键(供防自触发判断)。</summary>
        private void RecordSelfSent(int vk)
        {
            if (vk == 0) return;
            lock (_lock)
            {
                _selfSentKeys[vk] = Environment.TickCount64;
            }
        }

        /// <summary>该键是否为本程序最近输出的合成键。
        /// 窗口期内其"按下"和"抬起"事件都视为合成键(不消耗记录):
        /// 若按下时消耗、抬起时无记录,合成键的抬起会误清"按住触发键"状态,
        /// 导致按住触发键时因自动重复而无限重新触发(触发键恰好也在宏输出里时必现)。</summary>
        private bool IsSelfSentRecently(int vk)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                if (_selfSentKeys.TryGetValue(vk, out long t))
                {
                    if (now - t <= SelfSentWindowMs)
                        return true;
                    _selfSentKeys.Remove(vk); // 过期条目清理,避免无限增长
                }
                return false;
            }
        }

        /// <summary>校验当前修饰键按下状态是否与要求一致。</summary>
        private bool ModifiersMatch(List<string> required)
        {
            var held = KeyMap.GetPressedModifiers();
            // 要求按住的所有修饰键必须按下
            foreach (var name in required)
            {
                int vk = KeyMap.GetVk(name);
                if (vk == 0 || !held.Contains(vk)) return false;
            }
            // 额外的修饰键不应被按下(要求严格匹配,避免误触)
            foreach (var hvk in held)
            {
                bool requiredByName = required.Any(r => KeyMap.GetVk(r) == hvk);
                if (!requiredByName) return false;
            }
            return true;
        }

        private void ToggleEnabled() => SetEnabled(!_config.MacroEnabled);

        /// <summary>设置宏全局启用状态(热键/界面按钮/托盘菜单共用)。
        /// 关闭时清空排队中尚未执行的宏(正在执行的跑完即止),并停止所有循环。</summary>
        public void SetEnabled(bool enabled)
        {
            bool changed = _config.MacroEnabled != enabled;
            _config.MacroEnabled = enabled;
            if (!enabled)
            {
                ClearPendingQueue();
                StopAllLoops();
            }
            if (changed)
                MacroEnabledChanged?.Invoke();
        }

        /// <summary>清空排队中的宏:作废所有尚未开始执行的任务。</summary>
        private void ClearPendingQueue()
        {
            List<QueuedMacro> toCancel;
            lock (_lock)
            {
                toCancel = new List<QueuedMacro>(_pending);
                _pending.Clear();
            }
            foreach (var job in toCancel)
            {
                Core.GlobalKeyboardHook.Log("  QUEUE CLEARED: cancel " + job.Macro.Name);
                job.Cts.Cancel();
            }
        }

        /// <summary>应用新的排队上限并立即生效(界面「应用」按钮调用):
        /// 写回配置后,当前已排队的宏立即裁剪到新上限 —— 保留最早排队的,丢弃超出的;
        /// 上限为 0 表示不限制,无需裁剪。此后新触发按新上限判断。</summary>
        public void ApplyQueueLimit(int maxQueueSize)
        {
            _config.MaxQueueSize = Math.Max(0, maxQueueSize);

            if (_config.MaxQueueSize <= 0) return;

            List<QueuedMacro> toCancel;
            lock (_lock)
            {
                if (_pending.Count <= _config.MaxQueueSize) return;
                toCancel = _pending.Skip(_config.MaxQueueSize).ToList();
            }
            foreach (var job in toCancel)
            {
                Core.GlobalKeyboardHook.Log($"  LIMIT APPLIED cap={_config.MaxQueueSize}: cancel queued macro={job.Macro.Name}");
                job.Cts.Cancel();
            }
        }

        private void TriggerMacro(Macro macro)
        {
            // 循环模式:按触发键切换循环开关(不进入排队,独立运行)
            if (macro.LoopEnabled && macro.Steps.Count > 0)
            {
                ToggleLoop(macro);
                MacroTriggered?.Invoke();
                return;
            }

            QueuedMacro job;
            lock (_lock)
            {
                int cap = _config.MaxQueueSize;
                if (cap > 0 && _pending.Count >= cap)
                {
                    // 队列已满:丢弃本次触发(触发键已被吞掉,不再排队执行)
                    Core.GlobalKeyboardHook.Log($"  QUEUE FULL: drop macro={macro.Name} (pending={_pending.Count}, cap={cap})");
                    return;
                }
                job = new QueuedMacro { Macro = macro };
                _pending.Add(job);
            }

            MacroTriggered?.Invoke();
            _ = ExecuteAsync(job);
        }

        /// <summary>顺序执行宏步骤。同一时刻只执行一个宏,连按触发依次排队;
        /// 排队数量超过 MaxQueueSize 时新触发被丢弃;暂停或应用上限时排队被作废。</summary>
        private async Task ExecuteAsync(QueuedMacro job)
        {
            try
            {
                await _execLock.WaitAsync(job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 暂停清队/上限裁剪:本任务作废,不再执行
                RemovePending(job);
                return;
            }

            // 已获得执行权(开始执行),不再计入排队
            RemovePending(job);

            try
            {
                await ExecuteSteps(job.Macro, job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 执行中途被取消(暂停/上限裁剪):静默退出
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("执行宏失败: " + ex.Message);
            }
            finally
            {
                _execLock.Release();
            }
        }

        /// <summary>顺序执行一个宏的完整动作序列(支持取消)。步骤内的延时同样可被取消。</summary>
        private async Task ExecuteSteps(Macro macro, CancellationToken ct)
        {
            foreach (var step in macro.Steps)
            {
                ct.ThrowIfCancellationRequested();
                switch (step.Type)
                {
                    case ActionType.Key:
                        SendKeyStep(step);
                        break;
                    case ActionType.Text:
                        // 文本中可映射为按键的字符(数字/字母等)同样标记为合成键,
                        // 防止文本回喂触发(如触发键 "1" + 文本 "14567")
                        foreach (char c in step.Text)
                        {
                            int cvk = KeyMap.GetVk(c.ToString());
                            if (cvk != 0) RecordSelfSent(cvk);
                        }
                        KeySender.SendUnicodeText(step.Text);
                        break;
                    case ActionType.Delay:
                        break; // 延时在下方统一处理
                }
                // 步骤后延时
                if (step.DelayMs > 0)
                    await Task.Delay(Math.Min(step.DelayMs, 60000), ct);
            }
        }

        // ================= 循环模式 =================

        /// <summary>切换指定宏的循环开关:未循环则启动,循环中则停止。</summary>
        private void ToggleLoop(Macro macro)
        {
            lock (_lock)
            {
                if (_loops.TryGetValue(macro, out var existing))
                {
                    // 已在循环:停止
                    _loops.Remove(macro);
                    existing.Macro.IsLooping = false;
                    existing.Cts.Cancel();
                    Core.GlobalKeyboardHook.Log($"  LOOP STOP macro={macro.Name}");
                    return;
                }

                // 启动。用 Task.Run 让循环任务在后台线程执行,
                // 避免首轮动作(含 SendInput+Sleep)阻塞钩子线程。
                var cts = new CancellationTokenSource();
                var loop = new ActiveLoop { Macro = macro, Cts = cts };
                _loops[macro] = loop;
                loop.Macro.IsLooping = true;
                Core.GlobalKeyboardHook.Log($"  LOOP START macro={macro.Name} interval={macro.LoopIntervalMs}ms");
                _ = Task.Run(() => RunLoopAsync(loop, cts.Token));
            }
        }

        /// <summary>循环执行宏:每轮执行一遍动作序列,完成后等待 LoopIntervalMs 再进入下一轮。
        /// 轮隙之间释放 _execLock,允许其它宏插入;循环启动/停止由触发键控制。</summary>
        private async Task RunLoopAsync(ActiveLoop loop, CancellationToken ct)
        {
            var macro = loop.Macro;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        await _execLock.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    try
                    {
                        await ExecuteSteps(macro, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("循环执行失败: " + ex.Message);
                    }
                    finally
                    {
                        _execLock.Release();
                    }

                    // 每轮完成后等待间隔,可被取消
                    if (macro.LoopIntervalMs > 0)
                    {
                        try { await Task.Delay(Math.Min(macro.LoopIntervalMs, 60000), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (_loops.TryGetValue(macro, out var cur))
                    {
                        // 字典中仍存在该宏:若还是本作业则移除并清标记;
                        // 若已换成新的循环作业,则不打扰新作业的标记。
                        if (ReferenceEquals(cur, loop))
                        {
                            _loops.Remove(macro);
                            loop.Macro.IsLooping = false;
                        }
                    }
                    else
                    {
                        // 已被外部停止(触发切换/暂停/删除):清运行标记
                        loop.Macro.IsLooping = false;
                    }
                }
                loop.Cts.Dispose();
            }
        }

        /// <summary>停止指定宏的循环(外部调用:宏被禁用/删除时)。</summary>
        public void StopLoop(Macro macro)
        {
            ActiveLoop? loop;
            lock (_lock)
            {
                if (!_loops.TryGetValue(macro, out loop)) return;
                _loops.Remove(macro);
            }
            loop!.Macro.IsLooping = false;
            loop.Cts.Cancel();
            Core.GlobalKeyboardHook.Log($"  LOOP STOP (external) macro={macro.Name}");
        }

        /// <summary>停止所有正在循环的宏(暂停/退出时调用)。</summary>
        private void StopAllLoops()
        {
            List<ActiveLoop> all;
            lock (_lock)
            {
                all = new List<ActiveLoop>(_loops.Values);
                _loops.Clear();
            }
            foreach (var loop in all)
            {
                loop.Macro.IsLooping = false;
                loop.Cts.Cancel();
            }
        }

        private void RemovePending(QueuedMacro job)
        {
            lock (_lock)
            {
                _pending.Remove(job);
            }
            job.Cts.Dispose();
        }

        private void SendKeyStep(MacroStep step)
        {
            int mainVk = KeyMap.GetVk(step.Key);
            if (mainVk == 0) return;

            var mods = new List<int>();
            foreach (var m in step.Modifiers)
            {
                int vk = KeyMap.GetVk(m);
                if (vk != 0 && !mods.Contains(vk)) mods.Add(vk);
            }

            // 记录本程序将输出的按键,钩子回调据此识别合成键
            RecordSelfSent(mainVk);
            foreach (var m in mods) RecordSelfSent(m);

            KeySender.SendCombo(mods, mainVk);
        }

        public void Dispose()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            ClearPendingQueue();
            StopAllLoops();
            _hook.Dispose();
        }
    }
}
