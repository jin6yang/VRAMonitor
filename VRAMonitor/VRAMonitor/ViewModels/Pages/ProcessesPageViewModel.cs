using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using VRAMonitor.Models;
using VRAMonitor.Nvidia;
using VRAMonitor.Helpers;
using VRAMonitor.Services;
using Windows.ApplicationModel.Resources;

namespace VRAMonitor.ViewModels.Pages
{
    public partial class ProcessesPageViewModel : ObservableObject
    {
        // [新增] 引入 ResourceLoader
        private readonly ResourceLoader _resourceLoader;

        [ObservableProperty]
        private string _gpuName = "正在检测...";

        [ObservableProperty]
        private string _totalVramStatus = "正在获取显存信息...";

        [ObservableProperty]
        private string _statusMessage = "准备就绪";

        [ObservableProperty]
        private GpuProcess _selectedProcess;

        [ObservableProperty]
        private bool _isGrouped = false;

        [ObservableProperty]
        private string _filterText = "";

        public ObservableCollection<GpuProcess> FilteredProcesses { get; } = new();

        public ObservableCollection<GroupInfoList> GroupedProcesses { get; private set; } = new();

        private readonly List<GpuProcess> _allProcesses = new();

        public ObservableCollection<GpuProcess> Processes => FilteredProcesses;

        private readonly DispatcherQueue _dispatcherQueue;
        private DispatcherTimer _timer;
        private List<(int Index, IntPtr Handle, string Name)> _gpuDevices = new();
        private bool _isInitialized;
        private GpuMemoryCounterHelper _perfHelper;
        private readonly Dictionary<uint, ProcessInfoHelper.ProcessMetadata> _metadataCache = new();

        // 记录最后一次更新时间，用于状态栏显示
        private DateTime _lastUpdateTime = DateTime.MinValue;

        public event EventHandler GroupingChanged;

        public ProcessesPageViewModel()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _perfHelper = new GpuMemoryCounterHelper();
            try { _resourceLoader = new ResourceLoader(); } catch { }
        }

        [RelayCommand]
        private void ToggleGrouping()
        {
            IsGrouped = !IsGrouped;
            RefreshDisplay();
            GroupingChanged?.Invoke(this, EventArgs.Empty);
        }

        partial void OnFilterTextChanged(string value)
        {
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            IEnumerable<GpuProcess> query = _allProcesses;

            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                string lowerFilter = FilterText.Trim().ToLower();
                query = query.Where(p =>
                    (p.Name != null && p.Name.ToLower().Contains(lowerFilter)) ||
                    p.Pid.ToString().Contains(lowerFilter) ||
                    (p.Publisher != null && p.Publisher.ToLower().Contains(lowerFilter))
                );
            }

            var filteredList = query.ToList();
            SyncObservableCollection(FilteredProcesses, filteredList);

