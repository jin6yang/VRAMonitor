using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRAMonitor.Nvidia;

namespace VRAMonitor.Models;

public partial class GpuProcess : ObservableObject
{
    [ObservableProperty]
    private uint _pid;

    [ObservableProperty]
    private string _name = "正在获取...";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private string _publisher = "";

    // [修改] 重命名为 GpuEngine，存储如 "GPU 0 - 3D"
    [ObservableProperty]
    private string _gpuEngine = "";

    [ObservableProperty]
    private ImageSource _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EfficiencyIconVisibility))]
    private bool _isEfficiencyMode;

    public Visibility EfficiencyIconVisibility => _isEfficiencyMode ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VramUsageFormatted))]
    private ulong _vramUsageBytes;

    public string VramUsageFormatted
    {
        get
        {
            if (_vramUsageBytes == NvmlApi.NVML_VALUE_NOT_AVAILABLE)
            {
                return "N/A";
            }
            return $"{_vramUsageBytes / 1024.0 / 1024.0:F2} MB";
        }
    }
}