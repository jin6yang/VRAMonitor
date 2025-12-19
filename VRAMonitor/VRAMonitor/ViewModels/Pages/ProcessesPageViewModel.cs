using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using VRAMonitor.Helpers;
using VRAMonitor.Models;
using VRAMonitor.Services;
using VRAMonitor.Services.Providers;
using Windows.ApplicationModel.Resources;

namespace VRAMonitor.ViewModels.Pages
{
    public partial class ProcessesPageViewModel : ObservableObject
    {
        private readonly ResourceLoader _resourceLoader;
        private readonly DispatcherQueue _dispatcherQueue;
        private DispatcherTimer _timer;
        private IGpuTelemetryProvider _gpuProvider;

        private bool _isInitialized;
        private readonly Dictionary<uint, ProcessInfoHelper.ProcessMetadata> _metadataCache = new();
        private DateTime _lastUpdateTime = DateTime.MinValue;

        [ObservableProperty] private string _gpuName = "正在检测...";
        [ObservableProperty] private string _totalVramStatus = "正在初始化...";
        [ObservableProperty] private string _statusMessage = "准备就绪";
        [ObservableProperty] private GpuProcess _selectedProcess;
        [ObservableProperty] private bool _isGrouped = false;
        [ObservableProperty] private string _filterText = "";

        public ObservableCollection<GpuProcess> FilteredProcesses { get; } = new();
        public ObservableCollection<GroupInfoList> GroupedProcesses { get; private set; } = new();

        // 维护一个全量列表，用于过滤和搜索
        private readonly List<GpuProcess> _allProcesses = new();

        public ObservableCollection<GpuProcess> Processes => FilteredProcesses;

        public event EventHandler GroupingChanged;

        public ProcessesPageViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try { _resourceLoader = new ResourceLoader(); } catch { }
        }

        public void Initialize()
        {
            if (_isInitialized) return;

            // 1. 尝试 NVIDIA NVML
            var nvProvider = new NvidiaTelemetryProvider();
            if (nvProvider.IsSupported)
            {
                _gpuProvider = nvProvider;
            }
            else
            {
                nvProvider.Dispose();
                // 2. 降级到 WDDM (通用)
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
            UpdateStatusMessage(); // 初始状态显示

            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => UpdateVramUsage();
            SettingsManager.ScanIntervalChanged += OnScanIntervalChanged;
            UpdateTimerState(SettingsManager.ScanInterval);

            if (_timer.IsEnabled) UpdateVramUsage();

            _isInitialized = true;
        }

        private async void UpdateVramUsage()
        {
            if (_gpuProvider == null) return;

            try
            {
                _gpuProvider.Refresh();
                _lastUpdateTime = DateTime.Now;

                string status = _gpuProvider.GetTotalVramStatus();
                _dispatcherQueue.TryEnqueue(() => TotalVramStatus = status);

                // 获取数据字典: PID -> (VRAM, EngineString)
                var usageMap = await Task.Run(() => _gpuProvider.GetProcessVramUsage());

                if (usageMap.Count == 0)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        _allProcesses.Clear();
                        RefreshDisplay();
                        UpdateStatusMessage();
                    });
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
                        // [恢复] 使用 Provider 返回的具体引擎字符串 (如 "GPU 0 - 3D")
                        GpuEngine = engineStr
                    };

                    if (_metadataCache.TryGetValue(pid, out var cachedMeta))
                    {
                        gpuProc.Name = cachedMeta.FriendlyName;
                        gpuProc.Icon = cachedMeta.Icon;
                        gpuProc.FullPath = cachedMeta.FullPath;
                        gpuProc.Publisher = cachedMeta.Publisher;
                    }
                    else
                    {
                        gpuProc.Name = $"PID: {pid} (加载中...)";
                        _ = LoadProcessMetadataAsync(pid);
                    }

