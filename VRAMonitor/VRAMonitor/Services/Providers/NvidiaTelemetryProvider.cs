using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VRAMonitor.Nvidia;

namespace VRAMonitor.Services.Providers
{
    /// <summary>
    /// 混合提供者：
    /// 1. 使用 WddmTelemetryProvider 作为基底，确保获取到正确的全局 GPU 索引和核显信息。
    /// 2. 使用 NVML "增强" NVIDIA 显卡的数据（提供更准确的显存读数和核心利用率）。
    /// </summary>
    public class NvidiaTelemetryProvider : IGpuTelemetryProvider
    {
        // 基底提供者 (负责 DXGI 枚举、核显数据、进程 VRAM)
        private readonly WddmTelemetryProvider _baseProvider;

        // NVML 设备句柄缓存
        private readonly Dictionary<int, IntPtr> _nvmlHandleCache = new();
        private bool _isNvmlInitialized = false;

        public string ProviderName => "NVIDIA NVML + WDDM (Unified Index)";

        // 只要基底支持，或者 NVML 初始化成功，就认为支持
        public bool IsSupported => _baseProvider.IsSupported || _isNvmlInitialized;

        public int GpuCount => _baseProvider.GpuCount;

        public NvidiaTelemetryProvider()
        {
            // 1. 初始化基底 (WDDM/DXGI)
            _baseProvider = new WddmTelemetryProvider();

            // 2. 初始化 NVML
            InitializeNvml();
        }

        private void InitializeNvml()
        {
            try
            {
                var result = NvmlApi.Init();
                if (result == NvmlApi.NvmlReturn.Success)
                {
                    _isNvmlInitialized = true;
                    // 尝试预匹配 NVML 设备到 DXGI 索引
                    MapNvmlDevices();
                }
                else
                {
                    Debug.WriteLine($"[NvidiaProvider] NVML Init failed: {result}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NvidiaProvider] NVML Load Exception: {ex.Message}");
            }
        }

        private void MapNvmlDevices()
        {
            if (!_isNvmlInitialized) return;

            _nvmlHandleCache.Clear();

            // 获取基底的所有 GPU (带有正确的 Index)
            var wddmGpus = _baseProvider.GetGpuStatuses();

            // 获取 NVML 设备数量
            uint nvCount = 0;
            NvmlApi.DeviceGetCount(ref nvCount);

            // 简单匹配逻辑：
            // 遍历 WDDM 列表，如果是 NVIDIA 显卡，则尝试按顺序分配 NVML 句柄。
            // 注意：这在多 N 卡环境下可能不准确（需要对比 PCI Bus ID），
            // 但对于 "核显 + 1张独显" 的笔记本/台式机环境，这种顺序匹配通常是正确的。

            uint nvIndex = 0;
            foreach (var gpu in wddmGpus)
            {
                if (IsNvidiaCard(gpu.Name))
                {
                    if (nvIndex < nvCount)
                    {
                        IntPtr handle;
                        var ret = NvmlApi.DeviceGetHandleByIndex(nvIndex, out handle);
                        if (ret == NvmlApi.NvmlReturn.Success)
                        {
                            _nvmlHandleCache[gpu.Index] = handle;
                            Debug.WriteLine($"[NvidiaProvider] Mapped DXGI GPU {gpu.Index} ({gpu.Name}) to NVML Device {nvIndex}");
                        }
                        nvIndex++;
                    }
                }
            }
        }

        private bool IsNvidiaCard(string name)
        {
            return name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("Quadro", StringComparison.OrdinalIgnoreCase);
        }

        public string GetGpuName() => _baseProvider.GetGpuName();

        public void Refresh()
        {
            // 刷新基底 (处理插拔等)
            _baseProvider.Refresh();

            // 如果 GPU 数量变化，可能需要重新映射 NVML (简化起见暂不重置 NVML)
        }

        public List<GpuStatusInfo> GetGpuStatuses()
        {
            // 1. 获取基底数据 (WDDM)
            // 这确保了我们有核显的数据，且 Index 是正确的
            var statuses = _baseProvider.GetGpuStatuses();

            // 2. 使用 NVML 数据“修补” NVIDIA 显卡的状态
            if (_isNvmlInitialized)
            {
                for (int i = 0; i < statuses.Count; i++)
                {
                    var status = statuses[i];

                    // 如果我们缓存了这个 Index 对应的 NVML 句柄
                    if (_nvmlHandleCache.TryGetValue(status.Index, out IntPtr handle))
                    {
                        try
                        {
                            // A. 获取显存信息
                            NvmlApi.NvmlMemory memoryInfo;
                            if (NvmlApi.DeviceGetMemoryInfo(handle, out memoryInfo) == NvmlApi.NvmlReturn.Success)
                            {
                                // 覆盖 WDDM 的显存数据 (NVML 通常更准)
                                status.DedicatedUsed = memoryInfo.used;
                                status.DedicatedTotal = memoryInfo.total; // 也可以保留 WDDM 的 Total，看哪个更准，通常 NVML Total 包含预留区
                            }

                            // B. 获取核心利用率
                            NvmlApi.NvmlUtilization utilization;
                            if (NvmlApi.DeviceGetUtilizationRates(handle, out utilization) == NvmlApi.NvmlReturn.Success)
                            {
                                status.CoreLoad = utilization.gpu; // NVML 返回的是 0-100 的整数
                            }

                            // C. (未来扩展) 在这里可以获取温度、功耗、风扇转速
                            // float temp = 0;
                            // NvmlApi.DeviceGetTemperature(handle, 0, ref temp);

                            // 将修改后的结构体放回列表
                            statuses[i] = status;
                        }
                        catch
                        {
                            // 如果 NVML 读取失败，保持 WDDM 数据不变
                        }
                    }
                }
            }

            return statuses;
        }

        public Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage()
        {
            // 对于进程级统计，WDDM 的性能计数器通常已经足够好，且能覆盖所有显卡（包括核显）。
            // NVML 的 DeviceGetComputeRunningProcesses 通常只包含 CUDA/计算进程，
            // DeviceGetGraphicsRunningProcesses 也不一定全。
            // 为了保持统一和稳定，直接使用 WDDM 的进程数据。
            // (且我们已经在 WddmTelemetryProvider 里实现了 "GPU 0 - 3D" 的引擎名映射)

            return _baseProvider.GetProcessVramUsage();
        }

        public void Dispose()
        {
            _baseProvider?.Dispose();

            if (_isNvmlInitialized)
            {
                try { NvmlApi.Shutdown(); } catch { }
                _isNvmlInitialized = false;
            }
        }
    }
}