using System;
using System.Collections.Generic;

namespace VRAMonitor.Services
{
    // [更新] 强类型的 GPU 状态信息，包含统一索引
    public struct GpuStatusInfo
    {
        // [新增] 对应全局统一编号 (GPU 0, GPU 1...)
        public int Index;

        public string Name;           // 显卡名称 (e.g. "NVIDIA GeForce RTX 4060")
        public ulong DedicatedUsed;   // 专用显存已用 (Bytes)
        public ulong DedicatedTotal;  // 专用显存总量 (Bytes)
        public ulong SharedUsed;      // 共享显存已用 (Bytes)
        public ulong SharedTotal;     // 共享显存总量 (Bytes)
        public float CoreLoad;        // 核心利用率 (0-100)

        // 辅助判断：是否为核显 
        public bool IsIntegrated
        {
            get
            {
                // 1GB = 1024 * 1024 * 1024
                ulong oneGb = 1024ul * 1024 * 1024;
                return DedicatedTotal < oneGb && SharedTotal > DedicatedTotal;
            }
        }
    }

    public interface IGpuTelemetryProvider : IDisposable
    {
        string ProviderName { get; }
        bool IsSupported { get; }
        int GpuCount { get; }

        string GetGpuName();
        void Refresh();

        // 获取每个 GPU 的状态列表
        List<GpuStatusInfo> GetGpuStatuses();

        // 获取每个进程的显存占用（Key: PID, Value: (Bytes, EngineName)）
        Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage();
    }
}