            if (IsGrouped)
            {
                RefreshGrouping(filteredList);
            }
        }

        private void SyncObservableCollection(ObservableCollection<GpuProcess> target, List<GpuProcess> source)
        {
            var sourcePids = new HashSet<uint>(source.Select(p => p.Pid));
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!sourcePids.Contains(target[i].Pid))
                {
                    target.RemoveAt(i);
                }
            }

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

        public void RefreshGrouping(List<GpuProcess> sourceList)
        {
            var query = from item in sourceList
                        group item by GetGroupKey(item.Name) into g
                        orderby g.Key
                        select new GroupInfoList(g) { Key = g.Key };

            GroupedProcesses.Clear();
            foreach (var g in query)
            {
                GroupedProcesses.Add(g);
            }
        }

        public void RefreshGrouping()
        {
            RefreshDisplay();
        }

        private string GetGroupKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return "#";
            var firstChar = name.Substring(0, 1).ToUpper();
            if (char.IsLetter(firstChar[0])) return firstChar;
            return "#";
        }

        public void Initialize()
        {
            if (_isInitialized) return;

            var result = NvmlApi.Init();
            if (result != NvmlApi.NvmlReturn.Success)
            {
                StatusMessage = $"NVML 初始化失败: {result}";
                return;
            }

            uint deviceCount = 0;
            NvmlApi.DeviceGetCount(ref deviceCount);
            if (deviceCount == 0)
            {
                StatusMessage = "未找到 NVIDIA GPU 设备。";
                NvmlApi.Shutdown();
                return;
            }

            _gpuDevices.Clear();
            var allNamesBuilder = new StringBuilder();
            var nameBuffer = new StringBuilder(256);

            for (uint i = 0; i < deviceCount; i++)
            {
                if (NvmlApi.DeviceGetHandleByIndex(i, out IntPtr handle) == NvmlApi.NvmlReturn.Success)
                {
                    NvmlApi.DeviceGetName(handle, nameBuffer, (uint)nameBuffer.Capacity);
                    string deviceName = nameBuffer.ToString();
                    string displayName = $"GPU {i}: {deviceName}";
                    _gpuDevices.Add(((int)i, handle, deviceName));

                    if (allNamesBuilder.Length > 0) allNamesBuilder.Append(" | ");
                    allNamesBuilder.Append(displayName);
                }
            }

            GpuName = allNamesBuilder.ToString();

            _timer = new DispatcherTimer();
            _timer.Tick += (s, e) => UpdateVramUsage();

            SettingsManager.ScanIntervalChanged += OnScanIntervalChanged;

            UpdateTimerState(SettingsManager.ScanInterval);

            if (_timer.IsEnabled) UpdateVramUsage();

            _isInitialized = true;
        }

        private void OnScanIntervalChanged(object sender, double interval)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                UpdateTimerState(interval);
                // 设置改变时，也刷新一下状态栏文案（为了显示新的间隔秒数或“已停止”）
                UpdateStatusMessage();
            });
        }

        private void UpdateTimerState(double interval)
        {
            if (interval <= 0)
            {
                _timer.Stop();
            }
            else
            {
                _timer.Interval = TimeSpan.FromSeconds(interval);
                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                    UpdateVramUsage();
                }
            }
        }

        // [新增] 统一状态消息构建逻辑
        private void UpdateStatusMessage()
        {
            if (_resourceLoader == null) return;

            string statusFormat = _resourceLoader.GetString("StatusBar_Format");
            // 格式示例: "最后更新于: {0} | 监控 {1} 个 GPU | {2}"
            // 参数 0: 时间
            // 参数 1: GPU 数量
            // 参数 2: 刷新间隔描述

            string intervalDesc;
            double currentInterval = SettingsManager.ScanInterval;

            if (currentInterval <= 0)
            {
                intervalDesc = _resourceLoader.GetString("StatusBar_Stopped"); // "已停止监控"
            }
            else
            {
                string refreshFormat = _resourceLoader.GetString("StatusBar_RefreshRate"); // "每 {0:F1} 秒刷新"
                intervalDesc = string.Format(refreshFormat, currentInterval);
            }

            // 如果从未更新过，用 "N/A" 或当前时间占位
            string timeStr = _lastUpdateTime == DateTime.MinValue
                ? "N/A"
                : _lastUpdateTime.ToString("HH:mm:ss");

            try
            {
                StatusMessage = string.Format(statusFormat, timeStr, _gpuDevices.Count, intervalDesc);
            }
            catch
            {
                // Fallback
                StatusMessage = $"{timeStr} | {_gpuDevices.Count} GPUs | {intervalDesc}";
            }
        }

        private async void UpdateVramUsage()
        {
            try
            {
                _perfHelper.Refresh();

                // 更新时间戳
                _lastUpdateTime = DateTime.Now;

                var pidDataMap = new Dictionary<uint, (ulong TotalVram, HashSet<string> EngineStrings, bool UsedPerfCounter)>();
                var statusBuilder = new StringBuilder();

                foreach (var (index, handle, gpuName) in _gpuDevices)
                {
                    NvmlApi.NvmlMemory memoryInfo;
                    if (NvmlApi.DeviceGetMemoryInfo(handle, out memoryInfo) == NvmlApi.NvmlReturn.Success)
                    {
                        double totalGB = memoryInfo.total / 1024.0 / 1024.0 / 1024.0;
                        double usedGB = memoryInfo.used / 1024.0 / 1024.0 / 1024.0;
                        double percent = (double)memoryInfo.used / memoryInfo.total * 100.0;

                        if (statusBuilder.Length > 0) statusBuilder.Append("   |   ");
                        statusBuilder.Append($"[GPU {index}] {percent:F1}% ({usedGB:F2} / {totalGB:F2} GB)");
                    }

                    uint processCount = 0;
                    var result = NvmlApi.DeviceGetGraphicsRunningProcesses(handle, ref processCount, null);

                    if (result == NvmlApi.NvmlReturn.Success || result == NvmlApi.NvmlReturn.ErrorInsufficientSize)
                    {
                        if (processCount > 0)
                        {
                            var processInfos = new NvmlApi.NvmlProcessInfo[processCount];
                            if (NvmlApi.DeviceGetGraphicsRunningProcesses(handle, ref processCount, processInfos) == NvmlApi.NvmlReturn.Success)
                            {
                                foreach (var info in processInfos)
                                {
                                    if (!pidDataMap.ContainsKey(info.pid))
                                    {
                                        pidDataMap[info.pid] = (0, new HashSet<string>(), false);
                                    }

                                    var entry = pidDataMap[info.pid];
                                    entry.EngineStrings.Add($"GPU {index} - 3D");

                                    if (info.usedGpuMemory != NvmlApi.NVML_VALUE_NOT_AVAILABLE && info.usedGpuMemory > 0)
                                    {
                                        entry.TotalVram += info.usedGpuMemory;
                                    }
                                    else
                                    {
                                        entry.UsedPerfCounter = true;
                                    }

                                    pidDataMap[info.pid] = entry;
                                }
                            }
                        }
                    }
                }

                _dispatcherQueue.TryEnqueue(() =>
                {
                    TotalVramStatus = statusBuilder.ToString();
                });

                var newProcessList = new List<GpuProcess>();

                if (pidDataMap.Count == 0)
                {
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        _allProcesses.Clear();
                        RefreshDisplay();
                        // [修改] 如果列表为空，依然要更新底部的状态栏时间
                        UpdateStatusMessage();
                    });
                    return;
                }

                foreach (var kvp in pidDataMap)
                {
                    uint pid = kvp.Key;
                    var (totalVram, engines, needPerfCounter) = kvp.Value;

                    if (needPerfCounter || totalVram == 0)
                    {
                        ulong perfValue = _perfHelper.GetDedicatedVramUsage(pid);
                        if (perfValue > 0) totalVram = perfValue;
                        else if (totalVram == 0 && needPerfCounter) totalVram = NvmlApi.NVML_VALUE_NOT_AVAILABLE;
                    }

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
                        VramUsageBytes = totalVram,
                        IsEfficiencyMode = isEfficiency,
                        GpuEngine = string.Join(", ", engines)
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
                    // [修改] 更新状态栏
                    UpdateStatusMessage();
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"发生错误: {ex.Message}";
            }
        }

        private async Task LoadProcessMetadataAsync(uint pid)
        {
            var meta = await ProcessInfoHelper.GetMetadataAsync(pid);
            lock (_metadataCache)
            {
                if (!_metadataCache.ContainsKey(pid))
                    _metadataCache[pid] = meta;
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

                    RefreshDisplay();
                }
            });
        }

        private void MergeProcessData(List<GpuProcess> newProcesses)
        {
            var orderedNew = newProcesses.OrderByDescending(p => p.VramUsageBytes).ToList();
            bool listChanged = false;

            var pidsToRemove = _allProcesses.Select(p => p.Pid)
                                            .Except(orderedNew.Select(p => p.Pid))
                                            .ToList();
            if (pidsToRemove.Count > 0) listChanged = true;

            foreach (var pid in pidsToRemove)
            {
                var processToRemove = _allProcesses.FirstOrDefault(p => p.Pid == pid);
                if (processToRemove != null)
                {
                    _allProcesses.Remove(processToRemove);
                    lock (_metadataCache) { _metadataCache.Remove(pid); }
                }
            }

            foreach (var newProc in orderedNew)
            {
                var existingProc = _allProcesses.FirstOrDefault(p => p.Pid == newProc.Pid);
                if (existingProc != null)
                {
                    existingProc.VramUsageBytes = newProc.VramUsageBytes;
                    existingProc.IsEfficiencyMode = newProc.IsEfficiencyMode;
                    existingProc.GpuEngine = newProc.GpuEngine;

                    if (!newProc.Name.Contains("(加载中...)"))
                    {
                        if (existingProc.Name != newProc.Name) listChanged = true;

                        existingProc.Name = newProc.Name;
                        existingProc.Icon = newProc.Icon;
                        existingProc.FullPath = newProc.FullPath;
                        existingProc.Publisher = newProc.Publisher;
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

        public void Cleanup()
        {
            _timer?.Stop();
            _perfHelper?.Cleanup();
            if (_isInitialized)
            {
                NvmlApi.Shutdown();
                _isInitialized = false;
            }
            SettingsManager.ScanIntervalChanged -= OnScanIntervalChanged;
        }
    }
}