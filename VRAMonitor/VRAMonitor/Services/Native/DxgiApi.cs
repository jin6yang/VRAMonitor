// 注意！此类(DxgiApi.cs)已被废弃，当前已被GpuInfoHelper.cs代替

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VRAMonitor.Services.Native
{
    public static class DxgiApi
    {
        // [修复] 正确定义 LUID 结构
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;

            // 转换为 long 用于比较和存储
            public long ToInt64()
            {
                return ((long)HighPart << 32) | LowPart;
            }

            public static LUID FromInt64(long value)
            {
                return new LUID
                {
                    LowPart = (uint)(value & 0xFFFFFFFF),
                    HighPart = (int)(value >> 32)
                };
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public IntPtr DedicatedVideoMemory;
            public IntPtr DedicatedSystemMemory;
            public IntPtr SharedSystemMemory;
            public LUID AdapterLuid; // [修复] 使用正确的 LUID 结构
        }

        [ComImport]
        [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIFactory
        {
            [PreserveSig]
            int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        }

        [ComImport]
        [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIAdapter
        {
            [PreserveSig]
            int EnumOutputs(uint output, out IntPtr ppOutput);

            [PreserveSig]
            int GetDesc(out DXGI_ADAPTER_DESC pDesc);
        }

        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

        public static List<DXGI_ADAPTER_DESC> GetAdapters()
        {
            var list = new List<DXGI_ADAPTER_DESC>();
            IntPtr factoryPtr = IntPtr.Zero;

            try
            {
                var iid = typeof(IDXGIFactory).GUID;
                if (CreateDXGIFactory(ref iid, out factoryPtr) != 0) return list;

                var factory = (IDXGIFactory)Marshal.GetObjectForIUnknown(factoryPtr);
                uint i = 0;

                while (true)
                {
                    IntPtr adapterPtr = IntPtr.Zero;
                    if (factory.EnumAdapters(i, out adapterPtr) != 0) break;

                    try
                    {
                        var adapter = (IDXGIAdapter)Marshal.GetObjectForIUnknown(adapterPtr);
                        adapter.GetDesc(out var desc);

                        // 过滤掉 Microsoft Basic Render Driver
                        if (desc.VendorId != 0x1414 && desc.DeviceId != 0x8c)
                        {
                            list.Add(desc);
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                    i++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DXGI GetAdapters error: {ex.Message}");
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            }

            return list;
        }
    }
}