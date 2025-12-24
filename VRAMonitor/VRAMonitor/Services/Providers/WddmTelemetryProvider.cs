using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using VRAMonitor.Services.Native;

namespace VRAMonitor.Services.Providers
{
    [SupportedOSPlatform("windows")]
    public class WddmTelemetryProvider : IGpuTelemetryProvider
    {
        private PerformanceCounterCategory _processCategory;
        private Dictionary<string, PerformanceCounter> _processCounters = new();
        private Dictionary<uint, List<string>> _pidToInstanceMap = new();

        private PerformanceCounterCategory _adapterCategory;
        private List<GpuAdapter> _adapters = new();
        private string _cachedGpuName = "Generic WDDM GPU";
        private bool _isSupported = false;
        private readonly object _lock = new();

        public string ProviderName => "Windows WDDM (Unified Index)";
        public bool IsSupported => _isSupported;
        public int GpuCount => _adapters.Count;

        private class GpuAdapter
        {
            // [新增] 全局统一索引
            public int Index { get; set; }
            public string Name { get; set; }
            public long Luid { get; set; }
            public string InstanceName { get; set; }
            public ulong DedicatedTotal { get; set; }
            public ulong SharedTotal { get; set; }
            public PerformanceCounter DedicatedCounter { get; set; }
            public PerformanceCounter SharedCounter { get; set; }
        }

        public WddmTelemetryProvider()
        {
            try
            {
                // 尝试初始化计数器类别
                string[] procNames = { "GPU Process Memory", "GPU 进程内存" };
                foreach (var name in procNames)
                    if (PerformanceCounterCategory.Exists(name)) { _processCategory = new PerformanceCounterCategory(name); _isSupported = true; break; }

                string[] adapterNames = { "GPU Adapter Memory", "GPU 适配器内存" };
                foreach (var name in adapterNames)
                    if (PerformanceCounterCategory.Exists(name)) { _adapterCategory = new PerformanceCounterCategory(name); break; }

                InitializeAdapters();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WddmTelemetryProvider init error: {ex.Message}");
                // 即使计数器初始化失败，DXGI 依然可以工作
                _isSupported = true;
            }
        }

