using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace VRAMonitor.Services.Providers
{
    [SupportedOSPlatform("windows")]
    public class WddmTelemetryProvider : IGpuTelemetryProvider
    {
        private PerformanceCounterCategory _category;
        private readonly string _categoryName;
        private Dictionary<string, PerformanceCounter> _counters = new();
        private Dictionary<uint, List<string>> _pidToInstanceMap = new();
        private bool _isSupported = false;
        private string _cachedGpuName = "Generic WDDM GPU";

        private readonly object _lock = new();

        public string ProviderName => "Windows WDDM";
        public bool IsSupported => _isSupported;

        public int GpuCount => 1;

        public WddmTelemetryProvider()
        {
            string[] possibleNames = { "GPU Process Memory", "GPU 进程内存" };
            foreach (var name in possibleNames)
            {
                if (PerformanceCounterCategory.Exists(name))
                {
                    _categoryName = name;
                    _category = new PerformanceCounterCategory(_categoryName);
                    _isSupported = true;
                    break;
                }
            }

            if (_isSupported) DetectGpuNameViaUser32();
        }

        private void DetectGpuNameViaUser32()
        {
            try
            {
                var device = new DISPLAY_DEVICE();
                device.cb = Marshal.SizeOf(device);
                var sb = new StringBuilder();
                uint id = 0;

                // [新增] 用于去重的集合
                var seenNames = new HashSet<string>();

                // [新增] 过滤关键词列表 (过滤虚拟显卡、USB显卡、远程桌面驱动等)
                var ignoredKeywords = new[]
                {
                    "Microsoft Remote Display Adapter",
                    "Microsoft Basic Render Driver",
                    "Lebo",
                    "Virtual",
                    "GameViewer",
                    "USB Monitor",
                    "TeamViewer",
                    "Radmin",
                    "Citrix",
                    "VNC",
                    "AnyDesk",
                    "Spacedesk",
                    "Idda" // Indirect Display Driver Adapter
                };

                while (EnumDisplayDevices(null, id, ref device, 0))
                {
                    string name = device.DeviceString;

                    if (!string.IsNullOrEmpty(name))
                    {
                        // 1. 去重检查
                        if (!seenNames.Contains(name))
                        {
                            // 2. 关键词过滤检查
                            bool isVirtual = ignoredKeywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!isVirtual)
                            {
                                if (sb.Length > 0) sb.Append(" | ");
                                sb.Append(name);
                                seenNames.Add(name);
                            }
                        }
                    }

                    device.cb = Marshal.SizeOf(device); // 重置大小
                    id++;
                }

                if (sb.Length > 0)
                {
                    _cachedGpuName = sb.ToString();
                }
            }
            catch
            {
                _cachedGpuName = "Generic Graphics Device";
            }
        }

        public string GetGpuName() => _cachedGpuName;

        public string GetTotalVramStatus() => "监控模式: Windows 通用计数器 (WDDM)";

        public void Refresh()
        {
            if (!_isSupported) return;

            if (Monitor.TryEnter(_lock))
            {
                try
                {
                    var instanceNames = _category.GetInstanceNames();
                    _pidToInstanceMap.Clear();
                    var activeInstances = new HashSet<string>();

                    foreach (var name in instanceNames)
                    {
                        var parts = name.Split('_');
                        if (parts.Length > 1 && parts[0] == "pid" && uint.TryParse(parts[1], out uint pid))
                        {
                            if (!_pidToInstanceMap.ContainsKey(pid)) _pidToInstanceMap[pid] = new List<string>();
                            _pidToInstanceMap[pid].Add(name);
                            activeInstances.Add(name);
                        }
                    }

                    var deadKeys = _counters.Keys.Where(k => !activeInstances.Any(inst => k.StartsWith(inst))).ToList();
                    foreach (var key in deadKeys)
                    {
                        if (_counters.TryGetValue(key, out var c)) { c.Dispose(); _counters.Remove(key); }
                    }
                }
                catch { }
                finally { Monitor.Exit(_lock); }
            }
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
                    ulong totalBytes = 0;
                    foreach (var instanceName in kvp.Value)
                    {
                        totalBytes += ReadCounter(instanceName, "Dedicated Usage");
                    }

                    if (totalBytes > 0)
                    {
                        result[pid] = (totalBytes, "GPU - 3D/Copy");
                    }
                }
                return result;
            }
        }

        private ulong ReadCounter(string instanceName, string counterName)
        {
            string key = $"{instanceName}_{counterName}";
            if (!_counters.TryGetValue(key, out var counter))
            {
                try { counter = new PerformanceCounter(_categoryName, counterName, instanceName); _counters[key] = counter; }
                catch { return 0; }
            }
            try { return (ulong)counter.NextValue(); } catch { return 0; }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var c in _counters.Values) c.Dispose();
                _counters.Clear();
            }
        }

        #region P/Invoke
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct DISPLAY_DEVICE
        {
            [MarshalAs(UnmanagedType.U4)] public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            [MarshalAs(UnmanagedType.U4)] public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        #endregion
    }
}