using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Diagnostics;
using VRAMonitor.Nvidia;

namespace VRAMonitor.Services.Providers
{
    /// <summary>
    /// 简化版 NVIDIA 提供者：完全不依赖 DXGI
    /// 使用 NVML 获取 N 卡信息，使用纯 WDDM 获取其他 GPU
    /// </summary>
    public class NvidiaTelemetryProvider : IGpuTelemetryProvider
    {
        private List<(int Index, IntPtr Handle, string Name)> _nvDevices = new();
        private bool _isInitialized = false;

        // 备用 WDDM 提供者（用于获取非 N 卡和共享显存信息）
        private WddmTelemetryProvider _fallbackProvider;

        public string ProviderName => "NVIDIA NVML + WDDM (Hybrid, No DXGI)";
        public bool IsSupported => _isInitialized && _nvDevices.Count > 0;

        public int GpuCount => GetGpuStatuses().Count;

        public NvidiaTelemetryProvider()
        {
            Initialize();
            _fallbackProvider = new WddmTelemetryProvider();
        }

        private void Initialize()
        {
            try
            {
                var result = NvmlApi.Init();
                if (result != NvmlApi.NvmlReturn.Success)
                {
                    Debug.WriteLine($"NVML Init failed: {result}");
                    return;
                }

                uint deviceCount = 0;
                NvmlApi.DeviceGetCount(ref deviceCount);

                if (deviceCount == 0)
                {
                    Debug.WriteLine("No NVIDIA devices found");
                    return;
                }

                var nameBuffer = new StringBuilder(256);
                for (uint i = 0; i < deviceCount; i++)
                {
                    if (NvmlApi.DeviceGetHandleByIndex(i, out IntPtr handle) == NvmlApi.NvmlReturn.Success)
                    {
                        NvmlApi.DeviceGetName(handle, nameBuffer, (uint)nameBuffer.Capacity);
                        _nvDevices.Add(((int)i, handle, nameBuffer.ToString()));
                        Debug.WriteLine($"Found NVIDIA GPU {i}: {nameBuffer}");
                    }
                }

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NVML initialization error: {ex.Message}");
                _isInitialized = false;
            }
        }

        public string GetGpuName()
        {
            var statuses = GetGpuStatuses();
            if (statuses.Count > 0)
            {
                return string.Join(" | ", statuses.Select(s => s.Name));
            }
            return "Unknown GPU";
        }

        public void Refresh()
        {
            _fallbackProvider?.Refresh();
        }

