using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VRAMonitor.Services;
using VRAMonitor.ViewModels.Pages;
using VRAMonitor.Views.Interfaces; // [修改] 更新命名空间引用
using Windows.ApplicationModel.Resources;

namespace VRAMonitor.Views.Pages
{
    // 实现 ISearchablePage 接口
    public sealed partial class ProcessesPage : Page, ISearchablePage
    {
        public ProcessesPageViewModel ViewModel { get; }
        private readonly ResourceLoader _resourceLoader;

        private readonly HashSet<string> _criticalSystemProcesses = new()
        {
            "csrss", "wininit", "smss", "services", "lsass", "winlogon",
            "svchost", "sihost", "fontdrvhost", "memory compression",
            "system", "registry", "audiodg"
        };
        private readonly HashSet<string> _allowedSystemProcesses = new()
        {
            "dwm", "explorer", "taskmgr", "tabtip"
        };

        #region P/Invoke Definitions
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpVerb;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpFile;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpParameters;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }
        private const int SW_SHOW = 5;
        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        #endregion

        public ProcessesPage()
        {
            ViewModel = new ProcessesPageViewModel();
            InitializeComponent();

            try { _resourceLoader = new ResourceLoader(); } catch { }

            ViewModel.GroupingChanged += OnViewModelGroupingChanged;
            SettingsManager.ShowSelectionPillChanged += OnShowSelectionPillChanged;

            UpdateCollectionViewSource();
            UpdateListViewStyle(SettingsManager.ShowSelectionPill);
        }

        // === 实现 ISearchablePage 接口 ===

        public void OnSearch(string query)
        {
            // 将搜索请求转发给 ViewModel
            ViewModel.FilterText = query;
        }

        public string SearchPlaceholderText
        {
            get => _resourceLoader?.GetString("SearchPlaceholder_Processes") ?? "Search processes...";
        }

        // ===================================

        private void UpdateListViewStyle(bool showPill)
        {
            string styleKey = showPill ? "ProcessItemStyle_WithPill" : "ProcessItemStyle_NoPill";
            if (Resources.TryGetValue(styleKey, out object styleObj) && styleObj is Style style)
            {
                ProcessListView.ItemContainerStyle = style;
            }
        }

