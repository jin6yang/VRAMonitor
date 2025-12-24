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
using System.Runtime.InteropServices.WindowsRuntime;
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

            var nvProvider = new NvidiaTelemetryProvider();
            if (nvProvider.IsSupported)
            {
                _gpuProvider = nvProvider;
            }
            else
            {
                nvProvider.Dispose();
                var wddmProvider = new WddmTelemetryProvider();
                if (wddmProvider.IsSupported)
                {
                    _gpuProvider = wddmProvider;
                }
                else
                {
                    StatusMessage = "错误: 您的系统不支持 GPU 性能计数器。";
                    GpuName = "硬件不受支持";
                    return;
                }
            }

            GpuName = _gpuProvider.GetGpuName();
            UpdateStatusMessage();

            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => UpdateVramUsage();
            SettingsManager.ScanIntervalChanged += OnScanIntervalChanged;
            UpdateTimerState(SettingsManager.ScanInterval);

            if (_timer.IsEnabled) UpdateVramUsage();

            _isInitialized = true;
        }

        // ... Sort, Icon, Helpers ...
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
            {
                return col == CurrentSortColumn ? Visibility.Visible : Visibility.Collapsed;
            }
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
            if (_gpuProvider == null) return;

            try
            {
                _gpuProvider.Refresh();
                _lastUpdateTime = DateTime.Now;

                // 1. 获取 GPU 状态列表
                var gpuInfos = await Task.Run(() => _gpuProvider.GetGpuStatuses());

                var viewModels = new List<GpuStatusViewModel>();
                for (int i = 0; i < gpuInfos.Count; i++)
                {
                    var info = gpuInfos[i];

                    // Header: [GPU i] Name
                    string header = $"[GPU {i}] {info.Name}";

                    double corePercent = 0;
                    string coreLabel = "Core";

                    // [关键修改] 根据核显/独显显示不同的核心占用率来源
                    if (info.IsIntegrated)
                    {
                        // 核显：Core 代表 Shared Memory 占比
                        coreLabel = "Core (Shared)";
                        if (info.SharedTotal > 0)
                            corePercent = (double)info.SharedUsed / info.SharedTotal * 100.0;
                    }
                    else
                    {
                        // 独显：
                        // 如果有 CoreLoad (来自 NVML 或 WDDM Engine)，优先使用
                        // 否则使用 VRAM 占比作为参考
                        coreLabel = "Core (VRAM)";

                        // NVML 提供了 CoreLoad，直接使用
                        if (info.CoreLoad > 0)
                        {
                            corePercent = info.CoreLoad;
                        }
                        else if (info.DedicatedTotal > 0)
                        {
                            // 否则回退到显存占比
                            corePercent = (double)info.DedicatedUsed / info.DedicatedTotal * 100.0;
                        }
                    }

                    string vramStr = FormatSize(info.DedicatedUsed, info.DedicatedTotal);
                    string sharedStr = FormatSize(info.SharedUsed, info.SharedTotal);

                    // 构造显示的字符串
                    string details = $"{coreLabel} {corePercent:F1}% | VRAM {vramStr} | Shared {sharedStr}";

                    viewModels.Add(new GpuStatusViewModel { Header = header, Details = details });
                }

                _dispatcherQueue.TryEnqueue(() =>
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
                            if (GpuStatusList[i].Header != viewModels[i].Header ||
                                GpuStatusList[i].Details != viewModels[i].Details)
                            {
                                GpuStatusList[i].Header = viewModels[i].Header;
                                GpuStatusList[i].Details = viewModels[i].Details;
                            }
                        }
                    }
                });

                // 2. 获取进程列表 (保持原有逻辑)
                var usageMap = await Task.Run(() => _gpuProvider.GetProcessVramUsage());

                _dispatcherQueue.TryEnqueue(() =>
                {
                    if (usageMap.Count == 0)
                    {
                        _allProcesses.Clear();
                        RefreshDisplay();
                        UpdateStatusMessage();
                        return;
                    }

                    var newProcessList = new List<GpuProcess>();

                    foreach (var kvp in usageMap)
                    {
                        uint pid = kvp.Key;
                        var (vram, engineStr) = kvp.Value;

                        bool isEfficiency = false;
                        try
                        {
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

                        if (_metadataCache.TryGetValue(pid, out var cachedMeta))
                        {
                            gpuProc.Name = cachedMeta.FriendlyName;
                            gpuProc.FullPath = cachedMeta.FullPath;
                            gpuProc.Publisher = cachedMeta.Publisher;
                            if (_iconUiCache.TryGetValue(pid, out var cachedIcon)) gpuProc.Icon = cachedIcon;
                        }
                        else
                        {
                            gpuProc.Name = $"PID: {pid} (加载中...)";
                            _ = LoadProcessMetadataAsync(pid);
                        }
                        newProcessList.Add(gpuProc);
                    }

                    MergeProcessData(newProcessList);
                    UpdateStatusMessage();
                });
            }
            catch (Exception ex)
            {
                _dispatcherQueue.TryEnqueue(() => StatusMessage = $"更新失败: {ex.Message}");
            }
        }

        private string FormatSize(ulong used, ulong total)
        {
            double usedMB = used / 1024.0 / 1024.0;
            double totalMB = total / 1024.0 / 1024.0;

            if (totalMB > 1024)
            {
                return $"{usedMB / 1024.0:F2} / {totalMB / 1024.0:F2} GB";
            }
            else
            {
                return $"{usedMB:F0} / {totalMB:F0} MB";
            }
        }

        private async Task LoadProcessMetadataAsync(uint pid)
        {
            var meta = await ProcessInfoHelper.GetMetadataAsync(pid);
            lock (_metadataCache) { if (!_metadataCache.ContainsKey(pid)) _metadataCache[pid] = meta; }

            _dispatcherQueue.TryEnqueue(async () =>
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

                var proc = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (proc != null)
                {
                    proc.Name = meta.FriendlyName;
                    proc.FullPath = meta.FullPath;
                    proc.Publisher = meta.Publisher;
                    proc.Icon = bitmap;
                    RefreshDisplay();
                }
            });
        }

        private void MergeProcessData(List<GpuProcess> newProcesses)
        {
            var orderedNew = newProcesses.OrderBy(p => p.Pid).ToList();
            bool listChanged = false;

            var pidsToRemove = _allProcesses.Select(p => p.Pid).Except(orderedNew.Select(p => p.Pid)).ToList();
            if (pidsToRemove.Count > 0) listChanged = true;

            foreach (var pid in pidsToRemove)
            {
                var target = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (target != null)
                {
                    _allProcesses.Remove(target);
                    lock (_metadataCache) { _metadataCache.Remove(pid); }
                    _iconUiCache.Remove(pid);
                }
            }

            foreach (var newProc in orderedNew)
            {
                var existing = _allProcesses.FirstOrDefault(p => p.Pid == newProc.Pid);
                if (existing != null)
                {
                    existing.VramUsageBytes = newProc.VramUsageBytes;
                    existing.IsEfficiencyMode = newProc.IsEfficiencyMode;
                    existing.GpuEngine = newProc.GpuEngine;

                    if (!newProc.Name.Contains("(加载中...)"))
                    {
                        if (existing.Name != newProc.Name) listChanged = true;
                        if (existing.Icon == null && newProc.Icon != null) existing.Icon = newProc.Icon;
                    }
                }
                else
                {
                    _allProcesses.Add(newProc);
                    listChanged = true;
                }
            }
            RefreshDisplay();
        }

        private void SyncObservableCollection(ObservableCollection<GpuProcess> target, List<GpuProcess> source)
        {
            var sourcePids = new HashSet<uint>(source.Select(p => p.Pid));
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!sourcePids.Contains(target[i].Pid)) target.RemoveAt(i);
            }
            for (int i = 0; i < source.Count; i++)
            {
                var sourceItem = source[i];
                if (i >= target.Count || target[i].Pid != sourceItem.Pid)
                {
                    int existingIndex = -1;
                    for (int j = i + 1; j < target.Count; j++)
                    {
                        if (target[j].Pid == sourceItem.Pid) { existingIndex = j; break; }
                    }
                    if (existingIndex >= 0) target.Move(existingIndex, i);
                    else target.Insert(i, sourceItem);
                }
            }
        }

        private void SyncGroupedCollection(ObservableCollection<GroupInfoList> target, List<GroupInfoList> source)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var currentKey = target[i].Key.ToString();
                if (!source.Any(s => s.Key.ToString() == currentKey)) target.RemoveAt(i);
            }
            for (int i = 0; i < source.Count; i++)
            {
                var sourceGroup = source[i];
                var targetIndex = -1;
                for (int j = 0; j < target.Count; j++)
                {
                    if (target[j].Key.ToString() == sourceGroup.Key.ToString()) { targetIndex = j; break; }
                }
                if (targetIndex == -1) target.Insert(i, sourceGroup);
                else
                {
                    if (targetIndex != i) target.Move(targetIndex, i);
                    var targetGroup = target[i];
                    bool contentChanged = false;
                    if (targetGroup.Count != sourceGroup.Count) contentChanged = true;
                    else
                    {
                        for (int k = 0; k < targetGroup.Count; k++)
                        {
                            var tItem = targetGroup[k] as GpuProcess;
                            var sItem = sourceGroup[k] as GpuProcess;
                            if (tItem?.Pid != sItem?.Pid) { contentChanged = true; break; }
                        }
                    }
                    if (contentChanged) target[i] = sourceGroup;
                }
            }
        }

        private void RefreshDisplay()
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
                bool orderChanged = false;
                if (FilteredProcesses.Count != sortedList.Count) orderChanged = true;
                else
                {
                    for (int i = 0; i < sortedList.Count; i++)
                    {
                        if (FilteredProcesses[i].Pid != sortedList[i].Pid) { orderChanged = true; break; }
                    }
                }
                if (orderChanged) SyncObservableCollection(FilteredProcesses, sortedList);
            }
            else RefreshGrouping(filteredList);
        }

        private void RefreshGrouping(List<GpuProcess> sourceList)
        {
            var query = from item in sourceList group item by GetGroupKey(item.Name) into g orderby g.Key select new GroupInfoList(GetSortedList(g)) { Key = g.Key };
            var newGroups = query.ToList();
            SyncGroupedCollection(GroupedProcesses, newGroups);
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
            _dispatcherQueue.TryEnqueue(() => { UpdateTimerState(interval); UpdateStatusMessage(); });
        }

        private void UpdateTimerState(double interval)
        {
            if (interval <= 0) _timer.Stop();
            else
            {
                _timer.Interval = TimeSpan.FromSeconds(interval);
                if (!_timer.IsEnabled) { _timer.Start(); UpdateVramUsage(); }
            }
        }

        private void UpdateStatusMessage()
        {
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

        public void Cleanup()
        {
            _timer?.Stop();
            SettingsManager.ScanIntervalChanged -= OnScanIntervalChanged;
            _gpuProvider?.Dispose();
            _isInitialized = false;
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