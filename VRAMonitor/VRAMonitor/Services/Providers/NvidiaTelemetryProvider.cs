using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using VRAMonitor.Nvidia; // 引用根目录下的 NvmlApi

namespace VRAMonitor.Services.Providers
{
    public class NvidiaTelemetryProvider : IGpuTelemetryProvider
    {
        private List<(int Index, IntPtr Handle, string Name)> _gpuDevices = new();
        private bool _isInitialized = false;

        // 备用提供者 (WDDM)，用于获取核显数据或补充 NVML 缺失的数据
        private WddmTelemetryProvider _fallbackProvider;

        public string ProviderName => "NVIDIA NVML + WDDM (Hybrid)";
        public bool IsSupported => _isInitialized && _gpuDevices.Count > 0;

        // 返回 NVML 检测到的独显数量，+1 代表潜在的核显 (虽然不精确，但在状态栏显示够用了)
        public int GpuCount => _gpuDevices.Count + (_fallbackProvider != null ? 1 : 0);

        public NvidiaTelemetryProvider()
        {
            Initialize();
            // 初始化 WDDM 提供者
            _fallbackProvider = new WddmTelemetryProvider();
        }

        private void Initialize()
        {
            try
            {
                var result = NvmlApi.Init();
                if (result != NvmlApi.NvmlReturn.Success) return;

                uint deviceCount = 0;
                NvmlApi.DeviceGetCount(ref deviceCount);

                if (deviceCount == 0) return;

                var nameBuffer = new StringBuilder(256);
                for (uint i = 0; i < deviceCount; i++)
                {
                    if (NvmlApi.DeviceGetHandleByIndex(i, out IntPtr handle) == NvmlApi.NvmlReturn.Success)
                    {
                        NvmlApi.DeviceGetName(handle, nameBuffer, (uint)nameBuffer.Capacity);
                        _gpuDevices.Add(((int)i, handle, nameBuffer.ToString()));
                    }
                }

                _isInitialized = true;
            }
            catch
            {
                _isInitialized = false;
            }
        }

        public string GetGpuName()
        {
            // [修改] 优先尝试使用 WDDM 获取的系统所有显卡名称 (例如 "AMD Radeon(TM) Graphics | NVIDIA GeForce RTX 3060")
            // 这样能确保 UI 头部显示出 AMD 核显
            if (_fallbackProvider != null && _fallbackProvider.IsSupported)
            {
                string wddmName = _fallbackProvider.GetGpuName();
                // 过滤掉失败时的通用名称，确保拿到的是真实硬件名
                if (!string.IsNullOrEmpty(wddmName) && !wddmName.Contains("Generic") && !wddmName.Contains("未知"))
                {
                    return wddmName;
                }
            }

            // 如果 WDDM 获取失败，回退到只显示 NVIDIA
            if (_gpuDevices.Count == 0) return "NVIDIA GPU (未检测到)";

            var sb = new StringBuilder();
            foreach (var dev in _gpuDevices)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append($"GPU {dev.Index}: {dev.Name}");
            }
            return sb.ToString();
        }

        public string GetTotalVramStatus()
        {
            if (!_isInitialized) return "NVML 未就绪";

            var sb = new StringBuilder();

            // 1. 显示 NVIDIA 显存状态
            foreach (var (index, handle, _) in _gpuDevices)
            {
                if (NvmlApi.DeviceGetMemoryInfo(handle, out var mem) == NvmlApi.NvmlReturn.Success)
                {
                    double totalGB = mem.total / 1024.0 / 1024.0 / 1024.0;
                    double usedGB = mem.used / 1024.0 / 1024.0 / 1024.0;
                    double percent = (double)mem.used / mem.total * 100.0;

                    if (sb.Length > 0) sb.Append("   |   ");
                    sb.Append($"[GPU {index}] {percent:F1}% ({usedGB:F2} / {totalGB:F2} GB)");
                }
            }

            // 2. [新增] 如果有核显，虽然无法获取精确总显存，但可以追加一个标记表示也在监控它
            if (_fallbackProvider != null && _fallbackProvider.IsSupported)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append("iGPU: 监控中 (WDDM)");
            }

            return sb.ToString();
        }

        public void Refresh()
        {
            // 刷新 WDDM 备用器，这非常重要，因为要靠它发现新启动的 AMD 核显进程
            _fallbackProvider?.Refresh();
        }

        public Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage()
        {
            var result = new Dictionary<uint, (ulong Vram, string Engine)>();

            // [关键修改 1] 先获取 WDDM 的全量数据作为 "底板"
            // WDDM 能看到所有 GPU (N卡 + A卡核显) 的进程
            if (_fallbackProvider != null && _fallbackProvider.IsSupported)
            {
                var wddmData = _fallbackProvider.GetProcessVramUsage();
                foreach (var kvp in wddmData)
                {
                    // 先默认标记为 "GPU (Shared/Other)"，表示可能是核显或 WDDM 通用数据
                    // 如果后面 NVML 没找到这个进程，那它就是纯核显进程
                    result[kvp.Key] = (kvp.Value.Vram, "GPU (Shared/Other)");
                }
            }

            // 辅助字典：PID -> 引擎列表 (用于 NVIDIA 多引擎聚合)
            var engineMap = new Dictionary<uint, HashSet<string>>();

            if (_isInitialized)
            {
                foreach (var (index, handle, _) in _gpuDevices)
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

                                    ulong usage = (info.usedGpuMemory == NvmlApi.NVML_VALUE_NOT_AVAILABLE) ? 0 : info.usedGpuMemory;

                                    // [关键修改 2] 如果 NVML 返回 0，尝试保留 WDDM 里的数据 (如果刚才 WDDM 查到了)
                                    if (usage == 0 && result.TryGetValue(info.pid, out var existingWddm))
                                    {
                                        usage = existingWddm.Vram;
                                    }

                                    string engineStr = $"GPU {index} - 3D";

                                    // 如果这是第一次在 NVML 循环中遇到此 PID (即它已经在 result 里作为 WDDM 数据存在，或者完全是新的)
                                    // 我们需要清除之前的 "GPU (Shared/Other)" 标记，改用精确的 NVIDIA 标记
                                    if (!engineMap.ContainsKey(info.pid))
                                    {
                                        engineMap[info.pid] = new HashSet<string>();
                                        // 重置 VRAM 计数 (如果是首次被 NVML 确认)，以免重复累加 WDDM 的值
                                        // 但要注意，如果这是多卡环境，不能清除其他 NVIDIA 卡的数据。
                                        // 简单策略：NVML 数据通常更准，直接覆盖 WDDM 的条目
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

            // [关键修改 3] 合并 NVML 的引擎名称，保留纯 WDDM 进程 (即 AMD 核显进程)
            // 遍历所有 result，如果是 NVML 处理过的 (在 engineMap 里)，用 engineMap 的名字
            // 如果不在 engineMap 里，说明它是纯核显进程，保留 "GPU (Shared/Other)"
            var keys = result.Keys.ToList(); // 复制 Key 列表以防修改
            foreach (var pid in keys)
            {
                if (engineMap.TryGetValue(pid, out var engines))
                {
                    string engineDisplay = string.Join(", ", engines);
                    result[pid] = (result[pid].Vram, engineDisplay);
                }
                // else: 它不在 engineMap 里，说明 NVML 没看到它 -> 它是 AMD 核显进程 -> 保持原样
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