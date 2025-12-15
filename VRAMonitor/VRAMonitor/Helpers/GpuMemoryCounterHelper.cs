using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;

namespace VRAMonitor.Helpers;

[SupportedOSPlatform("windows")]

public class GpuMemoryCounterHelper
{
    private PerformanceCounterCategory _category;
    private readonly string _categoryName;
    private Dictionary<uint, List<string>> _pidToInstanceMap = new();
    private Dictionary<string, PerformanceCounter> _counters = new();
    private bool _isSupported = false;

    public GpuMemoryCounterHelper()
    {
        // 尝试检测性能计数器类别名称（适配中文和英文系统）
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
    }

    /// <summary>
    /// 刷新当前活动的进程实例映射。建议每隔一段时间（如每次更新循环开始时）调用一次。
    /// </summary>
    public void Refresh()
    {
        if (!_isSupported) return;

        try
        {
            // 获取当前所有实例名称
            var instanceNames = _category.GetInstanceNames();
            _pidToInstanceMap.Clear();

            var activeInstances = new HashSet<string>();

            foreach (var name in instanceNames)
            {
                // 实例名格式通常为: pid_1234_luid_..._phys_0
                // 我们只需要解析出 pid_ 后面的数字
                var parts = name.Split('_');
                if (parts.Length > 1 && parts[0] == "pid" && uint.TryParse(parts[1], out uint pid))
                {
                    if (!_pidToInstanceMap.ContainsKey(pid))
                    {
                        _pidToInstanceMap[pid] = new List<string>();
                    }
                    _pidToInstanceMap[pid].Add(name);
                    activeInstances.Add(name);
                }
            }

            // 清理不再存在的计数器以释放资源
            var deadCounters = _counters.Keys.Except(activeInstances).ToList();
            foreach (var key in deadCounters)
            {
                _counters[key].Dispose();
                _counters.Remove(key);
            }
        }
        catch (Exception)
        {
            // 忽略权限不足或读取期间的瞬时错误
        }
    }

    /// <summary>
    /// 获取指定 PID 的专用显存使用量（字节）
    /// </summary>
    public ulong GetDedicatedVramUsage(uint pid)
    {
        if (!_isSupported || !_pidToInstanceMap.ContainsKey(pid)) return 0;

        ulong totalBytes = 0;

        // 一个进程可能对应多个实例（例如多卡环境或多引擎），我们需要累加
        foreach (var instanceName in _pidToInstanceMap[pid])
        {
            if (!_counters.TryGetValue(instanceName, out var counter))
            {
                try
                {
                    // 创建计数器：Dedicated Usage (专用显存)
                    // 中文系统下计数器名可能不同，通常 "Dedicated Usage" 在 Win10+ 内部是通用的
                    // 如果中文系统下报错，可能需要改为 "专用使用量"
                    // 但根据经验 "Dedicated Usage" 在大多数较新版本的 Windows 上即便是中文版也有效
                    counter = new PerformanceCounter(_categoryName, "Dedicated Usage", instanceName);
                    _counters[instanceName] = counter;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                // 读取数值
                totalBytes += (ulong)counter.NextValue();
            }
            catch { }
        }

        return totalBytes;
    }

    public void Cleanup()
    {
        foreach (var counter in _counters.Values)
        {
            counter.Dispose();
        }
        _counters.Clear();
    }
}