using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyMacro.Models
{
    /// <summary>动作类型。</summary>
    public enum ActionType
    {
        /// <summary>按键组合</summary>
        Key,
        /// <summary>输入文本</summary>
        Text,
        /// <summary>延时等待</summary>
        Delay,
    }

    /// <summary>宏动作序列中的一个步骤。</summary>
    public class MacroStep : INotifyPropertyChanged
    {
        private ActionType _type = ActionType.Key;
        private string _key = "";
        private List<string> _modifiers = new();
        private string _text = "";
        private int _delayMs = 100;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            // 展示辅助属性联动刷新
            if (name is nameof(Type) or nameof(Key) or nameof(Modifiers) or nameof(Text) or nameof(DelayMs))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StepTypeIcon)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StepTypeBg)));
            }
        }

        public ActionType Type
        {
            get => _type;
            set { if (_type != value) { _type = value; Notify(); } }
        }

        /// <summary>主键友好名称(Key 类型)。</summary>
        public string Key
        {
            get => _key;
            set { if (_key != value) { _key = value; Notify(); } }
        }

        /// <summary>修饰键友好名称列表,如 ["Ctrl","Shift"]。</summary>
        public List<string> Modifiers
        {
            get => _modifiers;
            set { _modifiers = value; Notify(); }
        }

        /// <summary>文本内容(Text 类型)。</summary>
        public string Text
        {
            get => _text;
            set { if (_text != value) { _text = value; Notify(); } }
        }

        /// <summary>本步骤执行后的延时毫秒;Delay 类型时表示等待时长。</summary>
        public int DelayMs
        {
            get => _delayMs;
            set { if (_delayMs != value) { _delayMs = value; Notify(); } }
        }

        // ---- 界面展示辅助(不参与序列化) ----
        [JsonIgnore]
        public string StepTypeIcon => Type switch
        {
            ActionType.Key => "键",
            ActionType.Text => "文",
            _ => "时"
        };

        [JsonIgnore]
        public string StepTypeBg => Type switch
        {
            ActionType.Key => "#4F8CFF",
            ActionType.Text => "#2FBF71",
            _ => "#F0A83A"
        };

        [JsonIgnore]
        public string Description
        {
            get
            {
                switch (Type)
                {
                    case ActionType.Key:
                        var parts = new List<string>(Modifiers);
                        if (!string.IsNullOrEmpty(Key)) parts.Add(Key);
                        return string.Join("+", parts);
                    case ActionType.Text:
                        return "输入文本: " + Text;
                    default:
                        return "等待 " + DelayMs + "ms";
                }
            }
        }

        public MacroStep Clone() => new()
        {
            Type = Type,
            Key = Key,
            Modifiers = new List<string>(Modifiers),
            Text = Text,
            DelayMs = DelayMs
        };
    }

    /// <summary>单个宏:触发键 + 有序动作序列。</summary>
    public class Macro : INotifyPropertyChanged
    {
        private string _name = "新宏";
        private string _triggerKey = "";
        private List<string> _triggerModifiers = new();
        private bool _enabled = true;
        private bool _loopEnabled;
        private int _loopIntervalMs = 500;
        private bool _isLooping;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name is nameof(Name) or nameof(TriggerKey) or nameof(TriggerModifiers) or nameof(Enabled)
                or nameof(LoopEnabled) or nameof(LoopIntervalMs) or nameof(IsLooping))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListTitle)));
        }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Notify(); } }
        }

        /// <summary>触发键友好名称,如 "F9" 或 "Num5"。</summary>
        public string TriggerKey
        {
            get => _triggerKey;
            set { if (_triggerKey != value) { _triggerKey = value; Notify(); } }
        }

        /// <summary>触发时需同时按下的修饰键。</summary>
        public List<string> TriggerModifiers
        {
            get => _triggerModifiers;
            set { _triggerModifiers = value; Notify(); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled != value) { _enabled = value; Notify(); } }
        }

        /// <summary>是否开启循环模式。
        /// 开启后,触发键按下一次即开始循环执行动作序列,再按一次同一触发键停止。
        /// 仅当触发键与动作键相同等场景下,动作合成键不会打断/再次触发触发键功能。</summary>
        public bool LoopEnabled
        {
            get => _loopEnabled;
            set { if (_loopEnabled != value) { _loopEnabled = value; Notify(); } }
        }

        /// <summary>循环间隔(毫秒):每执行完一遍动作序列后等待该时长再执行下一遍。</summary>
        public int LoopIntervalMs
        {
            get => _loopIntervalMs;
            set { if (_loopIntervalMs != value) { _loopIntervalMs = value; Notify(); } }
        }

        /// <summary>当前是否处于循环执行中(仅运行时状态,不参与序列化)。</summary>
        [JsonIgnore]
        public bool IsLooping
        {
            get => _isLooping;
            set { if (_isLooping != value) { _isLooping = value; Notify(); } }
        }

        /// <summary>动作步骤,按顺序执行。</summary>
        public ObservableCollection<MacroStep> Steps { get; set; } = new();

        [JsonIgnore]
        public string ListTitle
        {
            get
            {
                var parts = new List<string>(TriggerModifiers);
                if (!string.IsNullOrEmpty(TriggerKey)) parts.Add(TriggerKey);
                string hotkey = parts.Count == 0 ? "未设置触发键" : string.Join("+", parts);
                string loopTag = LoopEnabled ? $"  [循环 {LoopIntervalMs}ms]" : "";
                string loopingTag = IsLooping ? " · 循环中" : "";
                return (Enabled ? "● " : "○ ") + Name + "  —  " + hotkey + loopTag + loopingTag;
            }
        }

        public Macro Clone() => new()
        {
            Name = Name,
            TriggerKey = TriggerKey,
            TriggerModifiers = new List<string>(TriggerModifiers),
            Enabled = Enabled,
            LoopEnabled = LoopEnabled,
            LoopIntervalMs = LoopIntervalMs,
            Steps = new ObservableCollection<MacroStep>(Steps)
        };
    }

    /// <summary>应用级配置。</summary>
    public class AppConfig
    {
        /// <summary>全局启停热键的主键,如 "F8"。</summary>
        public string ToggleKey { get; set; } = "F8";

        /// <summary>全局启停热键的修饰键。</summary>
        public List<string> ToggleModifiers { get; set; } = new();

        /// <summary>宏功能是否启用(可通过全局热键切换)。</summary>
        public bool MacroEnabled { get; set; } = true;

        /// <summary>启动时是否最小化到托盘。</summary>
        public bool StartMinimized { get; set; } = false;

        /// <summary>是否开机自启。</summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>宏触发排队上限(0=不限制)。
        /// 连按触发键时,已排队(锁定执行、尚未开始)的宏数量达到该值后,新触发被丢弃(触发键仍被吞掉)。
        /// 正在执行的宏不计入排队。</summary>
        public int MaxQueueSize { get; set; } = 0;

        public List<Macro> Macros { get; set; } = new();
    }
}
