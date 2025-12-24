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

        public string ProviderName => "Windows WDDM (DXGI + Dedup)";
        public bool IsSupported => _isSupported;
        public int GpuCount => _adapters.Count;

        private class GpuAdapter
        {
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
                _isSupported = true; // Still allow static info
            }
        }

        private void InitializeAdapters()
        {
            _adapters.Clear();

            var dxgiGpus = GpuInfoHelper.GetAllGpus();
            string[] instanceNames = Array.Empty<string>();
            try
            {
                if (_adapterCategory != null) instanceNames = _adapterCategory.GetInstanceNames();
            }
            catch { }

            var luidRegex = new Regex(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)_phys_0", RegexOptions.IgnoreCase);

            // --- 步骤 1: 扫描所有 GPU，找出哪些能匹配到计数器 ---
            // 这是一个预处理步骤，用于构建 "已知有效 GPU 名称" 的集合
            HashSet<string> validGpuNames = new();
            Dictionary<long, string> luidToInstance = new();

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
                                validGpuNames.Add(gpu.Name); // 标记此名称的 GPU 是真实存在的
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            // --- 步骤 2: 创建适配器对象并去重 ---
            HashSet<string> addedFallbackNames = new(); // 用于防止重复添加无效的 Ghost 显卡

            foreach (var gpuInfo in dxgiGpus)
            {
                string matchedInstance = null;
                luidToInstance.TryGetValue(gpuInfo.Luid, out matchedInstance);

                // [去重逻辑 A] 如果这个显卡没有匹配到计数器，且它的名字在 "有效名单" 里
                // 说明这是一个同名的 Ghost 节点（例如 890M 的计算节点），我们应该忽略它
                if (matchedInstance == null && validGpuNames.Contains(gpuInfo.Name))
                {
                    Debug.WriteLine($"[WDDM] Skipping ghost adapter: {gpuInfo.Name} (LUID: 0x{gpuInfo.Luid:X})");
                    continue;
                }

                // [去重逻辑 B] 如果这个显卡没有匹配到计数器，且名字也没在有效名单里（可能是权限问题全挂了）
                // 我们只添加第一个，后续同名的都忽略，避免列表里出现 3 个无法监控的 890M
                if (matchedInstance == null)
                {
                    if (addedFallbackNames.Contains(gpuInfo.Name))
                    {
                        continue;
                    }
                    addedFallbackNames.Add(gpuInfo.Name);
                }

                // 创建适配器
                var adapter = new GpuAdapter
                {
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
                    catch { }
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

        // [修复] 补回 CreateProcessCounter
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

                foreach (var kvp in _pidToInstanceMap)
                {
                    uint pid = kvp.Key;
                    ulong total = 0;
                    foreach (var instance in kvp.Value)
                    {
                        total += ReadProcessCounter(instance, "Dedicated Usage");
                    }
                    if (total > 0) result[pid] = (total, "GPU (WDDM)");
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