        private void InitializeAdapters()
        {
            _adapters.Clear();

            // 1. 获取物理 GPU 信息 (包含 Index 和准确的 VRAM 大小)
            var dxgiGpus = GpuInfoHelper.GetAllGpus();

            // 2. 获取性能计数器实例名称
            string[] instanceNames = Array.Empty<string>();
            try
            {
                if (_adapterCategory != null) instanceNames = _adapterCategory.GetInstanceNames();
            }
            catch { }

            var luidRegex = new Regex(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)_phys_0", RegexOptions.IgnoreCase);

            // 构建 LUID -> 实例名 的映射
            // 同时记录哪些显卡名称是“有效”的（即匹配到了计数器）
            Dictionary<long, string> luidToInstance = new();
            HashSet<string> validGpuNames = new();

            foreach (var gpu in dxgiGpus)
            {
                foreach (var instance in instanceNames)
                {
                    if (!instance.Contains("phys_0")) continue;
                    var match = luidRegex.Match(instance);
                    if (match.Success)
                    {
                        try
                        {
                            long high = Convert.ToInt64(match.Groups[1].Value, 16);
                            long low = Convert.ToInt64(match.Groups[2].Value, 16);
                            long instLuid = (high << 32) | low;

                            if (instLuid == gpu.Luid)
                            {
                                luidToInstance[gpu.Luid] = instance;
                                validGpuNames.Add(gpu.Name);
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            // [去重逻辑]
            // 防止重复添加无法监控的“幽灵”显卡（如 AMD 890M 的多余节点）
            HashSet<string> addedFallbackNames = new();

            foreach (var gpuInfo in dxgiGpus)
            {
                string matchedInstance = null;
                luidToInstance.TryGetValue(gpuInfo.Luid, out matchedInstance);

                // Case A: 这是一个幽灵节点（没有匹配计数器），但在系统里已经有同名的有效节点了
                // -> 忽略它，因为它只是驱动暴露的计算/链接接口，不是主显示接口
                if (matchedInstance == null && validGpuNames.Contains(gpuInfo.Name))
                {
                    Debug.WriteLine($"[WDDM] Skipping ghost adapter: {gpuInfo.Name} (LUID: 0x{gpuInfo.Luid:X})");
                    continue;
                }

                // Case B: 这是一个无效节点，且之前也没添加过同名的节点（可能是全都不支持计数器，或权限受限）
                // -> 只添加第一个作为 Fallback，防止列表显示 3 个同样的卡
                if (matchedInstance == null)
                {
                    if (addedFallbackNames.Contains(gpuInfo.Name)) continue;
                    addedFallbackNames.Add(gpuInfo.Name);
                }

                var adapter = new GpuAdapter
                {
                    Index = gpuInfo.Index, // [重要] 传递 DXGI 统一索引
                    Name = gpuInfo.Name,
                    Luid = gpuInfo.Luid,
                    InstanceName = matchedInstance,
                    DedicatedTotal = gpuInfo.DedicatedMemory,
                    SharedTotal = gpuInfo.SharedMemory
                };

                if (matchedInstance != null)
                {
                    try
                    {
                        adapter.DedicatedCounter = CreateCounter("Dedicated Usage", matchedInstance);
                        adapter.SharedCounter = CreateCounter("Shared Usage", matchedInstance);
                        adapter.DedicatedCounter?.NextValue();
                        adapter.SharedCounter?.NextValue();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to init counters for {gpuInfo.Name}: {ex.Message}");
                    }
                }

                _adapters.Add(adapter);
            }

            if (_adapters.Count > 0)
                _cachedGpuName = string.Join(" | ", _adapters.Select(a => a.Name));
        }

        private PerformanceCounter CreateCounter(string englishName, string instanceName)
        {
            try
            {
                return new PerformanceCounter(_adapterCategory.CategoryName, englishName, instanceName);
            }
            catch
            {
                try
                {
                    string cnName = englishName switch
                    {
                        "Dedicated Usage" => "专用使用量",
                        "Shared Usage" => "共享使用量",
                        _ => englishName
                    };
                    return new PerformanceCounter(_adapterCategory.CategoryName, cnName, instanceName);
                }
                catch
                {
                    return null;
                }
            }
        }

        private PerformanceCounter CreateProcessCounter(string englishName, string instanceName)
        {
            if (_processCategory == null) return null;
            try
            {
                return new PerformanceCounter(_processCategory.CategoryName, englishName, instanceName);
            }
            catch
            {
                try
                {
                    string cnName = englishName == "Dedicated Usage" ? "专用使用量" : "共享使用量";
                    return new PerformanceCounter(_processCategory.CategoryName, cnName, instanceName);
                }
                catch { return null; }
            }
        }

        public string GetGpuName() => _cachedGpuName;

        public void Refresh()
        {
            if (!_isSupported) return;
            if (Monitor.TryEnter(_lock))
            {
                try
                {
                    if (_processCategory != null)
                    {
                        var instanceNames = _processCategory.GetInstanceNames();
                        _pidToInstanceMap.Clear();

                        foreach (var name in instanceNames)
                        {
                            if (name.StartsWith("pid_"))
                            {
                                var parts = name.Split('_');
                                if (parts.Length > 1 && uint.TryParse(parts[1], out uint pid))
                                {
                                    if (!_pidToInstanceMap.ContainsKey(pid))
                                        _pidToInstanceMap[pid] = new List<string>();
                                    _pidToInstanceMap[pid].Add(name);
                                }
                            }
                        }
                    }
                }
                catch { }
                finally { Monitor.Exit(_lock); }
            }
        }

        public List<GpuStatusInfo> GetGpuStatuses()
        {
            var result = new List<GpuStatusInfo>();
            foreach (var adapter in _adapters)
            {
                var info = new GpuStatusInfo
                {
                    Index = adapter.Index, // [重要] 传递 Index
                    Name = adapter.Name,
                    DedicatedTotal = adapter.DedicatedTotal,
                    SharedTotal = adapter.SharedTotal,
                    CoreLoad = 0
                };

                try
                {
                    if (adapter.DedicatedCounter != null) info.DedicatedUsed = (ulong)adapter.DedicatedCounter.NextValue();
                    if (adapter.SharedCounter != null) info.SharedUsed = (ulong)adapter.SharedCounter.NextValue();
                }
                catch { }

                result.Add(info);
            }
            return result;
        }

        public Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage()
        {
            lock (_lock)
            {
                var result = new Dictionary<uint, (ulong Vram, string Engine)>();
                if (!_isSupported) return result;

                var luidRegex = new Regex(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)_phys_0", RegexOptions.IgnoreCase);

                foreach (var kvp in _pidToInstanceMap)
                {
                    uint pid = kvp.Key;
                    ulong totalVram = 0;
                    HashSet<string> engines = new();

                    foreach (var instance in kvp.Value)
                    {
                        ulong usage = ReadProcessCounter(instance, "Dedicated Usage");
                        if (usage > 0)
                        {
                            totalVram += usage;

                            // [新增] 尝试解析进程所在的 GPU Index
                            // 格式: pid_1234_luid_0x...
                            var match = luidRegex.Match(instance);
                            if (match.Success)
                            {
                                try
                                {
                                    long high = Convert.ToInt64(match.Groups[1].Value, 16);
                                    long low = Convert.ToInt64(match.Groups[2].Value, 16);
                                    long instLuid = (high << 32) | low;

                                    // 查找 LUID 对应的 Adapter 以获取 Index
                                    var adapter = _adapters.FirstOrDefault(a => a.Luid == instLuid);
                                    if (adapter != null)
                                    {
                                        engines.Add($"GPU {adapter.Index}"); // 简化显示为 "GPU 0", "GPU 1"
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    if (totalVram > 0)
                    {
                        string engineStr = engines.Count > 0 ? string.Join(", ", engines) : "GPU";
                        result[pid] = (totalVram, engineStr);
                    }
                }
                return result;
            }
        }

        private ulong ReadProcessCounter(string instance, string counterName)
        {
            string key = $"{instance}_{counterName}";
            if (!_processCounters.TryGetValue(key, out var counter))
            {
                counter = CreateProcessCounter(counterName, instance);
                if (counter != null) _processCounters[key] = counter;
                else return 0;
            }
            try { return (ulong)counter.NextValue(); } catch { return 0; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var c in _processCounters.Values) c.Dispose();
                _processCounters.Clear();
                foreach (var a in _adapters) { a.DedicatedCounter?.Dispose(); a.SharedCounter?.Dispose(); }
                _adapters.Clear();
            }
        }
    }
}