        private void OnShowSelectionPillChanged(object sender, bool showPill)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateListViewStyle(showPill);
            });
        }

        private void OnViewModelGroupingChanged(object sender, EventArgs e)
        {
            UpdateCollectionViewSource();
        }

        private void UpdateCollectionViewSource()
        {
            ProcessCVS.Source = null;

            if (ViewModel.IsGrouped)
            {
                ProcessCVS.IsSourceGrouped = true;
                ProcessCVS.Source = ViewModel.GroupedProcesses;
            }
            else
            {
                ProcessCVS.IsSourceGrouped = false;
                ProcessCVS.Source = ViewModel.FilteredProcesses;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Initialize();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ViewModel.Cleanup();
            ViewModel.GroupingChanged -= OnViewModelGroupingChanged;
            SettingsManager.ShowSelectionPillChanged -= OnShowSelectionPillChanged;
        }

        private async void OnEndProcessClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedProcess == null) return;
            var gpuProc = ViewModel.SelectedProcess;

            try
            {
                var proc = Process.GetProcessById((int)gpuProc.Pid);
                string procName = proc.ProcessName.ToLower();

                if (_criticalSystemProcesses.Contains(procName))
                {
                    await ShowMessageDialog("操作被拒绝", $"“{gpuProc.Name}”是 Windows 关键系统进程，结束它会导致系统不稳定或蓝屏。");
                    return;
                }

                string warningText = $"确定要结束 “{gpuProc.Name}” (PID: {gpuProc.Pid}) 吗？\n\n请确保已保存该应用中的所有数据。";
                if (_allowedSystemProcesses.Contains(procName))
                    warningText += "\n\n警告：这是一个系统组件。结束它可能会导致桌面短暂闪烁或任务栏重启。";

                var confirmDialog = new ContentDialog
                {
                    Title = "结束进程确认",
                    Content = warningText,
                    PrimaryButtonText = "结束进程",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var result = await confirmDialog.ShowAsync();
                if (result == ContentDialogResult.Primary) proc.Kill();
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("无法结束进程", $"操作失败: {ex.Message}\n该进程可能需要管理员权限才能结束，或者已经退出。");
            }
        }

        private async void OnEfficiencyModeClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedProcess == null) return;
            var gpuProc = ViewModel.SelectedProcess;
            try
            {
                var proc = Process.GetProcessById((int)gpuProc.Pid);
                proc.PriorityClass = ProcessPriorityClass.Idle;
                await ShowMessageDialog("设置成功", $"已为 “{gpuProc.Name}” 开启效能模式 (低优先级)。\n这有助于减少它对系统资源的占用。");
            }
            catch (Exception ex)
            {
                await ShowMessageDialog("设置失败", $"无法更改优先级: {ex.Message}\n系统进程通常不允许修改此属性。");
            }
        }

        private void OnOpenLocationClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedProcess == null) return;
            string path = ViewModel.SelectedProcess.FullPath;
            OpenExplorerAndSelectFile(path);
        }

        private async void OnPropertiesClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedProcess == null) return;
            var process = ViewModel.SelectedProcess;

            var dialog = new ContentDialog { Title = "进程属性", XamlRoot = this.XamlRoot };
            var stackPanel = new StackPanel { Spacing = 10, Padding = new Thickness(10) };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 15 };

            if (process.Icon != null) headerStack.Children.Add(new Image { Source = process.Icon, Width = 48, Height = 48 });
            headerStack.Children.Add(new TextBlock { Text = process.Name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            stackPanel.Children.Add(headerStack);
            stackPanel.Children.Add(new Grid { Height = 10 });

            UIElement CreateDetailRow(string label, string value)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = label, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], FontSize = 12 });
                var valBlock = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                row.Children.Add(valBlock);
                return row;
            }

            stackPanel.Children.Add(CreateDetailRow("文件路径", string.IsNullOrEmpty(process.FullPath) ? "N/A" : process.FullPath));
            stackPanel.Children.Add(CreateDetailRow("发布者", string.IsNullOrEmpty(process.Publisher) ? "未知" : process.Publisher));
            stackPanel.Children.Add(CreateDetailRow("进程 ID (PID)", process.Pid.ToString()));
            stackPanel.Children.Add(CreateDetailRow("显存使用", process.VramUsageFormatted));

            var buttonGrid = new Grid { Margin = new Thickness(0, 25, 0, 0) };
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var openPropsBtn = new Button { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var btnContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btnContent.Children.Add(new FontIcon { Glyph = "\uEC50", FontSize = 14 });
            btnContent.Children.Add(new TextBlock { Text = "在文件管理器中打开属性" });
            openPropsBtn.Content = btnContent;
            openPropsBtn.Click += (s, a) => {
                if (!string.IsNullOrEmpty(process.FullPath) && !process.FullPath.StartsWith("N/A") && File.Exists(process.FullPath))
                    ShowNativeFileProperties(process.FullPath);
            };

            var okBtn = new Button { Content = "确定", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object accentStyle)) okBtn.Style = (Style)accentStyle;
            okBtn.Click += (s, a) => dialog.Hide();

            Grid.SetColumn(openPropsBtn, 0); Grid.SetColumn(okBtn, 1);
            buttonGrid.Children.Add(openPropsBtn); buttonGrid.Children.Add(okBtn);
            stackPanel.Children.Add(buttonGrid);
            dialog.Content = stackPanel;
            await dialog.ShowAsync();
        }

        private async System.Threading.Tasks.Task ShowMessageDialog(string title, string content)
        {
            var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "确定", XamlRoot = this.XamlRoot };
            await dialog.ShowAsync();
        }

        private async void OpenExplorerAndSelectFile(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("N/A") || !File.Exists(path))
            {
                await ShowMessageDialog("无法打开", "无法获取该进程的文件路径，或者路径不可访问。");
                return;
            }
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception ex) { await ShowMessageDialog("错误", $"无法打开资源管理器: {ex.Message}"); }
        }

        private void ShowNativeFileProperties(string filename)
        {
            try
            {
                var info = new SHELLEXECUTEINFO();
                info.cbSize = Marshal.SizeOf(info);
                info.lpVerb = "properties";
                info.lpFile = filename;
                info.nShow = SW_SHOW;
                info.fMask = SEE_MASK_INVOKEIDLIST;
                ShellExecuteEx(ref info);
            }
            catch { }
        }
    }
}