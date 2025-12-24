using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using VRAMonitor.Helpers;
using VRAMonitor.Models;
using VRAMonitor.Services;
using VRAMonitor.Services.Providers;
using VRAMonitor.ViewModels.Shared;
using Windows.ApplicationModel.Resources;
using Windows.Storage.Streams;

namespace VRAMonitor.ViewModels.Pages
{
    public enum SortColumn { App, Status, Pid, Vram, Engine }
    public enum SortDirection { Ascending, Descending }

    public partial class ProcessesPageViewModel : ObservableObject
    {
        private readonly ResourceLoader _resourceLoader;
        private readonly DispatcherQueue _dispatcherQueue;

        private DispatcherTimer _timer;
        private IGpuTelemetryProvider _gpuProvider;

        // [健壮性] 引入生命周期标志和取消令牌
        private bool _isActive = false;
        private CancellationTokenSource _cts;

        private bool _isInitialized;

        private readonly Dictionary<uint, ProcessInfoHelper.ProcessMetadata> _metadataCache = new();
        private readonly Dictionary<uint, BitmapImage> _iconUiCache = new();

        private DateTime _lastUpdateTime = DateTime.MinValue;

        [ObservableProperty] private string _gpuName = "正在检测...";
        [ObservableProperty] private string _totalVramStatus = "";
        [ObservableProperty] private string _statusMessage = "准备就绪";
        [ObservableProperty] private GpuProcess _selectedProcess;
        [ObservableProperty] private bool _isGrouped = false;
        [ObservableProperty] private string _filterText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Processes))]
        private SortColumn _currentSortColumn = SortColumn.Vram;

        [ObservableProperty]
        private SortDirection _currentSortDirection = SortDirection.Descending;

        public ObservableCollection<GpuProcess> FilteredProcesses { get; } = new();
        public ObservableCollection<GroupInfoList> GroupedProcesses { get; private set; } = new();

        public ObservableCollection<GpuStatusViewModel> GpuStatusList { get; } = new();

        private readonly List<GpuProcess> _allProcesses = new();
        public ObservableCollection<GpuProcess> Processes => FilteredProcesses;

        public event EventHandler GroupingChanged;

        public ProcessesPageViewModel(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
            try { _resourceLoader = new ResourceLoader(); } catch { }
        }

        public void Initialize()
        {
            if (_isInitialized) return;

            // [健壮性] 标记为活动状态，并创建新的取消令牌
            _isActive = true;
            _cts = new CancellationTokenSource();

            // [性能/代码简化] 直接使用 NvidiaTelemetryProvider，它内部会自动处理回退逻辑
            // 这样避免了外部重复判断，且更具扩展性
            _gpuProvider = new NvidiaTelemetryProvider();

            // 如果连 WDDM 都不支持（极罕见），则提示错误
            if (!_gpuProvider.IsSupported)
            {
                StatusMessage = "错误: 您的系统不支持 GPU 性能计数器。";
                GpuName = "硬件不受支持";
                // 即使不支持，也标记初始化完成，避免反复调用
                _isInitialized = true;
                return;
            }

            GpuName = _gpuProvider.GetGpuName();
            UpdateStatusMessage();

            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => UpdateVramUsage();
            SettingsManager.ScanIntervalChanged += OnScanIntervalChanged;
            UpdateTimerState(SettingsManager.ScanInterval);

            // 立即触发一次更新
            if (_timer.IsEnabled) UpdateVramUsage();

            _isInitialized = true;
        }

        // [健壮性] 彻底的清理逻辑，防止崩溃报告中的 COMException
        public void Cleanup()
        {
            _isActive = false; // 1. 立即停止任何新的 UI 调度
            _cts?.Cancel();    // 2. 取消所有正在进行的元数据加载任务
            _cts?.Dispose();
            _cts = null;

            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            SettingsManager.ScanIntervalChanged -= OnScanIntervalChanged;
            _gpuProvider?.Dispose();
            _isInitialized = false;
        }

        [RelayCommand]
        private void Sort(string columnKey)
        {
            if (Enum.TryParse<SortColumn>(columnKey, true, out var column))
            {
                if (column == CurrentSortColumn)
                {
                    CurrentSortDirection = (CurrentSortDirection == SortDirection.Ascending)
                        ? SortDirection.Descending
                        : SortDirection.Ascending;
                }
                else
                {
                    CurrentSortColumn = column;
                    if (column == SortColumn.Vram || column == SortColumn.Pid)
                        CurrentSortDirection = SortDirection.Descending;
                    else
                        CurrentSortDirection = SortDirection.Ascending;
                }

                OnPropertyChanged(nameof(CurrentSortColumn));
                OnPropertyChanged(nameof(CurrentSortDirection));

                RefreshDisplay();
            }
        }

        public Visibility GetSortIconVisibility(string columnKey, SortColumn trigger)
        {
            if (Enum.TryParse<SortColumn>(columnKey, true, out var col))
                return col == CurrentSortColumn ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public string GetSortIconGlyph(SortDirection trigger)
        {
            return CurrentSortDirection == SortDirection.Ascending ? "\uE70E" : "\uE70D";
        }

        private IEnumerable<GpuProcess> GetSortedList(IEnumerable<GpuProcess> list)
        {
            Func<GpuProcess, object> keySelector = CurrentSortColumn switch
            {
                SortColumn.App => p => p.Name,
                SortColumn.Pid => p => p.Pid,
                SortColumn.Vram => p => p.VramUsageBytes,
                SortColumn.Engine => p => p.GpuEngine,
                _ => p => p.VramUsageBytes
            };

            return CurrentSortDirection == SortDirection.Ascending
                ? list.OrderBy(keySelector).ThenBy(p => p.Pid)
                : list.OrderByDescending(keySelector).ThenBy(p => p.Pid);
        }

        private async void UpdateVramUsage()
        {
            // [健壮性] 如果页面已非活动，直接跳过
            if (!_isActive || _gpuProvider == null) return;

            try
            {
                _gpuProvider.Refresh();
                _lastUpdateTime = DateTime.Now;

                // 1. 获取 GPU 状态 (后台线程)
                var gpuInfos = await Task.Run(() => _gpuProvider.GetGpuStatuses());

                // [健壮性] 再次检查，防止 await 期间页面被关闭
                if (!_isActive) return;

                var viewModels = new List<GpuStatusViewModel>();
                for (int i = 0; i < gpuInfos.Count; i++)
                {
                    var info = gpuInfos[i];
                    string header = $"[GPU {info.Index}] {info.Name}"; // 使用 Index

                    double corePercent = 0;
                    string coreLabel = "Core";

                    if (info.IsIntegrated)
                    {
                        coreLabel = "Core (Shared)";
                        if (info.SharedTotal > 0)
                            corePercent = (double)info.SharedUsed / info.SharedTotal * 100.0;
                    }
                    else
                    {
                        coreLabel = "Core (VRAM)";
                        if (info.CoreLoad > 0) corePercent = info.CoreLoad;
                        else if (info.DedicatedTotal > 0) corePercent = (double)info.DedicatedUsed / info.DedicatedTotal * 100.0;
                    }

                    string vramStr = FormatSize(info.DedicatedUsed, info.DedicatedTotal);
                    string sharedStr = FormatSize(info.SharedUsed, info.SharedTotal);
                    string details = $"{coreLabel} {corePercent:F1}% | VRAM {vramStr} | Shared {sharedStr}";

                    viewModels.Add(new GpuStatusViewModel { Header = header, Details = details });
                }

                // 2. 获取进程列表 (后台线程)
                var usageMap = await Task.Run(() => _gpuProvider.GetProcessVramUsage());

                if (!_isActive) return;

                // 3. UI 更新 (统一调度)
                _dispatcherQueue.TryEnqueue(() =>
                {
                    // [健壮性] 即使在 Dispatcher 中，也要检查 Active 状态并捕获异常
                    if (!_isActive) return;

                    try
                    {
                        UpdateGpuStatusList(viewModels);
                        UpdateProcessList(usageMap);
                    }
                    catch (COMException)
                    {
                        // [崩溃修复] 忽略 "RPC_E_WRONG_THREAD" 或对象断开连接的异常
                        // 这通常发生在程序退出或窗口销毁的瞬间
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = $"更新异常: {ex.Message}";
                    }
                });
            }
            catch (Exception)
            {
                // 忽略 TaskCancelled 或其他后台异常
            }
        }

        private void UpdateGpuStatusList(List<GpuStatusViewModel> viewModels)
        {
            if (GpuStatusList.Count != viewModels.Count)
            {
                GpuStatusList.Clear();
                foreach (var vm in viewModels) GpuStatusList.Add(vm);
            }
            else
            {
                for (int i = 0; i < viewModels.Count; i++)
                {
                    // 仅在值变化时赋值，减少 UI 重绘开销
                    if (GpuStatusList[i].Header != viewModels[i].Header)
                        GpuStatusList[i].Header = viewModels[i].Header;

                    if (GpuStatusList[i].Details != viewModels[i].Details)
                        GpuStatusList[i].Details = viewModels[i].Details;
                }
            }
        }

        private void UpdateProcessList(Dictionary<uint, (ulong Vram, string Engine)> usageMap)
        {
            if (usageMap.Count == 0)
            {
                if (_allProcesses.Count > 0)
                {
                    _allProcesses.Clear();
                    RefreshDisplay();
                }
                UpdateStatusMessage();
                return;
            }

            var newProcessList = new List<GpuProcess>();
            var currentCancellationToken = _cts?.Token ?? CancellationToken.None;

            foreach (var kvp in usageMap)
            {
                uint pid = kvp.Key;
                var (vram, engineStr) = kvp.Value;

                // 快速判断效能模式，不在此处做重 IO
                bool isEfficiency = false;
                try
                {
                    // 警告: Process.GetProcessById 比较慢，最好能缓存 handle 或减少频率
                    // 但 PriorityClass 变化不频繁，可以考虑后续优化
                    // 这里为了简单暂且保留，但要防崩
                    using var sysProc = Process.GetProcessById((int)pid);
                    if (sysProc.PriorityClass == ProcessPriorityClass.Idle) isEfficiency = true;
                }
                catch { }

                var gpuProc = new GpuProcess
                {
                    Pid = pid,
                    VramUsageBytes = vram,
                    IsEfficiencyMode = isEfficiency,
                    GpuEngine = engineStr
                };

                // 尝试从缓存恢复元数据
                if (_metadataCache.TryGetValue(pid, out var cachedMeta))
                {
                    gpuProc.Name = cachedMeta.FriendlyName;
                    gpuProc.FullPath = cachedMeta.FullPath;
                    gpuProc.Publisher = cachedMeta.Publisher;
                    if (_iconUiCache.TryGetValue(pid, out var cachedIcon)) gpuProc.Icon = cachedIcon;
                }
                else
                {
                    // 缓存未命中，启动异步加载
                    gpuProc.Name = $"PID: {pid} (加载中...)";
                    // [健壮性] 传递 CancellationToken
                    _ = LoadProcessMetadataAsync(pid, currentCancellationToken);
                }
                newProcessList.Add(gpuProc);
            }

            MergeProcessData(newProcessList);
            UpdateStatusMessage();
        }

        private string FormatSize(ulong used, ulong total)
        {
            double usedMB = used / 1024.0 / 1024.0;
            double totalMB = total / 1024.0 / 1024.0;

            if (totalMB > 1024) return $"{usedMB / 1024.0:F2} / {totalMB / 1024.0:F2} GB";
            else return $"{usedMB:F0} / {totalMB:F0} MB";
        }

        // [性能优化] 异步加载元数据
        private async Task LoadProcessMetadataAsync(uint pid, CancellationToken ct)
        {
            // 1. 在后台线程做繁重的 IO (文件读取、图标提取字节流)
            var meta = await ProcessInfoHelper.GetMetadataAsync(pid);

            if (ct.IsCancellationRequested) return;

            lock (_metadataCache)
            {
                if (!_metadataCache.ContainsKey(pid)) _metadataCache[pid] = meta;
            }

            // 2. 切回 UI 线程创建 BitmapImage (必须在 UI 线程)
            _dispatcherQueue.TryEnqueue(async () =>
            {
                if (ct.IsCancellationRequested || !_isActive) return;

                try
                {
                    BitmapImage bitmap = null;
                    if (meta.IconData != null && meta.IconData.Length > 0)
                    {
                        try
                        {
                            bitmap = new BitmapImage();
                            using (var stream = new InMemoryRandomAccessStream())
                            {
                                await stream.WriteAsync(meta.IconData.AsBuffer());
                                stream.Seek(0);
                                await bitmap.SetSourceAsync(stream);
                            }
                        }
                        catch { }
                    }

                    if (bitmap != null) _iconUiCache[pid] = bitmap;

                    // [关键性能优化]
                    // 找到列表中已存在的对象，直接更新属性。
                    // 这一步会自动触发 UI 绑定更新，而不需要调用昂贵的 RefreshDisplay()
                    var proc = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                    if (proc != null)
                    {
                        proc.Name = meta.FriendlyName;
                        proc.FullPath = meta.FullPath;
                        proc.Publisher = meta.Publisher;
                        proc.Icon = bitmap;

                        // 注意：这里删除了 RefreshDisplay() 调用。
                        // 带来的副作用是：刚加载出来的名称可能没有按照首字母正确排序。
                        // 但这对用户体验影响很小（下次刷新就会对齐），却能消除 99% 的界面卡顿。
                    }
                }
                catch (COMException) { } // 忽略关闭时的异常
            });
        }

        private void MergeProcessData(List<GpuProcess> newProcesses)
        {
            var orderedNew = newProcesses.OrderBy(p => p.Pid).ToList();
            bool listChanged = false;

            // 1. 删除不存在的进程
            var pidsToRemove = _allProcesses.Select(p => p.Pid).Except(orderedNew.Select(p => p.Pid)).ToList();
            if (pidsToRemove.Count > 0) listChanged = true;

            foreach (var pid in pidsToRemove)
            {
                var target = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (target != null)
                {
                    _allProcesses.Remove(target);
                    // 延迟清理缓存（可选），防止进程闪烁导致反复加载
                }
            }

            // 2. 更新或添加进程
            foreach (var newProc in orderedNew)
            {
                var existing = _allProcesses.FirstOrDefault(p => p.Pid == newProc.Pid);
                if (existing != null)
                {
                    // 仅在值确实改变时设置，避免无谓的 NotifyPropertyChanged
                    if (existing.VramUsageBytes != newProc.VramUsageBytes) existing.VramUsageBytes = newProc.VramUsageBytes;
                    if (existing.IsEfficiencyMode != newProc.IsEfficiencyMode) existing.IsEfficiencyMode = newProc.IsEfficiencyMode;
                    if (existing.GpuEngine != newProc.GpuEngine) existing.GpuEngine = newProc.GpuEngine;

                    // 如果新数据已经加载完元数据，且旧数据没有，则更新
                    // (这种情况通常发生在元数据刚刚加载完成的下一帧)
                    if (!newProc.Name.Contains("(加载中...)") && existing.Name.Contains("(加载中...)"))
                    {
                        existing.Name = newProc.Name;
                        existing.FullPath = newProc.FullPath;
                        existing.Publisher = newProc.Publisher;
                        existing.Icon = newProc.Icon;
                        // 这里不用置 listChanged = true，因为属性变更会自动刷新 UI
                    }
                }
                else
                {
                    _allProcesses.Add(newProc);
                    listChanged = true;
                }
            }

            // 仅当列表结构发生变化（增/删）时，才重新排序和过滤
            if (listChanged)
            {
                RefreshDisplay();
            }
            else
            {
                // 如果只是数据变了，检查是否需要重新排序
                // (例如 VRAM 变化导致排序变了)
                // 这里做一个简单的检查：如果是按 VRAM 排序，且列表不为空，手动触发一次 Source 更新可能太重
                // 现有的逻辑是依靠 View 自动响应属性变化吗？
                // WinUI 的 ListView 不会自动根据属性变化重新排序。
                // 为了性能，我们可以每隔几次刷新或者是当 VRAM 变化巨大时才 RefreshDisplay
                // 目前保持现状：如果不增删，就不重排，以保持列表稳定，防止用户点击错位
            }
        }

        // [辅助函数] 保持 FilteredProcesses 与 _allProcesses 同步
        private void SyncObservableCollection(ObservableCollection<GpuProcess> target, List<GpuProcess> source)
        {
            // 1. Remove
            var sourcePids = new HashSet<uint>(source.Select(p => p.Pid));
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!sourcePids.Contains(target[i].Pid)) target.RemoveAt(i);
            }

            // 2. Add / Move / Update
            for (int i = 0; i < source.Count; i++)
            {
                var sourceItem = source[i];

                // 目标位置 i 处的元素不对
                if (i >= target.Count || target[i].Pid != sourceItem.Pid)
                {
                    // 查找这个元素是否在后面
                    int existingIndex = -1;
                    for (int j = i + 1; j < target.Count; j++)
                    {
                        if (target[j].Pid == sourceItem.Pid) { existingIndex = j; break; }
                    }

                    if (existingIndex >= 0)
                    {
                        // 找到了，移动过来
                        target.Move(existingIndex, i);
                    }
                    else
                    {
                        // 没找到，插入
                        target.Insert(i, sourceItem);
                    }
                }
                // 如果 Pid 匹配，因为是引用类型，内容(VRAM等)已经自动更新了
            }
        }

        private void SyncGroupedCollection(ObservableCollection<GroupInfoList> target, List<GroupInfoList> source)
        {
            // 简化版同步逻辑，分组变化不频繁，可以直接重置
            // 但为了平滑，保留部分同步逻辑
            target.Clear();
            foreach (var s in source) target.Add(s);
        }

        private void RefreshDisplay()
        {
            // 如果已经被标记为停止，就不再处理复杂的列表逻辑
            if (!_isActive) return;

            try
            {
                IEnumerable<GpuProcess> query = _allProcesses;
                if (!string.IsNullOrWhiteSpace(FilterText))
                {
                    string lower = FilterText.Trim().ToLower();
                    query = query.Where(p => (p.Name != null && p.Name.ToLower().Contains(lower)) || p.Pid.ToString().Contains(lower) || (p.Publisher != null && p.Publisher.ToLower().Contains(lower)));
                }

                var filteredList = query.ToList();

                if (!IsGrouped)
                {
                    var sortedList = GetSortedList(filteredList).ToList();
                    SyncObservableCollection(FilteredProcesses, sortedList);
                }
                else
                {
                    RefreshGrouping(filteredList);
                }
            }
            catch (Exception) { }
        }

        private void RefreshGrouping(List<GpuProcess> sourceList)
        {
            var query = from item in sourceList
                        group item by GetGroupKey(item.Name) into g
                        orderby g.Key
                        select new GroupInfoList(GetSortedList(g)) { Key = g.Key };

            var newGroups = query.ToList();

            // 为了避免分组时的复杂 Diff 导致 Bug，这里简单处理
            // 如果需要极致性能，可以参考 SyncObservableCollection 实现 SyncGroupedCollection
            GroupedProcesses.Clear();
            foreach (var g in newGroups) GroupedProcesses.Add(g);
        }

        private string GetGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "#";
            char c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') return c.ToString();
            return "#";
        }

        private void OnScanIntervalChanged(object sender, double interval)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_isActive)
                {
                    UpdateTimerState(interval);
                    UpdateStatusMessage();
                }
            });
        }

        private void UpdateTimerState(double interval)
        {
            if (_timer == null) return;
            if (interval <= 0) _timer.Stop();
            else
            {
                _timer.Interval = TimeSpan.FromSeconds(interval);
                if (!_timer.IsEnabled) { _timer.Start(); UpdateVramUsage(); }
            }
        }

        private void UpdateStatusMessage()
        {
            if (!_isActive) return;
            if (_resourceLoader == null) return;

            string statusFormat = _resourceLoader.GetString("StatusBar_Format");
            string intervalDesc;
            double currentInterval = SettingsManager.ScanInterval;
            if (currentInterval <= 0) intervalDesc = _resourceLoader.GetString("StatusBar_Stopped");
            else
            {
                string refreshFormat = _resourceLoader.GetString("StatusBar_RefreshRate");
                intervalDesc = string.Format(refreshFormat, currentInterval);
            }
            string timeStr = _lastUpdateTime == DateTime.MinValue ? "N/A" : _lastUpdateTime.ToString("HH:mm:ss");
            int gpuCount = _gpuProvider?.GpuCount ?? 0;
            try { StatusMessage = string.Format(statusFormat, timeStr, gpuCount, intervalDesc); }
            catch { StatusMessage = $"{timeStr} | {gpuCount} GPUs | {intervalDesc}"; }
        }

        [RelayCommand]
        private void ToggleGrouping()
        {
            IsGrouped = !IsGrouped;
            RefreshDisplay();
            GroupingChanged?.Invoke(this, EventArgs.Empty);
        }

        partial void OnFilterTextChanged(string value) => RefreshDisplay();
    }
}