        public List<GpuStatusInfo> GetGpuStatuses()
        {
            var result = new List<GpuStatusInfo>();

            // 1. 获取 WDDM 的完整 GPU 列表（包含所有显卡）
            var wddmGpus = _fallbackProvider?.GetGpuStatuses() ?? new List<GpuStatusInfo>();

            Debug.WriteLine($"WDDM found {wddmGpus.Count} GPUs");

            // 2. 标记哪些 WDDM GPU 已被 NVML 处理
            var matchedIndices = new HashSet<int>();

            // 3. 遍历 NVML 设备，用更精确的数据覆盖 WDDM 的对应项
            if (_isInitialized)
            {
                foreach (var (index, handle, nvName) in _nvDevices)
                {
                    var status = new GpuStatusInfo { Name = nvName };

                    // [NVML] 核心利用率（WDDM 无法提供）
                    if (NvmlApi.DeviceGetUtilizationRates(handle, out var util) == NvmlApi.NvmlReturn.Success)
                    {
                        status.CoreLoad = util.gpu;
                    }

                    // [NVML] 专用显存（比 WDDM 更准确）
                    if (NvmlApi.DeviceGetMemoryInfo(handle, out var mem) == NvmlApi.NvmlReturn.Success)
                    {
                        status.DedicatedUsed = mem.used;
                        status.DedicatedTotal = mem.total;
                    }

                    // [匹配 WDDM] 找到对应的 WDDM GPU 以获取共享显存
                    int bestMatch = -1;
                    for (int i = 0; i < wddmGpus.Count; i++)
                    {
                        if (matchedIndices.Contains(i)) continue;

                        var wddm = wddmGpus[i];

                        // 匹配策略：名称包含 NVIDIA 或相似
                        if (wddm.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nvName.IndexOf(wddm.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            wddm.Name.IndexOf(nvName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            bestMatch = i;
                            Debug.WriteLine($"Matched NVML GPU {index} with WDDM GPU {i}");
                            break;
                        }
                    }

                    // 使用 WDDM 的共享显存数据
                    if (bestMatch >= 0)
                    {
                        var matched = wddmGpus[bestMatch];
                        matchedIndices.Add(bestMatch);

                        status.SharedTotal = matched.SharedTotal;
                        status.SharedUsed = matched.SharedUsed;
                    }
                    else
                    {
                        // 如果没有匹配到，使用默认值
                        Debug.WriteLine($"No WDDM match for NVML GPU {index}, using defaults");
                        status.SharedTotal = 16UL * 1024 * 1024 * 1024; // 16GB 默认
                        status.SharedUsed = 0;
                    }

                    result.Add(status);
                }
            }

            // 4. 添加未被 NVML 匹配的 WDDM GPU（核显、AMD 等）
            for (int i = 0; i < wddmGpus.Count; i++)
            {
                if (!matchedIndices.Contains(i))
                {
                    Debug.WriteLine($"Adding unmatched WDDM GPU {i}: {wddmGpus[i].Name}");
                    result.Add(wddmGpus[i]);
                }
            }

            Debug.WriteLine($"Total GPUs returned: {result.Count}");
            return result;
        }

        public Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage()
        {
            var result = new Dictionary<uint, (ulong Vram, string Engine)>();

            // 1. 底板：WDDM 数据（涵盖所有 GPU 进程）
            if (_fallbackProvider != null)
            {
                var wddmData = _fallbackProvider.GetProcessVramUsage();
                foreach (var kvp in wddmData)
                {
                    result[kvp.Key] = (kvp.Value.Vram, "GPU (Shared/Other)");
                }
            }

            // 2. 覆盖：NVML 数据（N 卡进程）
            var engineMap = new Dictionary<uint, HashSet<string>>();

            if (_isInitialized)
            {
                foreach (var (index, handle, _) in _nvDevices)
                {
                    uint count = 0;
                    var ret = NvmlApi.DeviceGetGraphicsRunningProcesses(handle, ref count, null);

                    if (ret == NvmlApi.NvmlReturn.Success || ret == NvmlApi.NvmlReturn.ErrorInsufficientSize)
                    {
                        if (count > 0)
                        {
                            var infos = new NvmlApi.NvmlProcessInfo[count * 2];
                            count = (uint)infos.Length;

                            if (NvmlApi.DeviceGetGraphicsRunningProcesses(handle, ref count, infos) == NvmlApi.NvmlReturn.Success)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    var info = infos[i];
                                    if (info.pid == 0) continue;

                                    ulong usage = (info.usedGpuMemory == NvmlApi.NVML_VALUE_NOT_AVAILABLE)
                                        ? 0
                                        : info.usedGpuMemory;

                                    // 如果 NVML 报告为 0，回退到 WDDM
                                    if (usage == 0 && result.TryGetValue(info.pid, out var existing))
                                    {
                                        usage = existing.Vram;
                                    }

                                    string engineStr = $"GPU {index} - 3D";

                                    if (!engineMap.ContainsKey(info.pid))
                                    {
                                        engineMap[info.pid] = new HashSet<string>();
                                        result[info.pid] = (0, "");
                                    }

                                    var current = result[info.pid];
                                    result[info.pid] = (current.Vram + usage, "");
                                    engineMap[info.pid].Add(engineStr);
                                }
                            }
                        }
                    }
                }
            }

            // 3. 设置引擎显示字符串
            var keys = result.Keys.ToList();
            foreach (var pid in keys)
            {
                if (engineMap.TryGetValue(pid, out var engines))
                {
                    result[pid] = (result[pid].Vram, string.Join(", ", engines));
                }
            }

            return result;
        }

        public void Dispose()
        {
            if (_isInitialized)
            {
                try { NvmlApi.Shutdown(); } catch { }
                _isInitialized = false;
            }
            _fallbackProvider?.Dispose();
        }
    }
}