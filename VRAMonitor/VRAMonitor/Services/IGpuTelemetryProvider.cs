using System;
using System.Collections.Generic;

namespace VRAMonitor.Services
{
    // [新增] 强类型的 GPU 状态信息
    public struct GpuStatusInfo
    {
        public string Name;           // 显卡名称 (e.g. "NVIDIA GeForce RTX 4060")
        public ulong DedicatedUsed;   // 专用显存已用 (Bytes)
        public ulong DedicatedTotal;  // 专用显存总量 (Bytes)
        public ulong SharedUsed;      // 共享显存已用 (Bytes)
        public ulong SharedTotal;     // 共享显存总量 (Bytes)
        public float CoreLoad;        // 核心利用率 (0-100)

        // 辅助判断：是否为核显 
        // 判定法：如果专用显存 < 1GB 且 共享显存 > 专用显存，判定为核显
        // 现代核显（统一内存）通常拥有巨大的共享显存，但只有极少的专用显存（如 128MB-512MB）
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

        // 获取 GPU 数量
        int GpuCount { get; }

        // [修改] 获取显卡名称 (简单字符串，用于标题)
        string GetGpuName();

        // [修改] 获取所有 GPU 的详细状态列表
        List<GpuStatusInfo> GetGpuStatuses();

        // 保持原有的进程获取方法
        Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage();

        void Refresh();
    }
}