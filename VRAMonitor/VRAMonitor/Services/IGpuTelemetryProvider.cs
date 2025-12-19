using System;
using System.Collections.Generic;

namespace VRAMonitor.Services
{
    public interface IGpuTelemetryProvider : IDisposable
    {
        /// <summary>
        /// 提供者的名称（如 "NVML", "WDDM"）
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// 显卡是否可用/支持
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// [新增] 获取被监控的 GPU 数量 (用于状态栏显示)
        /// </summary>
        int GpuCount { get; }

        /// <summary>
        /// 获取显卡名称
        /// </summary>
        string GetGpuName();

        /// <summary>
        /// 获取总体显存状态字符串
        /// </summary>
        string GetTotalVramStatus();

        /// <summary>
        /// [修改] 获取进程显存数据
        /// 返回字典: PID -> (显存大小, 引擎名称字符串)
        /// 引擎名称示例: "GPU 0 - 3D"
        /// </summary>
        Dictionary<uint, (ulong Vram, string Engine)> GetProcessVramUsage();

        /// <summary>
        /// 刷新内部状态
        /// </summary>
        void Refresh();
    }
}