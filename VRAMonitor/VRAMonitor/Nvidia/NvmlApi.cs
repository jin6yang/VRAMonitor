using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace VRAMonitor.Nvidia;

public static class NvmlApi
{
    private const string NvmlDll = "nvml.dll";

    public const ulong NVML_VALUE_NOT_AVAILABLE = ulong.MaxValue;

    #region Enums and Structs

    public enum NvmlReturn
    {
        Success = 0,
        ErrorUninitialized = 1,
        ErrorInvalidArgument = 2,
        ErrorNotSupported = 3,
        ErrorNoPermission = 4,
        ErrorAlreadyInitialized = 5,
        ErrorNotFound = 6,
        ErrorInsufficientSize = 7,
        ErrorInsufficientPower = 8,
        ErrorDriverNotLoaded = 9,
        ErrorTimeout = 10,
        ErrorIrqIssue = 11,
        ErrorLibraryNotFound = 12,
        ErrorFunctionNotFound = 13,
        ErrorCorruptedInfoRom = 14,
        ErrorGpuIsLost = 15,
        ErrorResetRequired = 16,
        ErrorOperatingSystem = 17,
        ErrorLibRMVersionMismatch = 18,
        ErrorInUse = 19,
        ErrorMemory = 20,
        ErrorNoData = 21,
        ErrorVgpuEccNotSupported = 22,
        ErrorUnknown = 999
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlProcessInfo
    {
        public uint pid;
        public ulong usedGpuMemory; // VRAM in bytes
        public uint gpuInstanceId;
        public uint computeInstanceId;
    }

    // [新增] 显存信息结构体
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory
    {
        public ulong total;
        public ulong free;
        public ulong used;
    }

    #endregion

    #region P/Invoke DllImport

    [DllImport(NvmlDll, EntryPoint = "nvmlInit_v2")]
    public static extern NvmlReturn Init();

    [DllImport(NvmlDll, EntryPoint = "nvmlShutdown")]
    public static extern NvmlReturn Shutdown();

    [DllImport(NvmlDll, EntryPoint = "nvmlErrorString")]
    public static extern IntPtr ErrorString(NvmlReturn result);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern NvmlReturn DeviceGetCount(ref uint deviceCount);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern NvmlReturn DeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetName")]
    public static extern NvmlReturn DeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses_v2")]
    public static extern NvmlReturn DeviceGetGraphicsRunningProcesses(IntPtr device, ref uint infoCount, [In, Out] NvmlProcessInfo[] infos);

    [DllImport(NvmlDll, EntryPoint = "nvmlSystemGetProcessName")]
    public static extern NvmlReturn SystemGetProcessName(uint pid, StringBuilder name, uint length);

    // [新增] 获取显存详细信息
    [DllImport(NvmlDll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static extern NvmlReturn DeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    #endregion
}