                    newProcessList.Add(gpuProc);
                }

                _dispatcherQueue.TryEnqueue(() =>
                {
                    MergeProcessData(newProcessList);
                    UpdateStatusMessage();
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"更新失败: {ex.Message}";
            }
        }

        private async Task LoadProcessMetadataAsync(uint pid)
        {
            var meta = await ProcessInfoHelper.GetMetadataAsync(pid);
            lock (_metadataCache)
            {
                if (!_metadataCache.ContainsKey(pid)) _metadataCache[pid] = meta;
            }
            _dispatcherQueue.TryEnqueue(() =>
            {
                var proc = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (proc != null)
                {
                    proc.Name = meta.FriendlyName;
                    proc.Icon = meta.Icon;
                    proc.FullPath = meta.FullPath;
                    proc.Publisher = meta.Publisher;
                    // 元数据加载完毕后，也需要通过 RefreshDisplay 智能更新
                    RefreshDisplay();
                }
            });
        }

        private void MergeProcessData(List<GpuProcess> newProcesses)
        {
            var orderedNew = newProcesses.OrderByDescending(p => p.VramUsageBytes).ToList();
            bool listChanged = false;

            // 1. 标记删除
            var pidsToRemove = _allProcesses.Select(p => p.Pid).Except(orderedNew.Select(p => p.Pid)).ToList();
            if (pidsToRemove.Count > 0) listChanged = true;

            foreach (var pid in pidsToRemove)
            {
                var target = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (target != null)
                {
                    _allProcesses.Remove(target);
                    lock (_metadataCache) { _metadataCache.Remove(pid); }
                }
            }

            // 2. 更新或添加
            foreach (var newProc in orderedNew)
            {
                var existing = _allProcesses.FirstOrDefault(p => p.Pid == newProc.Pid);
                if (existing != null)
                {
                    // 更新现有数据
                    existing.VramUsageBytes = newProc.VramUsageBytes;
                    existing.IsEfficiencyMode = newProc.IsEfficiencyMode;
                    existing.GpuEngine = newProc.GpuEngine;

                    if (!newProc.Name.Contains("(加载中...)"))
                    {
                        if (existing.Name != newProc.Name) listChanged = true;
                        existing.Name = newProc.Name;
                        existing.Icon = newProc.Icon;
                        existing.FullPath = newProc.FullPath;
                        existing.Publisher = newProc.Publisher;
                    }
                }
                else
                {
                    _allProcesses.Add(newProc);
                    listChanged = true;
                }
            }

            // 调用刷新，这里会触发 SyncObservableCollection
            RefreshDisplay();
        }

        // [恢复] 原汁原味的 SyncObservableCollection 逻辑，防止 UI 闪烁
        private void SyncObservableCollection(ObservableCollection<GpuProcess> target, List<GpuProcess> source)
        {
            var sourcePids = new HashSet<uint>(source.Select(p => p.Pid));

            // 1. 删除
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!sourcePids.Contains(target[i].Pid))
                {
                    target.RemoveAt(i);
                }
            }

            // 2. 插入或移动
            for (int i = 0; i < source.Count; i++)
            {
                var sourceItem = source[i];
                if (i < target.Count && target[i].Pid == sourceItem.Pid) continue;

                int existingIndex = -1;
                for (int j = i + 1; j < target.Count; j++)
                {
                    if (target[j].Pid == sourceItem.Pid)
                    {
                        existingIndex = j;
                        break;
                    }
                }

                if (existingIndex >= 0) target.Move(existingIndex, i);
                else target.Insert(i, sourceItem);
            }
        }

        private void RefreshDisplay()
        {
            IEnumerable<GpuProcess> query = _allProcesses;
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                string lower = FilterText.Trim().ToLower();
                query = query.Where(p =>
                    (p.Name != null && p.Name.ToLower().Contains(lower)) ||
                    p.Pid.ToString().Contains(lower) ||
                    (p.Publisher != null && p.Publisher.ToLower().Contains(lower))
                );
            }

            var filteredList = query.ToList();

            // [恢复] 使用 Sync 方法而不是 Clear/Add
            SyncObservableCollection(FilteredProcesses, filteredList);

            if (IsGrouped) RefreshGrouping(filteredList);
        }

        private void RefreshGrouping(List<GpuProcess> sourceList)
        {
            var query = from item in sourceList
                        group item by GetGroupKey(item.Name) into g
                        orderby g.Key
                        select new GroupInfoList(g) { Key = g.Key };

            GroupedProcesses.Clear();
            foreach (var g in query) GroupedProcesses.Add(g);
        }

        private string GetGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "#";
            var c = name.Substring(0, 1).ToUpper();
            return char.IsLetter(c[0]) ? c : "#";
        }

        private void OnScanIntervalChanged(object sender, double interval)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                UpdateTimerState(interval);
                UpdateStatusMessage();
            });
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

        // [恢复] 完整的状态栏多语言逻辑
        private void UpdateStatusMessage()
        {
            if (_resourceLoader == null) return;

            string statusFormat = _resourceLoader.GetString("StatusBar_Format");
            // "最后更新于: {0} | 监控 {1} 个 GPU | {2}"

            string intervalDesc;
            double currentInterval = SettingsManager.ScanInterval;

            if (currentInterval <= 0)
            {
                intervalDesc = _resourceLoader.GetString("StatusBar_Stopped");
            }
            else
            {
                string refreshFormat = _resourceLoader.GetString("StatusBar_RefreshRate");
                intervalDesc = string.Format(refreshFormat, currentInterval);
            }

            string timeStr = _lastUpdateTime == DateTime.MinValue
                ? "N/A"
                : _lastUpdateTime.ToString("HH:mm:ss");

            int gpuCount = _gpuProvider?.GpuCount ?? 0;

            try
            {
                StatusMessage = string.Format(statusFormat, timeStr, gpuCount, intervalDesc);
            }
            catch
            {
                StatusMessage = $"{timeStr} | {gpuCount} GPUs | {intervalDesc}";
            }
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