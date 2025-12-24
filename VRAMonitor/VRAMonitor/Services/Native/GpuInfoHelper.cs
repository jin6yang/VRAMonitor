using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRAMonitor.Services.Native
{
    /// <summary>
    /// 使用 DXGI (DirectX Graphics Infrastructure) 获取 GPU 信息。
    /// [Final Version] 包含完整的 COM 接口定义、显存读取和统一索引分配。
    /// </summary>
    public static class GpuInfoHelper
    {
        #region DXGI COM Definitions

        [DllImport("dxgi.dll", SetLastError = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

        [ComImport]
        [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            // --- IDXGIObject Methods (4 methods) ---
            void SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            void SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            void GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            void GetParent(ref Guid riid, out IntPtr ppParent);

            // --- IDXGIFactory Methods (5 methods) ---
            [PreserveSig] int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr WindowHandle);
            [PreserveSig] int CreateSwapChain(object pDevice, ref object pDesc, out IntPtr ppSwapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);

            // --- IDXGIFactory1 Methods (2 methods) ---
            [PreserveSig] int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport]
        [Guid("29038f61-3839-4626-91fd-086879011a05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            // --- IDXGIObject Methods (4 methods) ---
            void SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
            void SetPrivateDataInterface(ref Guid Name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            void GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
            void GetParent(ref Guid riid, out IntPtr ppParent);

            // --- IDXGIAdapter Methods (3 methods) ---
            [PreserveSig] int EnumOutputs(uint Output, out IntPtr ppOutput);
            [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC pDesc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid InterfaceName, out long UMDVersion);

            // --- IDXGIAdapter1 Methods (1 method) ---
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long Luid;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long Luid;
            public uint Flags;
        }

        private const uint DXGI_ADAPTER_FLAG_NONE = 0;
        private const uint DXGI_ADAPTER_FLAG_REMOTE = 1;
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        private const int DXGI_ERROR_NOT_FOUND = -2005270526; // 0x887A0002

        #endregion

        public class GpuInfo
        {
            // [关键修改] 统一编号，对应任务管理器的 GPU 0, 1...
            public int Index { get; set; }

            public string Name { get; set; }
            public string HardwareId { get; set; }
            public uint VendorId { get; set; }
            public uint DeviceId { get; set; }
            public long Luid { get; set; } = 0;
            public bool IsSoftware { get; set; }

            // 显存大小 (Bytes)
            public ulong DedicatedMemory { get; set; }
            public ulong SharedMemory { get; set; }
        }

        public static List<GpuInfo> GetAllGpus()
        {
            Debug.WriteLine("[GpuInfoHelper] Start scanning for GPUs via DXGI...");
            var result = new List<GpuInfo>();

            IntPtr pFactory = IntPtr.Zero;
            try
            {
                Guid riid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref riid, out pFactory);

                if (hr != 0 || pFactory == IntPtr.Zero)
                {
                    Debug.WriteLine($"[GpuInfoHelper] CreateDXGIFactory1 failed: 0x{hr:X}");
                    return result;
                }

                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(pFactory);

                // DXGI 底层枚举索引
                uint enumIndex = 0;

                // 逻辑索引 (用于 UI 展示，仅包含有效物理卡)
                int logicalIndex = 0;

                while (true)
                {
                    IDXGIAdapter1 adapter = null;
                    try
                    {
                        hr = factory.EnumAdapters1(enumIndex, out adapter);

                        // 枚举结束
                        if (hr == DXGI_ERROR_NOT_FOUND) break;
                        // 其他错误
                        if (hr != 0 || adapter == null) break;

                        DXGI_ADAPTER_DESC1 desc;
                        if (adapter.GetDesc1(out desc) == 0)
                        {
                            var gpuInfo = new GpuInfo
                            {
                                Name = desc.Description,
                                VendorId = desc.VendorId,
                                DeviceId = desc.DeviceId,
                                Luid = desc.Luid,
                                HardwareId = $"PCI\\VEN_{desc.VendorId:X4}&DEV_{desc.DeviceId:X4}",
                                IsSoftware = (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0,
                                DedicatedMemory = desc.DedicatedVideoMemory.ToUInt64(),
                                SharedMemory = desc.SharedSystemMemory.ToUInt64()
                            };

                            // 过滤逻辑
                            if (IsPhysicalGpu(gpuInfo))
                            {
                                // 分配逻辑索引
                                gpuInfo.Index = logicalIndex++;
                                result.Add(gpuInfo);
                                Debug.WriteLine($"[GpuInfoHelper] Accepted: GPU {gpuInfo.Index} - {gpuInfo.Name} (LUID: 0x{gpuInfo.Luid:X})");
                            }
                            else
                            {
                                Debug.WriteLine($"[GpuInfoHelper] Rejected: {gpuInfo.Name}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GpuInfoHelper] Error at index {enumIndex}: {ex.Message}");
                    }
                    finally
                    {
                        if (adapter != null && Marshal.IsComObject(adapter))
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                    enumIndex++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GpuInfoHelper] DXGI Critical Error: {ex}");
            }
            finally
            {
                if (pFactory != IntPtr.Zero) Marshal.Release(pFactory);
            }

            return result;
        }

        private static bool IsPhysicalGpu(GpuInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.Name)) return false;

            // 1. DXGI 软件标志
            if (info.IsSoftware) return false;

            string nameLower = info.Name.ToLower();

            // 2. 白名单厂商
            bool isKnownVendor =
                info.VendorId == 0x10DE || // NVIDIA
                info.VendorId == 0x1002 || // AMD
                info.VendorId == 0x8086 || // Intel
                info.VendorId == 0x1E36 || // Moore Threads
                info.VendorId == 0x1D17 || // Zhaoxin
                info.VendorId == 0x0014;   // Loongson

            // 3. 黑名单关键字
            string[] blacklist = new[]
            {
                "microsoft basic", "microsoft remote",
                "citrix", "vmware", "virtualbox", "hyper-v",
                "vnc", "teamviewer", "anydesk", "splashtop",
                "chrome remote", "miracast", "spacedesk", "duet display",
                "dummy", "fake display",
                "npu", "neural processing",
                "usb mobile", "virtual adapter", "virtual display", "indirect display",
                "gameviewer"
            };

            foreach (var keyword in blacklist)
            {
                if (nameLower.Contains(keyword)) return false;
            }

            if (info.VendorId == 0x1414) return false; // Microsoft

            // 4. 未知厂商放行策略
            if (!isKnownVendor)
            {
                bool looksLikeGpu = nameLower.Contains("display") || nameLower.Contains("adapter") || nameLower.Contains("gpu") || nameLower.Contains("graphics");
                // 必须有有效的 LUID 才放行
                if (looksLikeGpu && info.Luid != 0) return true;
                return false;
            }

            return true;
        }
    }
}