using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams; // 用于 DataReader

namespace VRAMonitor.Helpers;

public static class ProcessInfoHelper
{
    // 常见系统进程的中文友好名称映射
    private static readonly Dictionary<string, string> SystemProcessNames = new()
    {
        { "dwm", "桌面窗口管理器" },
        { "csrss", "客户端服务器运行时进程" },
        { "svchost", "Windows 服务主进程" },
        { "lsass", "本地安全机构进程" },
        { "winlogon", "Windows 登录应用程序" },
        { "explorer", "Windows 资源管理器" },
        { "sihost", "Shell 基础设施主机" },
        { "taskmgr", "任务管理器" },
        { "smss", "Windows 会话管理器" },
        { "services", "服务和控制器应用" },
        { "registry", "注册表" }, 
        { "fontdrvhost", "用户模式字体驱动程序主机" },
        { "ctfmon", "CTF 加载程序" },
        { "searchhost", "搜索主机" },
        { "startmenuexperiencehost", "开始菜单" },
        { "textinputhost", "Windows 文本输入体验" },
        { "tabtip", "触摸键盘和手写面板" } 
    };

    public class ProcessMetadata
    {
        public string FriendlyName { get; set; }
        public string FullPath { get; set; }
        public string Publisher { get; set; }
        
        // [修改] 不再直接持有 UI 对象 ImageSource，改为持有纯数据
        // 这避免了在后台线程创建 UI 对象导致的 RPC_E_WRONG_THREAD 崩溃
        public byte[] IconData { get; set; } 
    }

    /// <summary>
    /// 异步获取进程的详细信息（图标数据、名称、路径等）
    /// </summary>
    public static async Task<ProcessMetadata> GetMetadataAsync(uint pid)
    {
        var metadata = new ProcessMetadata
        {
            FriendlyName = $"PID: {pid}",
            FullPath = "",
            Publisher = "未知发布者",
            IconData = null
        };

        string processName = "";

        try
        {
            var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName; 

            if (!string.IsNullOrEmpty(processName))
            {
                metadata.FriendlyName = processName;
            }
        }
        catch
        {
            return metadata;
        }

        // --- 步骤 1: 处理系统进程名称和默认路径推断 ---
        if (SystemProcessNames.TryGetValue(processName.ToLower(), out string friendlyName))
        {
            metadata.FriendlyName = friendlyName;
            metadata.Publisher = "Microsoft Corporation";

            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string winRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                metadata.FullPath = Path.Combine(winRoot, "explorer.exe");
            }
            else if (processName.Equals("tabtip", StringComparison.OrdinalIgnoreCase))
            {
                string commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                metadata.FullPath = Path.Combine(commonFiles, @"microsoft shared\ink\TabTip.exe");
            }
            else
            {
                metadata.FullPath = Path.Combine(sys32, processName + ".exe");
            }
        }

        // --- 步骤 2: 尝试获取真实路径和元数据 ---
        try
        {
            var process = Process.GetProcessById((int)pid);
            if (process.MainModule != null)
            {
                string realPath = process.MainModule.FileName;
                metadata.FullPath = realPath;

                if (File.Exists(realPath))
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(realPath);

                    if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
                    {
                        metadata.FriendlyName = versionInfo.FileDescription;
                    }

                    if (!string.IsNullOrWhiteSpace(versionInfo.CompanyName))
                    {
                        metadata.Publisher = versionInfo.CompanyName;
                    }
                }
            }
        }
        catch
        {
            if (string.IsNullOrEmpty(metadata.FullPath))
            {
                metadata.FullPath = "N/A (权限不足)";
                if (metadata.FriendlyName.StartsWith("PID:") && !string.IsNullOrEmpty(processName))
                {
                    metadata.FriendlyName = processName;
                }
            }
        }

        // --- 步骤 3: 提取图标数据 (转为 byte[]) ---
        if (!string.IsNullOrEmpty(metadata.FullPath) &&
            !metadata.FullPath.StartsWith("N/A") &&
            File.Exists(metadata.FullPath))
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(metadata.FullPath);
                using (var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32, Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail))
                {
                    if (thumbnail != null)
                    {
                        // 将流转换为 byte[]，这样就可以跨线程安全传递了
                        var bytes = new byte[thumbnail.Size];
                        using (var reader = new DataReader(thumbnail.GetInputStreamAt(0)))
                        {
                            await reader.LoadAsync((uint)thumbnail.Size);
                            reader.ReadBytes(bytes);
                        }
                        metadata.IconData = bytes;
                    }
                }
            }
            catch { }
        }

        return metadata;
    }
}