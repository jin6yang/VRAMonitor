using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.ApplicationModel.Resources;
using Windows.System;

namespace VRAMonitor.Views.Dialogs
{
    public sealed partial class ShortcutEditorDialog : ContentDialog, INotifyPropertyChanged
    {
        // UI 绑定的按键文本列表
        public ObservableCollection<string> CurrentKeys { get; } = new ObservableCollection<string>();

        // 仅当列表为空时显示占位符
        public bool IsWaitingForInput => CurrentKeys.Count == 0;

        // 控制错误提示条的显示
        private bool _isErrorVisible;
        public bool IsErrorVisible
        {
            get => _isErrorVisible;
            set
            {
                if (_isErrorVisible != value)
                {
                    _isErrorVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        // 最终结果
        public VirtualKeyModifiers ResultModifiers { get; private set; }
        public VirtualKey ResultKey { get; private set; }

        // 默认值，用于“重置”功能 (Reset to Defaults)
        private readonly VirtualKeyModifiers _defaultModifiers;
        private readonly VirtualKey _defaultKey;

        // [新增] 本地化文本属性供 XAML 绑定 (x:Bind 默认为 OneTime，这里足够了)
        public string TextInstruction { get; private set; }
        public string TextWaiting { get; private set; }
        public string TextError { get; private set; }
        public string TextReset { get; private set; }
        public string TextResetToolTip { get; private set; }
        public string TextClear { get; private set; }
        public string TextClearToolTip { get; private set; }
        public string TextDescription { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="currentModifiers">当前修饰键</param>
        /// <param name="currentKey">当前按键</param>
        /// <param name="defaultModifiers">默认/推荐修饰键 (用于重置)</param>
        /// <param name="defaultKey">默认/推荐按键 (用于重置)</param>
        public ShortcutEditorDialog(VirtualKeyModifiers currentModifiers, VirtualKey currentKey,
                                    VirtualKeyModifiers defaultModifiers, VirtualKey defaultKey)
        {
            // [新增] 在初始化组件前加载本地化字符串
            LoadLocalizedStrings();

            this.InitializeComponent();

            _defaultModifiers = defaultModifiers;
            _defaultKey = defaultKey;

            // 初始化显示
            UpdateKeysAndValidate(currentModifiers, currentKey);

            this.Loaded += (s, e) =>
            {
                // 自动聚焦以便捕获按键
                this.Focus(FocusState.Programmatic);
            };
        }

        private void LoadLocalizedStrings()
        {
            var loader = new ResourceLoader();

            TextInstruction = GetStr(loader, "ShortcutDialog_Instruction", "按组合键以更改此快捷键");
            TextWaiting = GetStr(loader, "ShortcutDialog_Waiting", "未设置 (请按下键盘...)");
            TextError = GetStr(loader, "ShortcutDialog_Error", "无效快捷键");
            TextReset = GetStr(loader, "ShortcutDialog_Reset", "重置");
            TextResetToolTip = GetStr(loader, "ShortcutDialog_ResetToolTip", "恢复到初始设置");
            TextClear = GetStr(loader, "ShortcutDialog_Clear", "清除");
            TextClearToolTip = GetStr(loader, "ShortcutDialog_ClearToolTip", "清除快捷键 (设为空)");
            TextDescription = GetStr(loader, "ShortcutDialog_Description", "只有以 Ctrl、Alt、Shift 或 Win 开头的快捷键才有效。");
        }

        private string GetStr(ResourceLoader loader, string key, string defaultVal)
        {
            try
            {
                var val = loader.GetString(key);
                return string.IsNullOrEmpty(val) ? defaultVal : val;
            }
            catch { return defaultVal; }
        }

        private void OnContentKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 如果焦点在按钮上，或者是 Tab 键，不要拦截，允许用户进行键盘导航
            // 这样用户可以用 Tab 切换到“保存/取消/重置”按钮并按 Enter/Space 触发它们，而不会被识别为快捷键
            if (e.OriginalSource is FrameworkElement fe &&
               (fe is Button || fe is HyperlinkButton || fe is TextBox))
            {
                return;
            }
            // 忽略 Tab 键，防止焦点陷入
            if (e.Key == VirtualKey.Tab) return;

            e.Handled = true;

            var key = e.Key;

            // 获取当前修饰键状态
            var modifiers = VirtualKeyModifiers.None;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Control;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Menu;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Shift;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Windows;

            // 如果按下的是修饰键本身
            if (IsModifierKey(key))
            {
                UpdateKeysAndValidate(modifiers, VirtualKey.None);
            }
            else
            {
                UpdateKeysAndValidate(modifiers, key);
            }
        }

        private void OnContentKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Tab) return;
            e.Handled = true;
        }

        // 点击“重置” -> 恢复到默认值 (Default)
        private void OnResetClicked(object sender, RoutedEventArgs e)
        {
            UpdateKeysAndValidate(_defaultModifiers, _defaultKey);

            // 重置焦点回对话框，方便用户继续输入（如果他们想在重置基础上再改）
            this.Focus(FocusState.Programmatic);
        }

        // 点击“清除” -> 清空
        private void OnClearClicked(object sender, RoutedEventArgs e)
        {
            UpdateKeysAndValidate(VirtualKeyModifiers.None, VirtualKey.None);
            this.Focus(FocusState.Programmatic);
        }

        private void UpdateKeysAndValidate(VirtualKeyModifiers modifiers, VirtualKey key)
        {
            // 1. 更新内部数据
            ResultModifiers = modifiers;
            ResultKey = key;

            // 2. 更新 UI 列表
            CurrentKeys.Clear();
            // 注意：这些键名通常是通用术语，可以用英文，或者从 ResourceLoader 获取，
            // 但为了简洁和习惯，这里保留标准缩写。
            if (modifiers.HasFlag(VirtualKeyModifiers.Windows)) CurrentKeys.Add("Win");
            if (modifiers.HasFlag(VirtualKeyModifiers.Control)) CurrentKeys.Add("Ctrl");
            if (modifiers.HasFlag(VirtualKeyModifiers.Menu)) CurrentKeys.Add("Alt");
            if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) CurrentKeys.Add("Shift");

            if (key != VirtualKey.None)
            {
                CurrentKeys.Add(key.ToString());
            }

            Bindings.Update();

            // 3. 执行验证
            ValidateShortcut();
        }

        private void ValidateShortcut()
        {
            bool isValid = false;
            bool isError = false;

            // 情况 A: 完全为空 (清除状态) -> 有效
            if (ResultModifiers == VirtualKeyModifiers.None && ResultKey == VirtualKey.None)
            {
                isValid = true;
                isError = false;
            }
            // 情况 B: 有修饰键，也有普通键 -> 有效
            else if (ResultModifiers != VirtualKeyModifiers.None && ResultKey != VirtualKey.None)
            {
                isValid = true;
                isError = false;
            }
            // 情况 C: 只有普通键，没有修饰键 -> 无效
            else if (ResultModifiers == VirtualKeyModifiers.None && ResultKey != VirtualKey.None)
            {
                isValid = false;
                isError = true;
            }
            // 情况 D: 只有修饰键，没有普通键 (输入中) -> 无效，但不提示红色错误，只禁用保存
            else
            {
                isValid = false;
                if (CurrentKeys.Count > 0)
                {
                    isError = true;
                }
            }

            // 4. 更新 UI 状态
            IsPrimaryButtonEnabled = isValid;
            IsErrorVisible = isError;
        }

        private bool IsModifierKey(VirtualKey key)
        {
            return key == VirtualKey.Control || key == VirtualKey.Menu || key == VirtualKey.Shift ||
                   key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}